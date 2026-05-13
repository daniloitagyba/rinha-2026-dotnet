#define _GNU_SOURCE

#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/tcp.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/epoll.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#define BUFFER_SIZE 16384
#define MAX_BACKENDS 8
#define MAX_EVENTS 1024

static const char *backend_paths[MAX_BACKENDS] = {
    "/sockets/api1.sock",
    "/sockets/api2.sock",
};

static const char *control_paths[MAX_BACKENDS] = {
    "/sockets/api1.sock.ctrl",
    "/sockets/api2.sock.ctrl",
};

typedef struct connection connection_t;

typedef struct fd_state {
    int fd;
    int is_client;
    connection_t *connection;
} fd_state_t;

struct connection {
    int client_fd;
    int backend_fd;
    int closed;
    fd_state_t client;
    fd_state_t backend;
    unsigned char c2b[BUFFER_SIZE];
    unsigned char b2c[BUFFER_SIZE];
    size_t c2b_off;
    size_t c2b_len;
    size_t b2c_off;
    size_t b2c_len;
    connection_t *next_closed;
};

static unsigned int next_backend = 0;
static unsigned int backend_count = 2;
static connection_t *closed_head = NULL;
static int control_fds[MAX_BACKENDS];
static char upstreams_storage[1024];

static int set_nonblocking(int fd) {
    int flags = fcntl(fd, F_GETFL, 0);
    if (flags < 0) {
        return -1;
    }

    return fcntl(fd, F_SETFL, flags | O_NONBLOCK);
}

static void set_tcp_nodelay(int fd) {
    int value = 1;
    (void)setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &value, sizeof(value));
}

static int env_enabled(const char *name, int fallback) {
    const char *value = getenv(name);
    if (value == NULL || *value == '\0') {
        return fallback;
    }

    return strcmp(value, "0") != 0 &&
           strcmp(value, "false") != 0 &&
           strcmp(value, "FALSE") != 0 &&
           strcmp(value, "no") != 0 &&
           strcmp(value, "NO") != 0;
}

static void set_small_socket_buffers(int fd) {
    int value = 16384;
    (void)setsockopt(fd, SOL_SOCKET, SO_RCVBUF, &value, sizeof(value));
    (void)setsockopt(fd, SOL_SOCKET, SO_SNDBUF, &value, sizeof(value));
}

static unsigned int choose_backend(void) {
    unsigned int backend = next_backend;
    next_backend = (next_backend + 1) % backend_count;
    return backend;
}

static int connect_backend(unsigned int start) {
    for (unsigned int attempt = 0; attempt < backend_count; attempt++) {
        unsigned int index = (start + attempt) % backend_count;
        int fd = socket(AF_UNIX, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
        if (fd < 0) {
            continue;
        }

        struct sockaddr_un addr;
        memset(&addr, 0, sizeof(addr));
        addr.sun_family = AF_UNIX;
        strncpy(addr.sun_path, backend_paths[index], sizeof(addr.sun_path) - 1);

        if (connect(fd, (struct sockaddr *)&addr, sizeof(addr)) == 0) {
            set_small_socket_buffers(fd);
            return fd;
        }

        close(fd);
    }

    return -1;
}

static int flush_buffer(int fd, unsigned char *buffer, size_t *offset, size_t *length) {
    while (*offset < *length) {
        ssize_t written = send(fd, buffer + *offset, *length - *offset, MSG_NOSIGNAL);
        if (written > 0) {
            *offset += (size_t)written;
            continue;
        }

        if (written < 0 && errno == EINTR) {
            continue;
        }

        if (written < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) {
            return 0;
        }

        return -1;
    }

    *offset = 0;
    *length = 0;
    return 0;
}

static int pump_direction(int src_fd, int dst_fd, unsigned char *buffer, size_t *offset, size_t *length) {
    for (;;) {
        if (*offset < *length) {
            if (flush_buffer(dst_fd, buffer, offset, length) < 0) {
                return -1;
            }

            if (*offset < *length) {
                return 0;
            }
        }

        ssize_t got = recv(src_fd, buffer, BUFFER_SIZE, 0);
        if (got > 0) {
            *offset = 0;
            *length = (size_t)got;
            continue;
        }

        if (got == 0) {
            return -1;
        }

        if (errno == EINTR) {
            continue;
        }

        if (errno == EAGAIN || errno == EWOULDBLOCK) {
            return 0;
        }

        return -1;
    }
}

static void schedule_close(int epoll_fd, connection_t *conn) {
    if (conn == NULL || conn->closed) {
        return;
    }

    conn->closed = 1;
    (void)epoll_ctl(epoll_fd, EPOLL_CTL_DEL, conn->client_fd, NULL);
    (void)epoll_ctl(epoll_fd, EPOLL_CTL_DEL, conn->backend_fd, NULL);
    close(conn->client_fd);
    close(conn->backend_fd);

    conn->next_closed = closed_head;
    closed_head = conn;
}

static void reap_closed(void) {
    connection_t *conn = closed_head;
    closed_head = NULL;

    while (conn != NULL) {
        connection_t *next = conn->next_closed;
        free(conn);
        conn = next;
    }
}

static int fd_events(const fd_state_t *state) {
    const connection_t *conn = state->connection;
    int events = EPOLLERR | EPOLLHUP | EPOLLRDHUP;

    if (state->is_client) {
        if (conn->c2b_len == 0) {
            events |= EPOLLIN;
        }

        if (conn->b2c_len > conn->b2c_off) {
            events |= EPOLLOUT;
        }
    } else {
        if (conn->b2c_len == 0) {
            events |= EPOLLIN;
        }

        if (conn->c2b_len > conn->c2b_off) {
            events |= EPOLLOUT;
        }
    }

    return events;
}

static int connect_control(unsigned int index) {
    int fd = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (fd < 0) {
        return -1;
    }

    struct sockaddr_un addr;
    memset(&addr, 0, sizeof(addr));
    addr.sun_family = AF_UNIX;
    strncpy(addr.sun_path, control_paths[index], sizeof(addr.sun_path) - 1);

    if (connect(fd, (struct sockaddr *)&addr, sizeof(addr)) == 0) {
        return fd;
    }

    close(fd);
    return -1;
}

static int ensure_control(unsigned int index) {
    if (control_fds[index] >= 0) {
        return control_fds[index];
    }

    int fd = connect_control(index);
    control_fds[index] = fd;
    return fd;
}

static int send_fd(int control_fd, int fd_to_send) {
    char data = 0;
    struct iovec io;
    io.iov_base = &data;
    io.iov_len = 1;

    char cmsgbuf[CMSG_SPACE(sizeof(int))];
    memset(cmsgbuf, 0, sizeof(cmsgbuf));

    struct msghdr msg;
    memset(&msg, 0, sizeof(msg));
    msg.msg_iov = &io;
    msg.msg_iovlen = 1;
    msg.msg_control = cmsgbuf;
    msg.msg_controllen = sizeof(cmsgbuf);

    struct cmsghdr *cmsg = CMSG_FIRSTHDR(&msg);
    cmsg->cmsg_level = SOL_SOCKET;
    cmsg->cmsg_type = SCM_RIGHTS;
    cmsg->cmsg_len = CMSG_LEN(sizeof(int));
    memcpy(CMSG_DATA(cmsg), &fd_to_send, sizeof(int));
    msg.msg_controllen = cmsg->cmsg_len;

    for (;;) {
        ssize_t sent = sendmsg(control_fd, &msg, MSG_NOSIGNAL);
        if (sent == 1) {
            return 0;
        }

        if (sent < 0 && errno == EINTR) {
            continue;
        }

        return -1;
    }
}

static int update_fd(int epoll_fd, fd_state_t *state) {
    struct epoll_event event;
    memset(&event, 0, sizeof(event));
    event.events = fd_events(state);
    event.data.ptr = state;
    return epoll_ctl(epoll_fd, EPOLL_CTL_MOD, state->fd, &event);
}

static int add_fd(int epoll_fd, fd_state_t *state) {
    struct epoll_event event;
    memset(&event, 0, sizeof(event));
    event.events = fd_events(state);
    event.data.ptr = state;
    return epoll_ctl(epoll_fd, EPOLL_CTL_ADD, state->fd, &event);
}

static int update_connection(int epoll_fd, connection_t *conn) {
    if (update_fd(epoll_fd, &conn->client) < 0) {
        return -1;
    }

    if (update_fd(epoll_fd, &conn->backend) < 0) {
        return -1;
    }

    return 0;
}

static void accept_clients(int epoll_fd, int listener) {
    for (;;) {
        int client_fd = accept4(listener, NULL, NULL, SOCK_NONBLOCK | SOCK_CLOEXEC);
        if (client_fd < 0) {
            if (errno == EINTR) {
                continue;
            }

            if (errno == EAGAIN || errno == EWOULDBLOCK) {
                return;
            }

            perror("accept");
            return;
        }

        set_tcp_nodelay(client_fd);
        set_small_socket_buffers(client_fd);

        int backend_fd = connect_backend(choose_backend());
        if (backend_fd < 0) {
            close(client_fd);
            continue;
        }

        connection_t *conn = calloc(1, sizeof(connection_t));
        if (conn == NULL) {
            close(client_fd);
            close(backend_fd);
            continue;
        }

        conn->client_fd = client_fd;
        conn->backend_fd = backend_fd;
        conn->client.fd = client_fd;
        conn->client.is_client = 1;
        conn->client.connection = conn;
        conn->backend.fd = backend_fd;
        conn->backend.is_client = 0;
        conn->backend.connection = conn;

        if (add_fd(epoll_fd, &conn->client) < 0 || add_fd(epoll_fd, &conn->backend) < 0) {
            schedule_close(epoll_fd, conn);
        }
    }
}

static void accept_clients_fdpass(int listener) {
    for (;;) {
        int client_fd = accept4(listener, NULL, NULL, SOCK_CLOEXEC);
        if (client_fd < 0) {
            if (errno == EINTR) {
                continue;
            }

            if (errno == EAGAIN || errno == EWOULDBLOCK) {
                return;
            }

            perror("accept");
            return;
        }

        set_tcp_nodelay(client_fd);
        set_small_socket_buffers(client_fd);

        unsigned int start = choose_backend();
        int delivered = 0;
        for (unsigned int attempt = 0; attempt < backend_count; attempt++) {
            unsigned int index = (start + attempt) % backend_count;
            int control_fd = ensure_control(index);
            if (control_fd >= 0 && send_fd(control_fd, client_fd) == 0) {
                delivered = 1;
                break;
            }

            if (control_fds[index] >= 0) {
                close(control_fds[index]);
                control_fds[index] = -1;
            }
        }

        close(client_fd);
        if (!delivered) {
            continue;
        }
    }
}

static int run_fdpass(int listener, int port) {
    int epoll_fd = epoll_create1(EPOLL_CLOEXEC);
    if (epoll_fd < 0) {
        perror("epoll_create1");
        close(listener);
        return 1;
    }

    fd_state_t listener_state;
    memset(&listener_state, 0, sizeof(listener_state));
    listener_state.fd = listener;
    listener_state.is_client = -1;

    struct epoll_event listener_event;
    memset(&listener_event, 0, sizeof(listener_event));
    listener_event.events = EPOLLIN;
    listener_event.data.ptr = &listener_state;
    if (epoll_ctl(epoll_fd, EPOLL_CTL_ADD, listener, &listener_event) < 0) {
        perror("epoll_ctl");
        close(listener);
        close(epoll_fd);
        return 1;
    }

    fprintf(stderr, "serving fdpass load balancer on 0.0.0.0:%d\n", port);

    struct epoll_event events[MAX_EVENTS];
    for (;;) {
        int ready = epoll_wait(epoll_fd, events, MAX_EVENTS, -1);
        if (ready < 0) {
            if (errno == EINTR) {
                continue;
            }

            perror("epoll_wait");
            break;
        }

        for (int i = 0; i < ready; i++) {
            if (events[i].data.ptr == &listener_state) {
                accept_clients_fdpass(listener);
            }
        }
    }

    close(listener);
    close(epoll_fd);
    for (unsigned int i = 0; i < backend_count; i++) {
        if (control_fds[i] >= 0) {
            close(control_fds[i]);
            control_fds[i] = -1;
        }
    }

    return 1;
}

static void init_backends(int fdpass_mode) {
    for (int i = 0; i < MAX_BACKENDS; i++) {
        control_fds[i] = -1;
    }

    const char *upstreams = getenv("UPSTREAMS");
    if (upstreams == NULL || *upstreams == '\0') {
        return;
    }

    strncpy(upstreams_storage, upstreams, sizeof(upstreams_storage) - 1);
    upstreams_storage[sizeof(upstreams_storage) - 1] = '\0';

    unsigned int count = 0;
    char *save = NULL;
    char *item = strtok_r(upstreams_storage, ",", &save);
    while (item != NULL && count < MAX_BACKENDS) {
        while (*item == ' ' || *item == '\t') {
            item++;
        }

        char *end = item + strlen(item);
        while (end > item && (end[-1] == ' ' || end[-1] == '\t' || end[-1] == '\n' || end[-1] == '\r')) {
            *--end = '\0';
        }

        if (*item != '\0') {
            if (fdpass_mode) {
                control_paths[count] = item;
            } else {
                backend_paths[count] = item;
            }

            count++;
        }

        item = strtok_r(NULL, ",", &save);
    }

    if (count > 0) {
        backend_count = count;
    }
}

static void process_proxy_event(int epoll_fd, fd_state_t *state, uint32_t events) {
    connection_t *conn = state->connection;
    if (conn->closed) {
        return;
    }

    int close_connection = 0;

    if (state->is_client) {
        if ((events & EPOLLOUT) && conn->b2c_len > conn->b2c_off) {
            if (flush_buffer(conn->client_fd, conn->b2c, &conn->b2c_off, &conn->b2c_len) < 0) {
                close_connection = 1;
            }
        }

        if (!close_connection && (events & EPOLLIN) && conn->c2b_len == 0) {
            if (pump_direction(conn->client_fd, conn->backend_fd, conn->c2b, &conn->c2b_off, &conn->c2b_len) < 0) {
                close_connection = 1;
            }
        }

        if ((events & EPOLLRDHUP) && conn->c2b_len == 0) {
            close_connection = 1;
        }
    } else {
        if ((events & EPOLLOUT) && conn->c2b_len > conn->c2b_off) {
            if (flush_buffer(conn->backend_fd, conn->c2b, &conn->c2b_off, &conn->c2b_len) < 0) {
                close_connection = 1;
            }
        }

        if (!close_connection && (events & EPOLLIN) && conn->b2c_len == 0) {
            if (pump_direction(conn->backend_fd, conn->client_fd, conn->b2c, &conn->b2c_off, &conn->b2c_len) < 0) {
                close_connection = 1;
            }
        }

        if ((events & EPOLLRDHUP) && conn->b2c_len == 0) {
            close_connection = 1;
        }
    }

    if (events & (EPOLLERR | EPOLLHUP)) {
        close_connection = 1;
    }

    if (close_connection) {
        schedule_close(epoll_fd, conn);
        return;
    }

    if (update_connection(epoll_fd, conn) < 0) {
        schedule_close(epoll_fd, conn);
    }
}

static int create_listener(int port) {
    int fd = socket(AF_INET, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
    if (fd < 0) {
        return -1;
    }

    int value = 1;
    (void)setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &value, sizeof(value));
    if (env_enabled("TCP_DEFER_ACCEPT", 0)) {
        value = 1;
        (void)setsockopt(fd, IPPROTO_TCP, TCP_DEFER_ACCEPT, &value, sizeof(value));
    }

    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_ANY);
    addr.sin_port = htons((uint16_t)port);

    if (bind(fd, (struct sockaddr *)&addr, sizeof(addr)) < 0) {
        close(fd);
        return -1;
    }

    if (listen(fd, 4096) < 0) {
        close(fd);
        return -1;
    }

    return fd;
}

int main(void) {
    signal(SIGPIPE, SIG_IGN);

    int port = 9999;
    const char *port_env = getenv("LB_PORT");
    if (port_env != NULL && *port_env != '\0') {
        port = atoi(port_env);
    }

    int listener = create_listener(port);
    if (listener < 0) {
        perror("listen");
        return 1;
    }

    const char *mode = getenv("LB_MODE");
    int fdpass_mode = mode != NULL && strcmp(mode, "fdpass") == 0;
    init_backends(fdpass_mode);

    if (fdpass_mode) {
        return run_fdpass(listener, port);
    }

    int epoll_fd = epoll_create1(EPOLL_CLOEXEC);
    if (epoll_fd < 0) {
        perror("epoll_create1");
        close(listener);
        return 1;
    }

    fd_state_t listener_state;
    memset(&listener_state, 0, sizeof(listener_state));
    listener_state.fd = listener;
    listener_state.is_client = -1;

    struct epoll_event listener_event;
    memset(&listener_event, 0, sizeof(listener_event));
    listener_event.events = EPOLLIN;
    listener_event.data.ptr = &listener_state;
    if (epoll_ctl(epoll_fd, EPOLL_CTL_ADD, listener, &listener_event) < 0) {
        perror("epoll_ctl");
        close(listener);
        close(epoll_fd);
        return 1;
    }

    fprintf(stderr, "serving epoll tcp proxy on 0.0.0.0:%d\n", port);

    struct epoll_event events[MAX_EVENTS];
    for (;;) {
        int ready = epoll_wait(epoll_fd, events, MAX_EVENTS, -1);
        if (ready < 0) {
            if (errno == EINTR) {
                continue;
            }

            perror("epoll_wait");
            break;
        }

        for (int i = 0; i < ready; i++) {
            if (events[i].data.ptr == &listener_state) {
                accept_clients(epoll_fd, listener);
            } else {
                process_proxy_event(epoll_fd, (fd_state_t *)events[i].data.ptr, events[i].events);
            }
        }

        reap_closed();
    }

    close(listener);
    close(epoll_fd);
    return 1;
}
