#define _GNU_SOURCE

#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/tcp.h>
#include <poll.h>
#include <pthread.h>
#include <signal.h>
#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#define BUFFER_SIZE 8192
#define BACKEND_COUNT 2

static const char *backend_paths[BACKEND_COUNT] = {
    "/sockets/api1.sock",
    "/sockets/api2.sock",
};

static atomic_uint next_backend = 0;
typedef struct connection {
    int client_fd;
    int backend_fd;
    unsigned char c2b[BUFFER_SIZE];
    unsigned char b2c[BUFFER_SIZE];
    size_t c2b_off;
    size_t c2b_len;
    size_t b2c_off;
    size_t b2c_len;
} connection_t;

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

static int connect_backend(unsigned int start, unsigned int *selected_index) {
    for (unsigned int attempt = 0; attempt < BACKEND_COUNT; attempt++) {
        unsigned int index = (start + attempt) % BACKEND_COUNT;
        int fd = socket(AF_UNIX, SOCK_STREAM, 0);
        if (fd < 0) {
            continue;
        }

        struct sockaddr_un addr;
        memset(&addr, 0, sizeof(addr));
        addr.sun_family = AF_UNIX;
        strncpy(addr.sun_path, backend_paths[index], sizeof(addr.sun_path) - 1);

        if (connect(fd, (struct sockaddr *)&addr, sizeof(addr)) == 0) {
            *selected_index = index;
            return fd;
        }

        close(fd);
    }

    return -1;
}

static int flush_buffer(int fd, unsigned char *buffer, size_t *offset, size_t *length) {
    while (*offset < *length) {
        ssize_t written = write(fd, buffer + *offset, *length - *offset);
        if (written > 0) {
            *offset += (size_t)written;
            continue;
        }

        if (written < 0 && (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR)) {
            return 0;
        }

        return -1;
    }

    *offset = 0;
    *length = 0;
    return 0;
}

static int fill_buffer(int fd, unsigned char *buffer, size_t *length) {
    ssize_t got = read(fd, buffer, BUFFER_SIZE);
    if (got > 0) {
        *length = (size_t)got;
        return 1;
    }

    if (got == 0) {
        return -1;
    }

    if (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR) {
        return 0;
    }

    return -1;
}

static void *proxy_connection(void *state) {
    connection_t *conn = (connection_t *)state;
    set_nonblocking(conn->client_fd);
    set_nonblocking(conn->backend_fd);

    for (;;) {
        struct pollfd fds[2];
        fds[0].fd = conn->client_fd;
        fds[0].events = 0;
        fds[1].fd = conn->backend_fd;
        fds[1].events = 0;

        if (conn->c2b_len == 0) {
            fds[0].events |= POLLIN;
        }
        if (conn->b2c_len > 0) {
            fds[0].events |= POLLOUT;
        }
        if (conn->b2c_len == 0) {
            fds[1].events |= POLLIN;
        }
        if (conn->c2b_len > 0) {
            fds[1].events |= POLLOUT;
        }

        int ready = poll(fds, 2, 5000);
        if (ready <= 0) {
            break;
        }

        if ((fds[0].revents & (POLLERR | POLLNVAL)) || (fds[1].revents & (POLLERR | POLLNVAL))) {
            break;
        }

        if ((fds[0].revents & POLLOUT) && conn->b2c_len > 0) {
            if (flush_buffer(conn->client_fd, conn->b2c, &conn->b2c_off, &conn->b2c_len) < 0) {
                break;
            }
        }

        if ((fds[1].revents & POLLOUT) && conn->c2b_len > 0) {
            if (flush_buffer(conn->backend_fd, conn->c2b, &conn->c2b_off, &conn->c2b_len) < 0) {
                break;
            }
        }

        if ((fds[0].revents & POLLIN) && conn->c2b_len == 0) {
            if (fill_buffer(conn->client_fd, conn->c2b, &conn->c2b_len) < 0) {
                break;
            }
        }

        if ((fds[1].revents & POLLIN) && conn->b2c_len == 0) {
            if (fill_buffer(conn->backend_fd, conn->b2c, &conn->b2c_len) < 0) {
                break;
            }
        }

        if ((fds[0].revents & POLLHUP) && conn->c2b_len == 0) {
            break;
        }

        if ((fds[1].revents & POLLHUP) && conn->b2c_len == 0) {
            break;
        }
    }

    close(conn->client_fd);
    close(conn->backend_fd);
    free(conn);
    return NULL;
}

static unsigned int choose_backend(void) {
    return atomic_fetch_add_explicit(&next_backend, 1, memory_order_relaxed) % BACKEND_COUNT;
}

static int create_listener(int port) {
    int fd = socket(AF_INET, SOCK_STREAM, 0);
    if (fd < 0) {
        return -1;
    }

    int value = 1;
    (void)setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &value, sizeof(value));

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

    pthread_attr_t attr;
    pthread_attr_init(&attr);
    pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED);
    pthread_attr_setstacksize(&attr, 64 * 1024);

    fprintf(stderr, "serving tcp proxy on 0.0.0.0:%d\n", port);
    for (;;) {
        int client_fd = accept4(listener, NULL, NULL, SOCK_CLOEXEC);
        if (client_fd < 0) {
            if (errno == EINTR) {
                continue;
            }
            perror("accept");
            continue;
        }

        set_tcp_nodelay(client_fd);
        unsigned int backend = choose_backend();
        unsigned int backend_index = backend;
        int backend_fd = connect_backend(backend, &backend_index);
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

        pthread_t thread;
        if (pthread_create(&thread, &attr, proxy_connection, conn) != 0) {
            close(client_fd);
            close(backend_fd);
            free(conn);
        }
    }
}
