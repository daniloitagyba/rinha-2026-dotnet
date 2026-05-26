#define _GNU_SOURCE

#include <errno.h>
#include <fcntl.h>
#include <pthread.h>
#include <signal.h>
#include <sched.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <sys/epoll.h>
#include <sys/mman.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <unistd.h>

#ifndef REQUEST_BUFFER_SIZE
#define REQUEST_BUFFER_SIZE 8192
#endif
#define QUEUE_CAPACITY 4096
#ifndef MAX_EVENTS
#define MAX_EVENTS 1024
#endif
#define KD_PARTITION_COUNT 256
#define KD_PARTITION_RECORD_SIZE 72
#define KD_NODE_RECORD_SIZE 80
#define KD_VECTOR_STRIDE 16
#define PROFILE_KEY_COUNT (1 << 22)

#define SECTION_PROFILE_COUNTS 1
#define SECTION_PROFILE_MASKS 2
#define SECTION_KD_META 15
#define SECTION_KD_PARTITIONS 16
#define SECTION_KD_NODES 17
#define SECTION_KD_VECTORS 18
#define SECTION_KD_LABELS 19
#define SECTION_KD_IDS 20

extern int32_t rinha_classify_json_kdtree_avx2(
    const uint8_t *partitions,
    const uint8_t *nodes,
    const int16_t *vectors,
    const uint8_t *labels,
    const int32_t *ids,
    const uint8_t *body,
    int32_t body_length,
    int32_t node_count,
    int32_t max_partitions);

extern int32_t rinha_classify_json_profile_kdtree_avx2(
    const uint8_t *partitions,
    const uint8_t *nodes,
    const int16_t *vectors,
    const uint8_t *labels,
    const int32_t *ids,
    const uint16_t *profile_counts,
    const uint8_t *profile_masks,
    const uint8_t *body,
    int32_t body_length,
    int32_t node_count,
    int32_t max_partitions,
    int32_t profile_fastpath,
    int32_t profile_legit_min_count,
    int32_t profile_fraud_min_count,
    int32_t profile_fraud_amount_min,
    int32_t profile_fraud_no_last_only);

typedef struct {
    uint8_t *base;
    size_t length;
    const uint16_t *profile_counts;
    const uint8_t *profile_masks;
    const uint8_t *partitions;
    const uint8_t *nodes;
    const int16_t *vectors;
    const uint8_t *labels;
    const int32_t *ids;
    int32_t node_count;
    int32_t max_partitions;
} native_index_t;

typedef struct {
    int fds[QUEUE_CAPACITY];
    int head;
    int tail;
    int count;
    pthread_mutex_t mutex;
    pthread_cond_t cond;
} fd_queue_t;

typedef struct {
    int type;
    int fd;
    int used;
    uint8_t buffer[REQUEST_BUFFER_SIZE];
} epoll_state_t;

static const uint8_t ready_response[] = "HTTP/1.1 200\r\nContent-Length:2\r\n\r\nOK";
static const uint8_t not_found_response[] = "HTTP/1.1 404 Not Found\r\nContent-Length:9\r\n\r\nnot found";
static const uint8_t approved00_response[] = "HTTP/1.1 200\r\nContent-Length:35\r\n\r\n{\"approved\":true,\"fraud_score\":0.0}";
static const uint8_t approved02_response[] = "HTTP/1.1 200\r\nContent-Length:35\r\n\r\n{\"approved\":true,\"fraud_score\":0.2}";
static const uint8_t approved04_response[] = "HTTP/1.1 200\r\nContent-Length:35\r\n\r\n{\"approved\":true,\"fraud_score\":0.4}";
static const uint8_t denied06_response[] = "HTTP/1.1 200\r\nContent-Length:36\r\n\r\n{\"approved\":false,\"fraud_score\":0.6}";
static const uint8_t denied08_response[] = "HTTP/1.1 200\r\nContent-Length:36\r\n\r\n{\"approved\":false,\"fraud_score\":0.8}";
static const uint8_t denied10_response[] = "HTTP/1.1 200\r\nContent-Length:36\r\n\r\n{\"approved\":false,\"fraud_score\":1.0}";

static native_index_t g_index;
static int g_profile_fastpath = 0;
static int g_profile_legit_min_count = 300;
static int g_profile_fraud_min_count = 100000;
static int g_profile_fraud_amount_min = 0;
static int g_profile_fraud_no_last_only = 0;
static int g_fd_immediate_read = 0;
static int g_epoll_et = 0;
static int g_fd_pre_read = 0;
static int g_assume_body_complete = 0;
static int g_assume_json_body_start = 0;
static int g_fd_control_prebuffer = 0;
static fd_queue_t g_queue = {
    .head = 0,
    .tail = 0,
    .count = 0,
    .mutex = PTHREAD_MUTEX_INITIALIZER,
    .cond = PTHREAD_COND_INITIALIZER,
};

enum {
    EPOLL_STATE_LISTENER = 1,
    EPOLL_STATE_CONTROL = 2,
    EPOLL_STATE_CLIENT = 3,
};

static uint32_t rd32(const uint8_t *p) {
    return (uint32_t)p[0] |
           ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) |
           ((uint32_t)p[3] << 24);
}

static uint64_t rd64(const uint8_t *p) {
    return (uint64_t)rd32(p) | ((uint64_t)rd32(p + 4) << 32);
}

static int env_int(const char *name, int fallback) {
    const char *value = getenv(name);
    if (value == NULL || *value == '\0') {
        return fallback;
    }

    char *end = NULL;
    long parsed = strtol(value, &end, 10);
    if (end == value) {
        return fallback;
    }

    if (parsed < INT32_MIN) {
        return INT32_MIN;
    }

    if (parsed > INT32_MAX) {
        return INT32_MAX;
    }

    return (int)parsed;
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

static void pin_first_allowed_cpu(void) {
    cpu_set_t current;
    if (sched_getaffinity(0, sizeof(current), &current) != 0) {
        return;
    }

    for (int cpu = 0; cpu < CPU_SETSIZE; cpu++) {
        if (CPU_ISSET(cpu, &current)) {
            cpu_set_t pinned;
            CPU_ZERO(&pinned);
            CPU_SET(cpu, &pinned);
            (void)sched_setaffinity(0, sizeof(pinned), &pinned);
            return;
        }
    }
}

static int set_nonblocking(int fd) {
    int flags = fcntl(fd, F_GETFL, 0);
    if (flags < 0) {
        return -1;
    }

    return fcntl(fd, F_SETFL, flags | O_NONBLOCK);
}

static void configure_client_fd(int fd) {
    (void)fd;
}

static int find_header_end(const uint8_t *buffer, int length) {
    for (int i = 3; i < length; i++) {
        if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' &&
            buffer[i - 1] == '\r' && buffer[i] == '\n') {
            return i - 3;
        }
    }

    return -1;
}

static int parse_positive_int(const uint8_t *data, int length) {
    int value = 0;
    int seen = 0;
    for (int i = 0; i < length; i++) {
        uint8_t b = data[i];
        if (b == ' ' || b == '\t') {
            if (seen) {
                break;
            }

            continue;
        }

        uint8_t d = (uint8_t)(b - '0');
        if (d > 9) {
            break;
        }

        seen = 1;
        value = value * 10 + (int)d;
    }

    return value;
}

static int ascii_lower(uint8_t b) {
    return (b >= 'A' && b <= 'Z') ? b + 32 : b;
}

static int content_length(const uint8_t *headers, int length) {
    static const char key[] = "content-length:";
    int key_length = (int)sizeof(key) - 1;
    static const char exact_key[] = "Content-Length:";
    int exact_key_length = (int)sizeof(exact_key) - 1;

    if (length >= exact_key_length && memcmp(headers, exact_key, (size_t)exact_key_length) == 0) {
        return parse_positive_int(headers + exact_key_length, length - exact_key_length);
    }

    for (int i = 0; i + exact_key_length + 2 <= length; i++) {
        if (headers[i] == '\r' &&
            headers[i + 1] == '\n' &&
            memcmp(headers + i + 2, exact_key, (size_t)exact_key_length) == 0) {
            return parse_positive_int(headers + i + 2 + exact_key_length, length - i - 2 - exact_key_length);
        }
    }

    for (int i = 0; i + key_length <= length; i++) {
        if (i != 0 && !(headers[i - 2] == '\r' && headers[i - 1] == '\n')) {
            continue;
        }

        int matched = 1;
        for (int j = 0; j < key_length; j++) {
            if (ascii_lower(headers[i + j]) != key[j]) {
                matched = 0;
                break;
            }
        }

        if (matched) {
            return parse_positive_int(headers + i + key_length, length - i - key_length);
        }
    }

    return 0;
}

static void send_all(int fd, const uint8_t *data, size_t length) {
    size_t offset = 0;
    while (offset < length) {
        ssize_t sent = send(fd, data + offset, length - offset, MSG_NOSIGNAL);
        if (sent > 0) {
            offset += (size_t)sent;
            continue;
        }

        if (sent < 0 && errno == EINTR) {
            continue;
        }

        return;
    }
}

static void send_decision(int fd, int fraud_count) {
    if (fraud_count <= 0) {
        send_all(fd, approved00_response, sizeof(approved00_response) - 1);
    } else if (fraud_count == 1) {
        send_all(fd, approved02_response, sizeof(approved02_response) - 1);
    } else if (fraud_count == 2) {
        send_all(fd, approved04_response, sizeof(approved04_response) - 1);
    } else if (fraud_count == 3) {
        send_all(fd, denied06_response, sizeof(denied06_response) - 1);
    } else if (fraud_count == 4) {
        send_all(fd, denied08_response, sizeof(denied08_response) - 1);
    } else {
        send_all(fd, denied10_response, sizeof(denied10_response) - 1);
    }
}

static int request_complete(const uint8_t *buffer, int used, int *header_end, int *body_length) {
    if (g_assume_json_body_start &&
        used >= 18 &&
        memcmp(buffer, "POST /fraud-score", 17) == 0 &&
        (buffer[17] == ' ' || buffer[17] == '?')) {
        const uint8_t *body = memchr(buffer, '{', (size_t)used);
        if (body != NULL) {
            *header_end = (int)(body - buffer) - 4;
            *body_length = used - (int)(body - buffer);
            return 1;
        }
    }

    *header_end = find_header_end(buffer, used);
    *body_length = 0;
    if (*header_end < 0) {
        return 0;
    }

    if (g_assume_body_complete) {
        *body_length = used - *header_end - 4;
        return *body_length >= 0;
    }

    *body_length = content_length(buffer, *header_end + 4);
    return used >= *header_end + 4 + *body_length;
}

static void handle_request(int fd, const uint8_t *buffer, int used, int header_end, int body_length) {
    if (used < 18 ||
        memcmp(buffer, "POST /fraud-score", 17) != 0 ||
        (buffer[17] != ' ' && buffer[17] != '?')) {
        if (used >= 11 &&
            memcmp(buffer, "GET /ready", 10) == 0 &&
            (buffer[10] == ' ' || buffer[10] == '?')) {
            send_all(fd, ready_response, sizeof(ready_response) - 1);
        } else {
            send_all(fd, not_found_response, sizeof(not_found_response) - 1);
        }
        return;
    }

    int body_start = header_end + 4;
    int fraud_count = rinha_classify_json_profile_kdtree_avx2(
        g_index.partitions,
        g_index.nodes,
        g_index.vectors,
        g_index.labels,
        g_index.ids,
        g_index.profile_counts,
        g_index.profile_masks,
        buffer + body_start,
        body_length,
        g_index.node_count,
        g_index.max_partitions,
        g_profile_fastpath,
        g_profile_legit_min_count,
        g_profile_fraud_min_count,
        g_profile_fraud_amount_min,
        g_profile_fraud_no_last_only);

    send_decision(fd, fraud_count < 0 ? 0 : fraud_count);
}

static void handle_connection(int fd) {
    uint8_t buffer[REQUEST_BUFFER_SIZE];
    int used = 0;

    for (;;) {
        int header_end = 0;
        int body_length = 0;
        while (!request_complete(buffer, used, &header_end, &body_length)) {
            if (used >= REQUEST_BUFFER_SIZE) {
                send_all(fd, approved00_response, sizeof(approved00_response) - 1);
                close(fd);
                return;
            }

            ssize_t got = recv(fd, buffer + used, (size_t)(REQUEST_BUFFER_SIZE - used), 0);
            if (got > 0) {
                used += (int)got;
                continue;
            }

            if (got < 0 && errno == EINTR) {
                continue;
            }

            close(fd);
            return;
        }

        handle_request(fd, buffer, used, header_end, body_length);
        used = 0;
    }
}

static void queue_push(int fd) {
    pthread_mutex_lock(&g_queue.mutex);
    if (g_queue.count == QUEUE_CAPACITY) {
        pthread_mutex_unlock(&g_queue.mutex);
        close(fd);
        return;
    }

    g_queue.fds[g_queue.tail] = fd;
    g_queue.tail = (g_queue.tail + 1) % QUEUE_CAPACITY;
    g_queue.count++;
    pthread_cond_signal(&g_queue.cond);
    pthread_mutex_unlock(&g_queue.mutex);
}

static int queue_pop(void) {
    pthread_mutex_lock(&g_queue.mutex);
    while (g_queue.count == 0) {
        pthread_cond_wait(&g_queue.cond, &g_queue.mutex);
    }

    int fd = g_queue.fds[g_queue.head];
    g_queue.head = (g_queue.head + 1) % QUEUE_CAPACITY;
    g_queue.count--;
    pthread_mutex_unlock(&g_queue.mutex);
    return fd;
}

static void *worker_loop(void *unused) {
    (void)unused;
    for (;;) {
        int fd = queue_pop();
        if (fd >= 0) {
            handle_connection(fd);
        }
    }

    return NULL;
}

static int receive_socket_fd(int control_fd) {
    char data = 0;
    char control_buffer[CMSG_SPACE(sizeof(int))];
    struct iovec io;
    io.iov_base = &data;
    io.iov_len = 1;

    struct msghdr msg;
    memset(&msg, 0, sizeof(msg));
    msg.msg_iov = &io;
    msg.msg_iovlen = 1;
    msg.msg_control = control_buffer;
    msg.msg_controllen = sizeof(control_buffer);

    ssize_t received = recvmsg(control_fd, &msg, 0);
    if (received <= 0) {
        return -1;
    }

    struct cmsghdr *cmsg = CMSG_FIRSTHDR(&msg);
    if (cmsg == NULL ||
        cmsg->cmsg_level != SOL_SOCKET ||
        cmsg->cmsg_type != SCM_RIGHTS ||
        cmsg->cmsg_len < CMSG_LEN(sizeof(int))) {
        return -1;
    }

    int fd = -1;
    memcpy(&fd, CMSG_DATA(cmsg), sizeof(fd));
    return fd;
}

static int receive_socket_fd_epoll(int control_fd, int *client_fd, uint8_t *initial_buffer, int *initial_used) {
    *client_fd = -1;
    *initial_used = 0;

    char data = 0;
    char control_buffer[CMSG_SPACE(sizeof(int))];
    struct iovec io;
    if (g_fd_control_prebuffer) {
        io.iov_base = initial_buffer;
        io.iov_len = REQUEST_BUFFER_SIZE;
    } else {
        io.iov_base = &data;
        io.iov_len = 1;
    }

    struct msghdr msg;
    memset(&msg, 0, sizeof(msg));
    msg.msg_iov = &io;
    msg.msg_iovlen = 1;
    msg.msg_control = control_buffer;
    msg.msg_controllen = sizeof(control_buffer);

    ssize_t received = recvmsg(control_fd, &msg, 0);
    if (received <= 0) {
        if (received < 0 && (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR)) {
            return 0;
        }

        return -1;
    }

    struct cmsghdr *cmsg = CMSG_FIRSTHDR(&msg);
    if (cmsg == NULL ||
        cmsg->cmsg_level != SOL_SOCKET ||
        cmsg->cmsg_type != SCM_RIGHTS ||
        cmsg->cmsg_len < CMSG_LEN(sizeof(int))) {
        return -1;
    }

    memcpy(client_fd, CMSG_DATA(cmsg), sizeof(*client_fd));
    if (g_fd_control_prebuffer && !(received == 1 && initial_buffer[0] == 0)) {
        *initial_used = (int)received;
    }
    return 1;
}

static void epoll_handle_client(int epoll_fd, epoll_state_t *state);
static int drain_client_without_epoll(epoll_state_t *state);

static void *control_loop(void *arg) {
    int control_fd = (int)(intptr_t)arg;
    for (;;) {
        int fd = receive_socket_fd(control_fd);
        if (fd < 0) {
            close(control_fd);
            return NULL;
        }

        configure_client_fd(fd);
        queue_push(fd);
    }
}

static int create_control_listener(const char *path) {
    unlink(path);

    int socket_type = env_enabled("FD_CONTROL_SEQPACKET", 0) ? SOCK_SEQPACKET : SOCK_STREAM;
    int fd = socket(AF_UNIX, socket_type | SOCK_CLOEXEC, 0);
    if (fd < 0) {
        perror("socket");
        return -1;
    }

    struct sockaddr_un addr;
    memset(&addr, 0, sizeof(addr));
    addr.sun_family = AF_UNIX;
    strncpy(addr.sun_path, path, sizeof(addr.sun_path) - 1);

    if (bind(fd, (struct sockaddr *)&addr, sizeof(addr)) < 0) {
        perror("bind");
        close(fd);
        return -1;
    }

    chmod(path, 0666);
    if (listen(fd, 128) < 0) {
        perror("listen");
        close(fd);
        return -1;
    }

    return fd;
}

static int epoll_add_state(int epoll_fd, epoll_state_t *state) {
    struct epoll_event event;
    memset(&event, 0, sizeof(event));
    event.events = EPOLLIN | EPOLLERR | EPOLLHUP | EPOLLRDHUP;
    if (g_epoll_et) {
        event.events |= EPOLLET;
    }
    event.data.ptr = state;
    return epoll_ctl(epoll_fd, EPOLL_CTL_ADD, state->fd, &event);
}

static void epoll_close_state(int epoll_fd, epoll_state_t *state) {
    if (state == NULL) {
        return;
    }

    epoll_ctl(epoll_fd, EPOLL_CTL_DEL, state->fd, NULL);
    close(state->fd);
    if (state->type != EPOLL_STATE_LISTENER) {
        free(state);
    }
}

static void epoll_accept_controls(int epoll_fd, int listener) {
    for (;;) {
        int control_fd = accept4(listener, NULL, NULL, SOCK_NONBLOCK | SOCK_CLOEXEC);
        if (control_fd < 0) {
            if (errno == EINTR) {
                continue;
            }

            return;
        }

        epoll_state_t *state = calloc(1, sizeof(*state));
        if (state == NULL) {
            close(control_fd);
            continue;
        }

        state->type = EPOLL_STATE_CONTROL;
        state->fd = control_fd;
        if (epoll_add_state(epoll_fd, state) < 0) {
            close(control_fd);
            free(state);
        }
    }
}

static void epoll_receive_fds(int epoll_fd, epoll_state_t *control_state) {
    for (;;) {
        int client_fd = -1;
        uint8_t initial_buffer[REQUEST_BUFFER_SIZE];
        int initial_used = 0;
        int status = receive_socket_fd_epoll(control_state->fd, &client_fd, initial_buffer, &initial_used);
        if (status == 0) {
            return;
        }

        if (status < 0) {
            epoll_close_state(epoll_fd, control_state);
            return;
        }

        set_nonblocking(client_fd);
        configure_client_fd(client_fd);
        epoll_state_t *client_state = calloc(1, sizeof(*client_state));
        if (client_state == NULL) {
            close(client_fd);
            continue;
        }

        client_state->type = EPOLL_STATE_CLIENT;
        client_state->fd = client_fd;
        if (initial_used > 0) {
            memcpy(client_state->buffer, initial_buffer, (size_t)initial_used);
            client_state->used = initial_used;
        }
        if (g_fd_pre_read && drain_client_without_epoll(client_state) < 0) {
            free(client_state);
            continue;
        }

        if (epoll_add_state(epoll_fd, client_state) < 0) {
            close(client_fd);
            free(client_state);
        } else if (g_fd_immediate_read && !g_fd_pre_read) {
            epoll_handle_client(epoll_fd, client_state);
        }
    }
}

static int drain_client_without_epoll(epoll_state_t *state) {
    for (;;) {
        int header_end = 0;
        int body_length = 0;
        if (request_complete(state->buffer, state->used, &header_end, &body_length)) {
            handle_request(state->fd, state->buffer, state->used, header_end, body_length);
            state->used = 0;
            continue;
        }

        if (state->used >= REQUEST_BUFFER_SIZE) {
            send_all(state->fd, approved00_response, sizeof(approved00_response) - 1);
            close(state->fd);
            return -1;
        }

        ssize_t got = recv(state->fd, state->buffer + state->used, (size_t)(REQUEST_BUFFER_SIZE - state->used), 0);
        if (got > 0) {
            state->used += (int)got;
            continue;
        }

        if (got < 0 && errno == EINTR) {
            continue;
        }

        if (got < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) {
            return 0;
        }

        close(state->fd);
        return -1;
    }
}

static void epoll_handle_client(int epoll_fd, epoll_state_t *state) {
    for (;;) {
        int header_end = 0;
        int body_length = 0;
        if (request_complete(state->buffer, state->used, &header_end, &body_length)) {
            handle_request(state->fd, state->buffer, state->used, header_end, body_length);
            state->used = 0;
            continue;
        }

        if (state->used >= REQUEST_BUFFER_SIZE) {
            send_all(state->fd, approved00_response, sizeof(approved00_response) - 1);
            epoll_close_state(epoll_fd, state);
            return;
        }

        ssize_t got = recv(state->fd, state->buffer + state->used, (size_t)(REQUEST_BUFFER_SIZE - state->used), 0);
        if (got > 0) {
            state->used += (int)got;
            continue;
        }

        if (got < 0 && errno == EINTR) {
            continue;
        }

        if (got < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) {
            return;
        }

        epoll_close_state(epoll_fd, state);
        return;
    }
}

static int run_epoll_api(const char *control_path) {
    int listener = create_control_listener(control_path);
    if (listener < 0) {
        return 1;
    }

    set_nonblocking(listener);
    int epoll_fd = epoll_create1(EPOLL_CLOEXEC);
    if (epoll_fd < 0) {
        perror("epoll_create1");
        close(listener);
        return 1;
    }

    epoll_state_t listener_state;
    memset(&listener_state, 0, sizeof(listener_state));
    listener_state.type = EPOLL_STATE_LISTENER;
    listener_state.fd = listener;
    if (epoll_add_state(epoll_fd, &listener_state) < 0) {
        perror("epoll add listener");
        close(listener);
        close(epoll_fd);
        return 1;
    }

    fprintf(stderr, "native-api epoll serving fd control on %s\n", control_path);
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
            epoll_state_t *state = (epoll_state_t *)events[i].data.ptr;
            if (state->type == EPOLL_STATE_LISTENER) {
                epoll_accept_controls(epoll_fd, listener);
            } else if (state->type == EPOLL_STATE_CONTROL) {
                if (events[i].events & (EPOLLERR | EPOLLHUP | EPOLLRDHUP)) {
                    epoll_close_state(epoll_fd, state);
                } else {
                    epoll_receive_fds(epoll_fd, state);
                }
            } else if (state->type == EPOLL_STATE_CLIENT) {
                if (events[i].events & (EPOLLERR | EPOLLHUP)) {
                    epoll_close_state(epoll_fd, state);
                } else {
                    epoll_handle_client(epoll_fd, state);
                }
            }
        }
    }

    close(epoll_fd);
    close(listener);
    return 1;
}

static int valid_section(uint64_t offset, uint64_t length, size_t file_length, uint64_t expected_length) {
    if (offset == 0 || length != expected_length || offset > file_length) {
        return 0;
    }

    return length <= file_length - offset;
}

static int open_index(native_index_t *index, const char *path) {
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        perror("open index");
        return -1;
    }

    struct stat st;
    if (fstat(fd, &st) < 0 || st.st_size < 80) {
        perror("fstat index");
        close(fd);
        return -1;
    }

    uint8_t *base = mmap(NULL, (size_t)st.st_size, PROT_READ, MAP_PRIVATE, fd, 0);
    close(fd);
    if (base == MAP_FAILED) {
        perror("mmap index");
        return -1;
    }

    if (memcmp(base, "RINHA26I", 8) != 0) {
        fprintf(stderr, "bad index magic\n");
        munmap(base, (size_t)st.st_size);
        return -1;
    }

    uint64_t directory_offset = rd64(base + 72);
    if (directory_offset == 0 || directory_offset + 16 > (uint64_t)st.st_size ||
        memcmp(base + directory_offset, "R26XDIR1", 8) != 0) {
        fprintf(stderr, "missing extension directory\n");
        munmap(base, (size_t)st.st_size);
        return -1;
    }

    uint64_t profile_counts_offset = 0;
    uint64_t profile_counts_length = 0;
    uint64_t profile_masks_offset = 0;
    uint64_t profile_masks_length = 0;
    uint64_t kd_meta_offset = 0;
    uint64_t kd_meta_length = 0;
    uint64_t kd_partitions_offset = 0;
    uint64_t kd_partitions_length = 0;
    uint64_t kd_nodes_offset = 0;
    uint64_t kd_nodes_length = 0;
    uint64_t kd_vectors_offset = 0;
    uint64_t kd_vectors_length = 0;
    uint64_t kd_labels_offset = 0;
    uint64_t kd_labels_length = 0;
    uint64_t kd_ids_offset = 0;
    uint64_t kd_ids_length = 0;

    uint32_t section_count = rd32(base + directory_offset + 8);
    uint64_t entry_offset = directory_offset + 16;
    if (entry_offset + (uint64_t)section_count * 24 > (uint64_t)st.st_size) {
        fprintf(stderr, "bad extension directory length\n");
        munmap(base, (size_t)st.st_size);
        return -1;
    }

    for (uint32_t i = 0; i < section_count; i++) {
        const uint8_t *entry = base + entry_offset + (uint64_t)i * 24;
        uint32_t type = rd32(entry);
        uint64_t offset = rd64(entry + 8);
        uint64_t length = rd64(entry + 16);
        switch (type) {
            case SECTION_PROFILE_COUNTS:
                profile_counts_offset = offset;
                profile_counts_length = length;
                break;
            case SECTION_PROFILE_MASKS:
                profile_masks_offset = offset;
                profile_masks_length = length;
                break;
            case SECTION_KD_META:
                kd_meta_offset = offset;
                kd_meta_length = length;
                break;
            case SECTION_KD_PARTITIONS:
                kd_partitions_offset = offset;
                kd_partitions_length = length;
                break;
            case SECTION_KD_NODES:
                kd_nodes_offset = offset;
                kd_nodes_length = length;
                break;
            case SECTION_KD_VECTORS:
                kd_vectors_offset = offset;
                kd_vectors_length = length;
                break;
            case SECTION_KD_LABELS:
                kd_labels_offset = offset;
                kd_labels_length = length;
                break;
            case SECTION_KD_IDS:
                kd_ids_offset = offset;
                kd_ids_length = length;
                break;
            default:
                break;
        }
    }

    if (kd_meta_offset == 0 || kd_meta_length < 64 || kd_meta_offset + kd_meta_length > (uint64_t)st.st_size ||
        memcmp(base + kd_meta_offset, "KDT1", 4) != 0) {
        fprintf(stderr, "missing kd meta\n");
        munmap(base, (size_t)st.st_size);
        return -1;
    }

    const uint8_t *meta = base + kd_meta_offset;
    uint32_t partition_count = rd32(meta + 8);
    uint32_t node_count = rd32(meta + 12);
    uint32_t vector_count = rd32(meta + 16);
    uint32_t partition_record_size = rd32(meta + 24);
    uint32_t node_record_size = rd32(meta + 28);
    uint32_t vector_stride = rd32(meta + 32);
    if (partition_count != KD_PARTITION_COUNT ||
        partition_record_size != KD_PARTITION_RECORD_SIZE ||
        node_record_size != KD_NODE_RECORD_SIZE ||
        vector_stride != KD_VECTOR_STRIDE) {
        fprintf(stderr, "bad kd metadata\n");
        munmap(base, (size_t)st.st_size);
        return -1;
    }

    if (!valid_section(kd_partitions_offset, kd_partitions_length, (size_t)st.st_size, KD_PARTITION_COUNT * KD_PARTITION_RECORD_SIZE) ||
        !valid_section(kd_nodes_offset, kd_nodes_length, (size_t)st.st_size, (uint64_t)node_count * KD_NODE_RECORD_SIZE) ||
        !valid_section(kd_vectors_offset, kd_vectors_length, (size_t)st.st_size, (uint64_t)vector_count * KD_VECTOR_STRIDE * 2) ||
        !valid_section(kd_labels_offset, kd_labels_length, (size_t)st.st_size, vector_count) ||
        !valid_section(kd_ids_offset, kd_ids_length, (size_t)st.st_size, (uint64_t)vector_count * 4)) {
        fprintf(stderr, "bad kd section lengths\n");
        munmap(base, (size_t)st.st_size);
        return -1;
    }

    int max_partitions = env_int("KDTREE_MAX_PARTITIONS", KD_PARTITION_COUNT);
    if (max_partitions > KD_PARTITION_COUNT) {
        max_partitions = KD_PARTITION_COUNT;
    }

    index->base = base;
    index->length = (size_t)st.st_size;
    index->profile_counts = valid_section(profile_counts_offset, profile_counts_length, (size_t)st.st_size, PROFILE_KEY_COUNT * 2ULL)
        ? (const uint16_t *)(base + profile_counts_offset)
        : NULL;
    index->profile_masks = valid_section(profile_masks_offset, profile_masks_length, (size_t)st.st_size, PROFILE_KEY_COUNT)
        ? base + profile_masks_offset
        : NULL;
    index->partitions = base + kd_partitions_offset;
    index->nodes = base + kd_nodes_offset;
    index->vectors = (const int16_t *)(base + kd_vectors_offset);
    index->labels = base + kd_labels_offset;
    index->ids = (const int32_t *)(base + kd_ids_offset);
    index->node_count = (int32_t)node_count;
    index->max_partitions = max_partitions;

    madvise(base, (size_t)st.st_size, MADV_WILLNEED);
    volatile uint8_t checksum = 0;
    for (size_t i = 0; i < (size_t)st.st_size; i += 4096) {
        checksum ^= base[i];
    }
    checksum ^= base[(size_t)st.st_size - 1];
    fprintf(stderr, "native-api index mapped bytes=%zu nodes=%u checksum=%u\n", (size_t)st.st_size, node_count, checksum);
    return 0;
}

int main(void) {
    signal(SIGPIPE, SIG_IGN);
    if (env_enabled("PIN_FIRST_CPU", 0)) {
        pin_first_allowed_cpu();
    }

    const char *index_path = getenv("INDEX_PATH");
    if (index_path == NULL || *index_path == '\0') {
        index_path = "/app/data/references.idx";
    }

    if (open_index(&g_index, index_path) != 0) {
        return 1;
    }

    g_profile_fastpath = env_int("PROFILE_FASTPATH", 0) != 0;
    g_profile_legit_min_count = env_int("PROFILE_LEGIT_MIN_COUNT", 300);
    g_profile_fraud_min_count = env_int("PROFILE_FRAUD_MIN_COUNT", 100000);
    g_profile_fraud_amount_min = env_int("PROFILE_FRAUD_AMOUNT_MIN", 0);
    g_profile_fraud_no_last_only = env_enabled("PROFILE_FRAUD_NO_LAST_ONLY", 0);
    g_fd_immediate_read = env_enabled("FD_IMMEDIATE_READ", 0);
    g_epoll_et = env_enabled("EPOLL_ET", 0);
    g_fd_pre_read = env_enabled("FD_PRE_READ", 0);
    g_assume_body_complete = env_enabled("ASSUME_BODY_COMPLETE", 0);
    g_assume_json_body_start = env_enabled("ASSUME_JSON_BODY_START", 0);
    g_fd_control_prebuffer = env_enabled("FD_CONTROL_PREBUFFER", 0);

    const char *bind_addr = getenv("BIND_ADDR");
    if (bind_addr == NULL || strncmp(bind_addr, "fd:", 3) != 0) {
        fprintf(stderr, "native-api requires BIND_ADDR=fd:<control-socket>\n");
        return 1;
    }

    if (env_enabled("NATIVE_EPOLL", 0)) {
        return run_epoll_api(bind_addr + 3);
    }

    int worker_count = env_int("NATIVE_WORKERS", 64);
    if (worker_count < 1) {
        worker_count = 1;
    } else if (worker_count > 4096) {
        worker_count = 4096;
    }
    pthread_attr_t attr;
    pthread_attr_init(&attr);
    pthread_attr_setstacksize(&attr, 64 * 1024);
    for (int i = 0; i < worker_count; i++) {
        pthread_t thread;
        if (pthread_create(&thread, &attr, worker_loop, NULL) == 0) {
            pthread_detach(thread);
        }
    }
    pthread_attr_destroy(&attr);

    int listener = create_control_listener(bind_addr + 3);
    if (listener < 0) {
        return 1;
    }

    fprintf(stderr, "native-api serving fd control on %s workers=%d\n", bind_addr + 3, worker_count);
    for (;;) {
        int control_fd = accept4(listener, NULL, NULL, SOCK_CLOEXEC);
        if (control_fd < 0) {
            if (errno == EINTR) {
                continue;
            }

            perror("accept control");
            continue;
        }

        pthread_t thread;
        if (pthread_create(&thread, NULL, control_loop, (void *)(intptr_t)control_fd) == 0) {
            pthread_detach(thread);
        } else {
            close(control_fd);
        }
    }
}
