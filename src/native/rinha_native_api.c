#define _GNU_SOURCE

#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <pthread.h>
#include <signal.h>
#include <sys/epoll.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/uio.h>
#include <sys/un.h>
#include <unistd.h>

#define DIM 14
#define PADDED_DIM 16
#define SCALE 10000
#define K 5
#define BUCKET_COUNT 4096
#define PROFILE_KEY_COUNT (1 << 22)
#define RISKY_STRIDE 16
#define RISKY_FINE_BUCKET_COUNT (BUCKET_COUNT << 3)
#define MAX_REQUEST_BYTES 8192
#define MAX_EVENTS 1024

#define SECTION_PROFILE_COUNTS 1
#define SECTION_PROFILE_MASKS 2
#define SECTION_NEIGHBOR_ORDERS 3
#define SECTION_RISKY_META 4
#define SECTION_RISKY_VECTORS 5
#define SECTION_RISKY_LABELS 6
#define SECTION_RISKY_FINE_BUCKET_OFFSETS 8
#define SECTION_RISKY_COARSE_FINE_OFFSETS 9
#define SECTION_RISKY_FINE_KEYS 10
#define SECTION_PROFILE_FRAUD_COUNTS 14
#define SECTION_PROFILE_COMPACT_COUNTS 15

#define LEGIT_MASK 1
#define FRAUD_MASK 2

extern int32_t rinha_consider_ann_avx2(
    const int16_t *vectors,
    const uint8_t *labels,
    const int32_t *bucket_offsets,
    const uint16_t *neighbor_keys,
    const int16_t *query,
    int32_t early_candidates,
    int32_t min_candidates,
    int32_t max_candidates,
    int32_t early_edge_fallback,
    int64_t *top_dist,
    uint8_t *top_label);

extern int32_t rinha_consider_ann_avx2_limited(
    const int16_t *vectors,
    const uint8_t *labels,
    const int32_t *bucket_offsets,
    const uint16_t *neighbor_keys,
    int32_t neighbor_count,
    const int16_t *query,
    int32_t early_candidates,
    int32_t min_candidates,
    int32_t max_candidates,
    int32_t early_edge_fallback,
    int64_t *top_dist,
    uint8_t *top_label);

extern int32_t rinha_consider_risky_fine_avx2(
    const int16_t *vectors,
    const uint8_t *labels,
    const int32_t *fine_bucket_offsets,
    const int32_t *coarse_fine_offsets,
    const int32_t *fine_keys,
    const uint16_t *neighbor_keys,
    const int16_t *query,
    int64_t *top_dist,
    uint8_t *top_label);

typedef struct {
    const char *ptr;
    int len;
} slice_t;

typedef struct {
    uint8_t *base;
    size_t length;
    int fd;
    int count;
    const int16_t *vectors;
    const uint8_t *labels;
    const int32_t *bucket_offsets;
    const uint16_t *neighbor_orders;
    int neighbor_order_count;
    const uint16_t *profile_counts;
    const uint16_t *profile_fraud_counts;
    const uint8_t *profile_masks;
    const uint8_t *profile_compact_counts;
    int risky_count;
    const int16_t *risky_vectors;
    const uint8_t *risky_labels;
    const int32_t *risky_fine_offsets;
    const int32_t *risky_coarse_offsets;
    const int32_t *risky_fine_keys;
} index_t;

typedef struct {
    int early_candidates;
    int min_candidates;
    int max_candidates;
    int early_edge_fallback;
    int profile_fastpath;
    int profile_legit_min_count;
    int profile_fraud_min_count;
    int profile_dominant_fastpath;
    int profile_dominant_min_count;
    int profile_dominant_max_opposite;
    int keep_alive_requests;
} settings_t;

typedef struct {
    double amount;
    double installments;
    slice_t requested_at;
    double customer_avg;
    double tx_count_24h;
    slice_t known_merchants;
    slice_t merchant_id;
    slice_t mcc;
    double merchant_avg;
    int is_online;
    int card_present;
    double km_from_home;
    int has_last_transaction;
    slice_t last_timestamp;
    double last_km_from_current;
} parsed_request_t;

typedef struct {
    int fd;
    index_t *index;
    settings_t settings;
} client_work_t;

typedef struct {
    int fd;
    index_t *index;
    settings_t settings;
} control_work_t;

typedef enum {
    EPOLL_LISTENER = 1,
    EPOLL_CONTROL = 2,
    EPOLL_CLIENT = 3
} epoll_kind_t;

typedef struct epoll_state {
    epoll_kind_t kind;
    int fd;
    int used;
    int handled;
    char buffer[MAX_REQUEST_BYTES];
} epoll_state_t;

static volatile uint8_t prefault_sink;

static const char response_ready[] = "HTTP/1.1 200 OK\r\nContent-Length:2\r\n\r\nOK";
static const char response_not_found[] = "HTTP/1.1 404 Not Found\r\nContent-Length:9\r\n\r\nnot found";
static const char response_00[] = "HTTP/1.1 200 OK\r\nContent-Length:35\r\n\r\n{\"approved\":true,\"fraud_score\":0.0}";
static const char response_02[] = "HTTP/1.1 200 OK\r\nContent-Length:35\r\n\r\n{\"approved\":true,\"fraud_score\":0.2}";
static const char response_04[] = "HTTP/1.1 200 OK\r\nContent-Length:35\r\n\r\n{\"approved\":true,\"fraud_score\":0.4}";
static const char response_06[] = "HTTP/1.1 200 OK\r\nContent-Length:36\r\n\r\n{\"approved\":false,\"fraud_score\":0.6}";
static const char response_08[] = "HTTP/1.1 200 OK\r\nContent-Length:36\r\n\r\n{\"approved\":false,\"fraud_score\":0.8}";
static const char response_10[] = "HTTP/1.1 200 OK\r\nContent-Length:36\r\n\r\n{\"approved\":false,\"fraud_score\":1.0}";

typedef struct {
    const char *data;
    size_t len;
} response_t;

static const response_t fraud_responses[] = {
    { response_00, sizeof(response_00) - 1 },
    { response_02, sizeof(response_02) - 1 },
    { response_04, sizeof(response_04) - 1 },
    { response_06, sizeof(response_06) - 1 },
    { response_08, sizeof(response_08) - 1 },
    { response_10, sizeof(response_10) - 1 },
};

static inline uint32_t rd32(const uint8_t *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static inline uint64_t rd64(const uint8_t *p) {
    return (uint64_t)rd32(p) | ((uint64_t)rd32(p + 4) << 32);
}

static inline int env_int(const char *name, int fallback) {
    const char *value = getenv(name);
    if (value == NULL || *value == '\0') {
        return fallback;
    }

    return atoi(value);
}

static inline int env_bool(const char *name, int fallback) {
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

static settings_t load_settings(void) {
    settings_t settings;
    settings.early_candidates = env_int("EARLY_CANDIDATES", 9800);
    settings.min_candidates = env_int("MIN_CANDIDATES", 9800);
    settings.max_candidates = env_int("MAX_CANDIDATES", 11000);
    if (settings.max_candidates < settings.min_candidates) {
        settings.max_candidates = settings.min_candidates;
    }

    settings.early_edge_fallback = env_bool("EARLY_EDGE_FALLBACK", 1);
    settings.profile_fastpath = env_bool("PROFILE_FASTPATH", 1);
    settings.profile_legit_min_count = env_int("PROFILE_LEGIT_MIN_COUNT", 5);
    settings.profile_fraud_min_count = env_int("PROFILE_FRAUD_MIN_COUNT", 15);
    settings.profile_dominant_fastpath = env_bool("PROFILE_DOMINANT_FASTPATH", 1);
    settings.profile_dominant_min_count = env_int("PROFILE_DOMINANT_MIN_COUNT", 15);
    settings.profile_dominant_max_opposite = env_int("PROFILE_DOMINANT_MAX_OPPOSITE", 2);
    settings.keep_alive_requests = env_int("KEEP_ALIVE_REQUESTS", 0);
    return settings;
}

static int valid_range(size_t file_length, uint64_t offset, uint64_t length) {
    return offset > 0 && length > 0 && offset <= file_length && length <= file_length - offset;
}

static int open_index(const char *path, index_t *index) {
    memset(index, 0, sizeof(*index));
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        perror("open index");
        return -1;
    }

    struct stat st;
    if (fstat(fd, &st) < 0 || st.st_size < 80) {
        perror("stat index");
        close(fd);
        return -1;
    }

    uint8_t *base = mmap(NULL, (size_t)st.st_size, PROT_READ, MAP_PRIVATE, fd, 0);
    if (base == MAP_FAILED) {
        perror("mmap index");
        close(fd);
        return -1;
    }

    if (memcmp(base, "RINHA26I", 8) != 0 || rd32(base + 12) != DIM) {
        fprintf(stderr, "invalid index header\n");
        munmap(base, (size_t)st.st_size);
        close(fd);
        return -1;
    }

    uint64_t vectors_offset = rd64(base + 32);
    uint64_t labels_offset = rd64(base + 40);
    uint64_t bucket_offsets_offset = rd64(base + 48);
    uint64_t extension_offset = rd64(base + 72);
    int count = (int)rd32(base + 16);
    size_t file_length = (size_t)st.st_size;

    if (!valid_range(file_length, vectors_offset, (uint64_t)count * DIM * 2) ||
        !valid_range(file_length, labels_offset, (uint64_t)count) ||
        !valid_range(file_length, bucket_offsets_offset, (uint64_t)(BUCKET_COUNT + 1) * 4) ||
        !valid_range(file_length, extension_offset, 16)) {
        fprintf(stderr, "invalid index offsets\n");
        munmap(base, file_length);
        close(fd);
        return -1;
    }

    uint64_t profile_counts_offset = 0;
    uint64_t profile_masks_offset = 0;
    uint64_t profile_fraud_counts_offset = 0;
    uint64_t profile_compact_counts_offset = 0;
    uint64_t neighbor_orders_offset = 0;
    uint64_t neighbor_orders_length = 0;
    uint64_t risky_meta_offset = 0;
    uint64_t risky_vectors_offset = 0;
    uint64_t risky_labels_offset = 0;
    uint64_t risky_fine_offsets_offset = 0;
    uint64_t risky_coarse_offsets_offset = 0;
    uint64_t risky_fine_keys_offset = 0;
    uint64_t risky_fine_key_length = 0;

    const uint8_t *directory = base + extension_offset;
    if (memcmp(directory, "R26XDIR1", 8) != 0) {
        fprintf(stderr, "missing extension directory\n");
        munmap(base, file_length);
        close(fd);
        return -1;
    }

    uint32_t sections = rd32(directory + 8);
    uint64_t entries_offset = extension_offset + 16;
    if (!valid_range(file_length, entries_offset, (uint64_t)sections * 24)) {
        fprintf(stderr, "invalid extension directory length\n");
        munmap(base, file_length);
        close(fd);
        return -1;
    }

    for (uint32_t i = 0; i < sections; i++) {
        const uint8_t *entry = base + entries_offset + (uint64_t)i * 24;
        uint32_t type = rd32(entry);
        uint64_t offset = rd64(entry + 8);
        uint64_t length = rd64(entry + 16);
        if (!valid_range(file_length, offset, length)) {
            continue;
        }

        switch (type) {
            case SECTION_PROFILE_COUNTS:
                if (length == (uint64_t)PROFILE_KEY_COUNT * 2) profile_counts_offset = offset;
                break;
            case SECTION_PROFILE_MASKS:
                if (length == (uint64_t)PROFILE_KEY_COUNT) profile_masks_offset = offset;
                break;
            case SECTION_PROFILE_FRAUD_COUNTS:
                if (length == (uint64_t)PROFILE_KEY_COUNT * 2) profile_fraud_counts_offset = offset;
                break;
            case SECTION_PROFILE_COMPACT_COUNTS:
                if (length == (uint64_t)PROFILE_KEY_COUNT * 2) profile_compact_counts_offset = offset;
                break;
            case SECTION_NEIGHBOR_ORDERS:
                if (length >= (uint64_t)BUCKET_COUNT * 2 &&
                    length <= (uint64_t)BUCKET_COUNT * BUCKET_COUNT * 2 &&
                    length % ((uint64_t)BUCKET_COUNT * 2) == 0) {
                    neighbor_orders_offset = offset;
                    neighbor_orders_length = length;
                }
                break;
            case SECTION_RISKY_META:
                risky_meta_offset = offset;
                break;
            case SECTION_RISKY_VECTORS:
                risky_vectors_offset = offset;
                break;
            case SECTION_RISKY_LABELS:
                risky_labels_offset = offset;
                break;
            case SECTION_RISKY_FINE_BUCKET_OFFSETS:
                risky_fine_offsets_offset = offset;
                break;
            case SECTION_RISKY_COARSE_FINE_OFFSETS:
                risky_coarse_offsets_offset = offset;
                break;
            case SECTION_RISKY_FINE_KEYS:
                risky_fine_keys_offset = offset;
                risky_fine_key_length = length;
                break;
        }
    }

    int has_profile_compact = profile_compact_counts_offset != 0;
    int has_profile_legacy = profile_counts_offset != 0 &&
                             profile_masks_offset != 0 &&
                             profile_fraud_counts_offset != 0;
    if ((!has_profile_compact && !has_profile_legacy) ||
        neighbor_orders_offset == 0 ||
        risky_meta_offset == 0 ||
        risky_vectors_offset == 0 ||
        risky_labels_offset == 0 ||
        risky_fine_offsets_offset == 0 ||
        risky_coarse_offsets_offset == 0 ||
        risky_fine_keys_offset == 0) {
        fprintf(stderr, "required index sections are missing\n");
        munmap(base, file_length);
        close(fd);
        return -1;
    }

    const uint8_t *meta = base + risky_meta_offset;
    if (memcmp(meta, "RSKY", 4) != 0 || rd32(meta + 4) != 1 || rd32(meta + 12) != RISKY_STRIDE) {
        fprintf(stderr, "invalid risky meta\n");
        munmap(base, file_length);
        close(fd);
        return -1;
    }

    int risky_count = (int)rd32(meta + 8);
    int fine_key_count = (int)rd32(meta + 16);
    int neighbor_order_count = (int)(neighbor_orders_length / ((uint64_t)BUCKET_COUNT * 2));
    if (!valid_range(file_length, risky_vectors_offset, (uint64_t)risky_count * RISKY_STRIDE * 2) ||
        !valid_range(file_length, risky_labels_offset, (uint64_t)risky_count) ||
        !valid_range(file_length, risky_fine_offsets_offset, (uint64_t)(RISKY_FINE_BUCKET_COUNT + 1) * 4) ||
        !valid_range(file_length, risky_coarse_offsets_offset, (uint64_t)(BUCKET_COUNT + 1) * 4) ||
        risky_fine_key_length != (uint64_t)fine_key_count * 4) {
        fprintf(stderr, "invalid risky section lengths\n");
        munmap(base, file_length);
        close(fd);
        return -1;
    }

    if (env_bool("INDEX_HUGEPAGES", 0)) {
#ifdef MADV_HUGEPAGE
        (void)madvise(base, file_length, MADV_HUGEPAGE);
#endif
    }

    (void)madvise(base, file_length, MADV_WILLNEED);
    uint8_t checksum = 0;
    for (size_t pos = 0; pos < file_length; pos += 4096) {
        checksum ^= base[pos];
    }

    checksum ^= base[file_length - 1];
    prefault_sink ^= checksum;
    index->base = base;
    index->length = file_length;
    index->fd = fd;
    index->count = count;
    index->vectors = (const int16_t *)(base + vectors_offset);
    index->labels = base + labels_offset;
    index->bucket_offsets = (const int32_t *)(base + bucket_offsets_offset);
    index->neighbor_orders = (const uint16_t *)(base + neighbor_orders_offset);
    index->neighbor_order_count = neighbor_order_count;
    index->profile_counts = has_profile_legacy ? (const uint16_t *)(base + profile_counts_offset) : NULL;
    index->profile_fraud_counts = has_profile_legacy ? (const uint16_t *)(base + profile_fraud_counts_offset) : NULL;
    index->profile_masks = has_profile_legacy ? base + profile_masks_offset : NULL;
    index->profile_compact_counts = has_profile_compact ? base + profile_compact_counts_offset : NULL;
    index->risky_count = risky_count;
    index->risky_vectors = (const int16_t *)(base + risky_vectors_offset);
    index->risky_labels = base + risky_labels_offset;
    index->risky_fine_offsets = (const int32_t *)(base + risky_fine_offsets_offset);
    index->risky_coarse_offsets = (const int32_t *)(base + risky_coarse_offsets_offset);
    index->risky_fine_keys = (const int32_t *)(base + risky_fine_keys_offset);
    return 0;
}

static void close_index(index_t *index) {
    if (index->base != NULL) {
        munmap(index->base, index->length);
    }

    if (index->fd >= 0) {
        close(index->fd);
    }
}

static inline int skip_ws(const char *s, int len, int pos) {
    while (pos < len && (s[pos] == ' ' || s[pos] == '\n' || s[pos] == '\r' || s[pos] == '\t')) {
        pos++;
    }

    return pos;
}

static int read_string(const char *s, int len, int *pos, slice_t *slice) {
    int p = skip_ws(s, len, *pos);
    if (p >= len || s[p] != '"') {
        return 0;
    }

    p++;
    int start = p;
    int escaped = 0;
    while (p < len) {
        char c = s[p];
        if (escaped) {
            escaped = 0;
        } else if (c == '\\') {
            escaped = 1;
        } else if (c == '"') {
            slice->ptr = s + start;
            slice->len = p - start;
            *pos = p + 1;
            return 1;
        }

        p++;
    }

    return 0;
}

static int consume_colon(const char *s, int len, int *pos) {
    int p = skip_ws(s, len, *pos);
    if (p >= len || s[p] != ':') {
        return 0;
    }

    *pos = p + 1;
    return 1;
}

static int slice_eq(slice_t value, const char *literal) {
    int n = (int)strlen(literal);
    return value.len == n && memcmp(value.ptr, literal, (size_t)n) == 0;
}

static int read_bool(const char *s, int len, int *pos, int *value) {
    int p = skip_ws(s, len, *pos);
    if (p + 4 <= len && memcmp(s + p, "true", 4) == 0) {
        *value = 1;
        *pos = p + 4;
        return 1;
    }

    if (p + 5 <= len && memcmp(s + p, "false", 5) == 0) {
        *value = 0;
        *pos = p + 5;
        return 1;
    }

    return 0;
}

static int read_number(const char *s, int len, int *pos, double *value) {
    int p = skip_ws(s, len, *pos);
    int start = p;
    int negative = 0;
    if (p < len && (s[p] == '-' || s[p] == '+')) {
        negative = s[p] == '-';
        p++;
    }

    long integer = 0;
    int digits = 0;
    while (p < len && s[p] >= '0' && s[p] <= '9') {
        integer = integer * 10 + (s[p] - '0');
        digits++;
        p++;
    }

    double result = (double)integer;
    if (p < len && s[p] == '.') {
        p++;
        double scale = 0.1;
        while (p < len && s[p] >= '0' && s[p] <= '9') {
            result += (double)(s[p] - '0') * scale;
            scale *= 0.1;
            digits++;
            p++;
        }
    }

    if (digits == 0) {
        *pos = start;
        return 0;
    }

    if (p < len && (s[p] == 'e' || s[p] == 'E')) {
        p++;
        int exp_neg = 0;
        if (p < len && (s[p] == '-' || s[p] == '+')) {
            exp_neg = s[p] == '-';
            p++;
        }

        int exp = 0;
        int exp_digits = 0;
        while (p < len && s[p] >= '0' && s[p] <= '9') {
            exp = exp * 10 + (s[p] - '0');
            exp_digits++;
            p++;
        }

        if (exp_digits == 0) {
            *pos = start;
            return 0;
        }

        while (exp-- > 0) {
            result = exp_neg ? result / 10.0 : result * 10.0;
        }
    }

    *value = negative ? -result : result;
    *pos = p;
    return 1;
}

static int skip_value(const char *s, int len, int *pos);

static int skip_array(const char *s, int len, int *pos) {
    int p = skip_ws(s, len, *pos);
    if (p >= len || s[p] != '[') {
        return 0;
    }

    p++;
    for (;;) {
        p = skip_ws(s, len, p);
        if (p >= len) {
            return 0;
        }

        if (s[p] == ']') {
            *pos = p + 1;
            return 1;
        }

        if (!skip_value(s, len, &p)) {
            return 0;
        }

        p = skip_ws(s, len, p);
        if (p < len && s[p] == ',') {
            p++;
            continue;
        }

        if (p < len && s[p] == ']') {
            *pos = p + 1;
            return 1;
        }

        return 0;
    }
}

static int read_array_slice(const char *s, int len, int *pos, slice_t *slice) {
    int p = skip_ws(s, len, *pos);
    if (p >= len || s[p] != '[') {
        return 0;
    }

    int start = p;
    if (!skip_array(s, len, &p)) {
        return 0;
    }

    slice->ptr = s + start;
    slice->len = p - start;
    *pos = p;
    return 1;
}

static int skip_object(const char *s, int len, int *pos) {
    int p = skip_ws(s, len, *pos);
    if (p >= len || s[p] != '{') {
        return 0;
    }

    p++;
    for (;;) {
        p = skip_ws(s, len, p);
        if (p >= len) {
            return 0;
        }

        if (s[p] == '}') {
            *pos = p + 1;
            return 1;
        }

        slice_t key;
        if (!read_string(s, len, &p, &key) || !consume_colon(s, len, &p) || !skip_value(s, len, &p)) {
            return 0;
        }

        p = skip_ws(s, len, p);
        if (p < len && s[p] == ',') {
            p++;
            continue;
        }

        if (p < len && s[p] == '}') {
            *pos = p + 1;
            return 1;
        }

        return 0;
    }
}

static int skip_value(const char *s, int len, int *pos) {
    int p = skip_ws(s, len, *pos);
    if (p >= len) {
        return 0;
    }

    if (s[p] == '"') {
        slice_t ignored;
        return read_string(s, len, pos, &ignored);
    }

    if (s[p] == '{') {
        return skip_object(s, len, pos);
    }

    if (s[p] == '[') {
        return skip_array(s, len, pos);
    }

    if (p + 4 <= len && (memcmp(s + p, "true", 4) == 0 || memcmp(s + p, "null", 4) == 0)) {
        *pos = p + 4;
        return 1;
    }

    if (p + 5 <= len && memcmp(s + p, "false", 5) == 0) {
        *pos = p + 5;
        return 1;
    }

    double ignored;
    return read_number(s, len, pos, &ignored);
}

static int skip_delimiter_or_end(const char *s, int len, int *pos, char end) {
    int p = skip_ws(s, len, *pos);
    if (p < len && s[p] == ',') {
        *pos = p + 1;
        return 1;
    }

    if (p < len && s[p] == end) {
        *pos = p;
        return 1;
    }

    return 0;
}

static int parse_transaction(const char *s, int len, int *pos, parsed_request_t *parsed) {
    int p = skip_ws(s, len, *pos);
    if (p >= len || s[p] != '{') {
        return 0;
    }

    p++;
    int has_amount = 0;
    int has_installments = 0;
    int has_requested_at = 0;
    for (;;) {
        p = skip_ws(s, len, p);
        if (p >= len) return 0;
        if (s[p] == '}') {
            *pos = p + 1;
            return has_amount && has_installments && has_requested_at;
        }

        slice_t key;
        if (!read_string(s, len, &p, &key) || !consume_colon(s, len, &p)) return 0;
        if (slice_eq(key, "amount")) {
            if (!read_number(s, len, &p, &parsed->amount)) return 0;
            has_amount = 1;
        } else if (slice_eq(key, "installments")) {
            if (!read_number(s, len, &p, &parsed->installments)) return 0;
            has_installments = 1;
        } else if (slice_eq(key, "requested_at")) {
            if (!read_string(s, len, &p, &parsed->requested_at)) return 0;
            has_requested_at = 1;
        } else if (!skip_value(s, len, &p)) {
            return 0;
        }

        if (!skip_delimiter_or_end(s, len, &p, '}')) return 0;
    }
}

static int parse_customer(const char *s, int len, int *pos, parsed_request_t *parsed) {
    int p = skip_ws(s, len, *pos);
    if (p >= len || s[p] != '{') return 0;
    p++;
    int has_avg = 0;
    int has_tx = 0;
    int has_known = 0;
    for (;;) {
        p = skip_ws(s, len, p);
        if (p >= len) return 0;
        if (s[p] == '}') {
            *pos = p + 1;
            return has_avg && has_tx && has_known;
        }

        slice_t key;
        if (!read_string(s, len, &p, &key) || !consume_colon(s, len, &p)) return 0;
        if (slice_eq(key, "avg_amount")) {
            if (!read_number(s, len, &p, &parsed->customer_avg)) return 0;
            has_avg = 1;
        } else if (slice_eq(key, "tx_count_24h")) {
            if (!read_number(s, len, &p, &parsed->tx_count_24h)) return 0;
            has_tx = 1;
        } else if (slice_eq(key, "known_merchants")) {
            if (!read_array_slice(s, len, &p, &parsed->known_merchants)) return 0;
            has_known = 1;
        } else if (!skip_value(s, len, &p)) {
            return 0;
        }

        if (!skip_delimiter_or_end(s, len, &p, '}')) return 0;
    }
}

static int parse_merchant(const char *s, int len, int *pos, parsed_request_t *parsed) {
    int p = skip_ws(s, len, *pos);
    if (p >= len || s[p] != '{') return 0;
    p++;
    int has_id = 0;
    int has_mcc = 0;
    int has_avg = 0;
    for (;;) {
        p = skip_ws(s, len, p);
        if (p >= len) return 0;
        if (s[p] == '}') {
            *pos = p + 1;
            return has_id && has_mcc && has_avg;
        }

        slice_t key;
        if (!read_string(s, len, &p, &key) || !consume_colon(s, len, &p)) return 0;
        if (slice_eq(key, "id")) {
            if (!read_string(s, len, &p, &parsed->merchant_id)) return 0;
            has_id = 1;
        } else if (slice_eq(key, "mcc")) {
            if (!read_string(s, len, &p, &parsed->mcc)) return 0;
            has_mcc = 1;
        } else if (slice_eq(key, "avg_amount")) {
            if (!read_number(s, len, &p, &parsed->merchant_avg)) return 0;
            has_avg = 1;
        } else if (!skip_value(s, len, &p)) {
            return 0;
        }

        if (!skip_delimiter_or_end(s, len, &p, '}')) return 0;
    }
}

static int parse_terminal(const char *s, int len, int *pos, parsed_request_t *parsed) {
    int p = skip_ws(s, len, *pos);
    if (p >= len || s[p] != '{') return 0;
    p++;
    int has_online = 0;
    int has_card = 0;
    int has_km = 0;
    for (;;) {
        p = skip_ws(s, len, p);
        if (p >= len) return 0;
        if (s[p] == '}') {
            *pos = p + 1;
            return has_online && has_card && has_km;
        }

        slice_t key;
        if (!read_string(s, len, &p, &key) || !consume_colon(s, len, &p)) return 0;
        if (slice_eq(key, "is_online")) {
            if (!read_bool(s, len, &p, &parsed->is_online)) return 0;
            has_online = 1;
        } else if (slice_eq(key, "card_present")) {
            if (!read_bool(s, len, &p, &parsed->card_present)) return 0;
            has_card = 1;
        } else if (slice_eq(key, "km_from_home")) {
            if (!read_number(s, len, &p, &parsed->km_from_home)) return 0;
            has_km = 1;
        } else if (!skip_value(s, len, &p)) {
            return 0;
        }

        if (!skip_delimiter_or_end(s, len, &p, '}')) return 0;
    }
}

static int parse_last_transaction(const char *s, int len, int *pos, parsed_request_t *parsed) {
    int p = skip_ws(s, len, *pos);
    if (p + 4 <= len && memcmp(s + p, "null", 4) == 0) {
        parsed->has_last_transaction = 0;
        parsed->last_timestamp.ptr = NULL;
        parsed->last_timestamp.len = 0;
        parsed->last_km_from_current = 0;
        *pos = p + 4;
        return 1;
    }

    if (p >= len || s[p] != '{') return 0;
    p++;
    parsed->has_last_transaction = 1;
    int has_ts = 0;
    int has_km = 0;
    for (;;) {
        p = skip_ws(s, len, p);
        if (p >= len) return 0;
        if (s[p] == '}') {
            *pos = p + 1;
            return has_ts && has_km;
        }

        slice_t key;
        if (!read_string(s, len, &p, &key) || !consume_colon(s, len, &p)) return 0;
        if (slice_eq(key, "timestamp")) {
            if (!read_string(s, len, &p, &parsed->last_timestamp)) return 0;
            has_ts = 1;
        } else if (slice_eq(key, "km_from_current")) {
            if (!read_number(s, len, &p, &parsed->last_km_from_current)) return 0;
            has_km = 1;
        } else if (!skip_value(s, len, &p)) {
            return 0;
        }

        if (!skip_delimiter_or_end(s, len, &p, '}')) return 0;
    }
}

static int parse_request(const char *s, int len, parsed_request_t *parsed) {
    memset(parsed, 0, sizeof(*parsed));
    int p = skip_ws(s, len, 0);
    if (p >= len || s[p] != '{') {
        return 0;
    }

    p++;
    int has_transaction = 0;
    int has_customer = 0;
    int has_merchant = 0;
    int has_terminal = 0;
    int has_last = 0;
    for (;;) {
        p = skip_ws(s, len, p);
        if (p >= len) return 0;
        if (s[p] == '}') {
            return has_transaction && has_customer && has_merchant && has_terminal && has_last;
        }

        slice_t key;
        if (!read_string(s, len, &p, &key) || !consume_colon(s, len, &p)) return 0;
        if (slice_eq(key, "transaction")) {
            if (!parse_transaction(s, len, &p, parsed)) return 0;
            has_transaction = 1;
        } else if (slice_eq(key, "customer")) {
            if (!parse_customer(s, len, &p, parsed)) return 0;
            has_customer = 1;
        } else if (slice_eq(key, "merchant")) {
            if (!parse_merchant(s, len, &p, parsed)) return 0;
            has_merchant = 1;
        } else if (slice_eq(key, "terminal")) {
            if (!parse_terminal(s, len, &p, parsed)) return 0;
            has_terminal = 1;
        } else if (slice_eq(key, "last_transaction")) {
            if (!parse_last_transaction(s, len, &p, parsed)) return 0;
            has_last = 1;
        } else if (!skip_value(s, len, &p)) {
            return 0;
        }

        if (!skip_delimiter_or_end(s, len, &p, '}')) return 0;
    }
}

static int find_literal_from(const char *s, int len, int start, const char *literal) {
    int literal_len = (int)strlen(literal);
    if (start < 0 || start > len || literal_len <= 0 || len - start < literal_len) {
        return -1;
    }

    const char *found = memmem(s + start, (size_t)(len - start), literal, (size_t)literal_len);
    return found == NULL ? -1 : (int)(found - s);
}

static int read_number_after_literal(const char *s, int len, int start, const char *literal, double *value, int *after) {
    int pos = find_literal_from(s, len, start, literal);
    if (pos < 0) {
        return 0;
    }

    pos += (int)strlen(literal);
    if (!read_number(s, len, &pos, value)) {
        return 0;
    }

    *after = pos;
    return 1;
}

static int read_bool_after_literal(const char *s, int len, int start, const char *literal, int *value, int *after) {
    int pos = find_literal_from(s, len, start, literal);
    if (pos < 0) {
        return 0;
    }

    pos += (int)strlen(literal);
    if (!read_bool(s, len, &pos, value)) {
        return 0;
    }

    *after = pos;
    return 1;
}

static int read_string_after_literal(const char *s, int len, int start, const char *literal, slice_t *value, int *after) {
    int pos = find_literal_from(s, len, start, literal);
    if (pos < 0) {
        return 0;
    }

    pos += (int)strlen(literal);
    int end = pos;
    while (end < len && s[end] != '"') {
        if (s[end] == '\\') {
            return 0;
        }

        end++;
    }

    if (end >= len) {
        return 0;
    }

    value->ptr = s + pos;
    value->len = end - pos;
    *after = end + 1;
    return 1;
}

static int read_array_after_literal(const char *s, int len, int start, const char *literal, slice_t *value, int *after) {
    int pos = find_literal_from(s, len, start, literal);
    if (pos < 0) {
        return 0;
    }

    pos += (int)strlen(literal) - 1;
    if (!read_array_slice(s, len, &pos, value)) {
        return 0;
    }

    *after = pos;
    return 1;
}

static int parse_request_fast(const char *s, int len, parsed_request_t *parsed) {
    memset(parsed, 0, sizeof(*parsed));
    int pos = find_literal_from(s, len, 0, "\"transaction\":{");
    if (pos < 0) return 0;
    if (!read_number_after_literal(s, len, pos, "\"amount\":", &parsed->amount, &pos)) return 0;
    if (!read_number_after_literal(s, len, pos, "\"installments\":", &parsed->installments, &pos)) return 0;
    if (!read_string_after_literal(s, len, pos, "\"requested_at\":\"", &parsed->requested_at, &pos)) return 0;

    pos = find_literal_from(s, len, pos, "\"customer\":{");
    if (pos < 0) return 0;
    if (!read_number_after_literal(s, len, pos, "\"avg_amount\":", &parsed->customer_avg, &pos)) return 0;
    if (!read_number_after_literal(s, len, pos, "\"tx_count_24h\":", &parsed->tx_count_24h, &pos)) return 0;
    if (!read_array_after_literal(s, len, pos, "\"known_merchants\":[", &parsed->known_merchants, &pos)) return 0;

    pos = find_literal_from(s, len, pos, "\"merchant\":{");
    if (pos < 0) return 0;
    if (!read_string_after_literal(s, len, pos, "\"id\":\"", &parsed->merchant_id, &pos)) return 0;
    if (!read_string_after_literal(s, len, pos, "\"mcc\":\"", &parsed->mcc, &pos)) return 0;
    if (!read_number_after_literal(s, len, pos, "\"avg_amount\":", &parsed->merchant_avg, &pos)) return 0;

    pos = find_literal_from(s, len, pos, "\"terminal\":{");
    if (pos < 0) return 0;
    if (!read_bool_after_literal(s, len, pos, "\"is_online\":", &parsed->is_online, &pos)) return 0;
    if (!read_bool_after_literal(s, len, pos, "\"card_present\":", &parsed->card_present, &pos)) return 0;
    if (!read_number_after_literal(s, len, pos, "\"km_from_home\":", &parsed->km_from_home, &pos)) return 0;

    pos = find_literal_from(s, len, pos, "\"last_transaction\":");
    if (pos < 0) return 0;
    pos += 19;
    pos = skip_ws(s, len, pos);
    if (pos + 4 <= len && memcmp(s + pos, "null", 4) == 0) {
        parsed->has_last_transaction = 0;
        return 1;
    }

    if (pos >= len || s[pos] != '{') {
        return 0;
    }

    parsed->has_last_transaction = 1;
    if (!read_string_after_literal(s, len, pos, "\"timestamp\":\"", &parsed->last_timestamp, &pos)) return 0;
    if (!read_number_after_literal(s, len, pos, "\"km_from_current\":", &parsed->last_km_from_current, &pos)) return 0;
    return 1;
}

static inline double clamp01(double value) {
    if (value != value || value < 0.0) {
        return 0.0;
    }

    if (value > 1.0) {
        return 1.0;
    }

    return value;
}

static inline int16_t quantize(double value) {
    double scaled = value * (double)SCALE;
    int result = (int)(scaled + 0.5);
    if (result < -SCALE) result = -SCALE;
    if (result > SCALE) result = SCALE;
    return (int16_t)result;
}

static int parse2(const char *s, int len, int offset, int *value) {
    if (offset + 2 > len) return 0;
    int a = s[offset] - '0';
    int b = s[offset + 1] - '0';
    if ((unsigned)a > 9 || (unsigned)b > 9) return 0;
    *value = a * 10 + b;
    return 1;
}

static int parse4(const char *s, int len, int offset, int *value) {
    if (offset + 4 > len) return 0;
    int result = 0;
    for (int i = 0; i < 4; i++) {
        int d = s[offset + i] - '0';
        if ((unsigned)d > 9) return 0;
        result = result * 10 + d;
    }

    *value = result;
    return 1;
}

static int parse_time(slice_t ts, int *year, int *month, int *day, int *hour, int *minute) {
    if (ts.len < 16) return 0;
    return parse4(ts.ptr, ts.len, 0, year) &&
           parse2(ts.ptr, ts.len, 5, month) &&
           parse2(ts.ptr, ts.len, 8, day) &&
           parse2(ts.ptr, ts.len, 11, hour) &&
           parse2(ts.ptr, ts.len, 14, minute);
}

static int day_of_week(int year, int month, int day) {
    static const int t[12] = {0, 3, 2, 5, 0, 3, 5, 1, 4, 6, 2, 4};
    int y = year;
    if (month < 3) {
        y--;
    }

    int dow = (y + y / 4 - y / 100 + y / 400 + t[month - 1] + day) % 7;
    return (dow + 6) % 7;
}

static int64_t days_from_civil(int year, int month, int day) {
    int y = year - (month <= 2 ? 1 : 0);
    int era = (y >= 0 ? y : y - 399) / 400;
    unsigned yoe = (unsigned)(y - era * 400);
    unsigned mp = (unsigned)(month + (month > 2 ? -3 : 9));
    unsigned doy = (153 * mp + 2) / 5 + (unsigned)day - 1;
    unsigned doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return (int64_t)era * 146097 + (int64_t)doe - 719468;
}

static int64_t epoch_minutes(int year, int month, int day, int hour, int minute) {
    return days_from_civil(year, month, day) * 1440 + (int64_t)hour * 60 + minute;
}

static double mcc_risk(slice_t mcc) {
    if (mcc.len == 4 && memcmp(mcc.ptr, "5411", 4) == 0) return 0.15;
    if (mcc.len == 4 && memcmp(mcc.ptr, "5812", 4) == 0) return 0.30;
    if (mcc.len == 4 && memcmp(mcc.ptr, "5912", 4) == 0) return 0.20;
    if (mcc.len == 4 && memcmp(mcc.ptr, "5944", 4) == 0) return 0.45;
    if (mcc.len == 4 && memcmp(mcc.ptr, "7801", 4) == 0) return 0.80;
    if (mcc.len == 4 && memcmp(mcc.ptr, "7802", 4) == 0) return 0.75;
    if (mcc.len == 4 && memcmp(mcc.ptr, "7995", 4) == 0) return 0.85;
    if (mcc.len == 4 && memcmp(mcc.ptr, "4511", 4) == 0) return 0.35;
    if (mcc.len == 4 && memcmp(mcc.ptr, "5311", 4) == 0) return 0.25;
    if (mcc.len == 4 && memcmp(mcc.ptr, "5999", 4) == 0) return 0.50;
    return 0.50;
}

static int contains_quoted(slice_t haystack, slice_t needle) {
    if (needle.len <= 0 || haystack.len < needle.len + 2) {
        return 0;
    }

    for (int pos = 0; pos + needle.len + 2 <= haystack.len; pos++) {
        if (haystack.ptr[pos] == '"' &&
            memcmp(haystack.ptr + pos + 1, needle.ptr, (size_t)needle.len) == 0 &&
            haystack.ptr[pos + 1 + needle.len] == '"') {
            return 1;
        }
    }

    return 0;
}

static void vectorize(const parsed_request_t *parsed, int16_t *output) {
    int year = 2026;
    int month = 1;
    int day = 1;
    int hour = 0;
    int minute = 0;
    (void)parse_time(parsed->requested_at, &year, &month, &day, &hour, &minute);
    int dow = day_of_week(year, month, day);

    output[0] = quantize(clamp01(parsed->amount / 10000.0));
    output[1] = quantize(clamp01(parsed->installments / 12.0));
    output[2] = quantize(clamp01(parsed->customer_avg <= 0.0 ? 1.0 : (parsed->amount / parsed->customer_avg) / 10.0));
    output[3] = quantize((double)hour / 23.0);
    output[4] = quantize((double)dow / 6.0);
    if (parsed->has_last_transaction) {
        int ly = year;
        int lm = month;
        int ld = day;
        int lh = hour;
        int lmin = minute;
        (void)parse_time(parsed->last_timestamp, &ly, &lm, &ld, &lh, &lmin);
        int64_t current = epoch_minutes(year, month, day, hour, minute);
        int64_t last = epoch_minutes(ly, lm, ld, lh, lmin);
        int64_t minutes_since_last = current > last ? current - last : 0;
        output[5] = quantize(clamp01((double)minutes_since_last / 1440.0));
        output[6] = quantize(clamp01(parsed->last_km_from_current / 1000.0));
    } else {
        output[5] = -SCALE;
        output[6] = -SCALE;
    }

    output[7] = quantize(clamp01(parsed->km_from_home / 1000.0));
    output[8] = quantize(clamp01(parsed->tx_count_24h / 20.0));
    output[9] = parsed->is_online ? SCALE : 0;
    output[10] = parsed->card_present ? SCALE : 0;
    output[11] = contains_quoted(parsed->known_merchants, parsed->merchant_id) ? 0 : SCALE;
    output[12] = quantize(mcc_risk(parsed->mcc));
    output[13] = quantize(clamp01(parsed->merchant_avg / 10000.0));
    output[14] = 0;
    output[15] = 0;
}

static int build_query(const char *body, int body_len, int16_t *query) {
    parsed_request_t parsed;
    if (!parse_request_fast(body, body_len, &parsed) &&
        !parse_request(body, body_len, &parsed)) {
        return 0;
    }

    vectorize(&parsed, query);
    return 1;
}

static inline int bucket8(int16_t value) {
    if (value <= 0) return 0;
    int b = (int)value * 8 / (SCALE + 1);
    return b < 0 ? 0 : b > 7 ? 7 : b;
}

static inline int bucket16(int16_t value) {
    if (value <= 0) return 0;
    int b = (int)value * 16 / (SCALE + 1);
    return b < 0 ? 0 : b > 15 ? 15 : b;
}

static inline int bucket4(int16_t value) {
    if (value <= 0) return 0;
    int b = (int)value * 4 / (SCALE + 1);
    return b < 0 ? 0 : b > 3 ? 3 : b;
}

static inline int bucket_key(const int16_t *v) {
    int amount = bucket8(v[0]);
    int ratio = bucket8(v[2]);
    int km_home = bucket8(v[7]);
    int hour = bucket4(v[3]);
    int no_last = v[5] < 0 ? 1 : 0;
    return amount | (ratio << 3) | (km_home << 6) | (hour << 9) | (no_last << 11);
}

static int fill_neighbor_keys_for_key(int bucket, uint16_t *output) {
    int amount = bucket & 7;
    int ratio = (bucket >> 3) & 7;
    int km_home = (bucket >> 6) & 7;
    int hour = (bucket >> 9) & 3;
    int no_last = (bucket >> 11) & 1;
    uint8_t seen[BUCKET_COUNT];
    memset(seen, 0, sizeof(seen));
    int count = 0;

    for (int radius = 0; radius < 8; radius++) {
        int amount_start = amount - radius > 0 ? amount - radius : 0;
        int amount_end = amount + radius < 7 ? amount + radius : 7;
        int ratio_start = ratio - radius > 0 ? ratio - radius : 0;
        int ratio_end = ratio + radius < 7 ? ratio + radius : 7;
        int km_start = km_home - radius > 0 ? km_home - radius : 0;
        int km_end = km_home + radius < 7 ? km_home + radius : 7;
        int hour_start = hour - radius > 0 ? hour - radius : 0;
        int hour_end = hour + radius < 3 ? hour + radius : 3;
        int last_start = radius >= 2 ? 0 : no_last;
        int last_end = radius >= 2 ? 1 : no_last;

        for (int a = amount_start; a <= amount_end; a++) {
            for (int r = ratio_start; r <= ratio_end; r++) {
                for (int k = km_start; k <= km_end; k++) {
                    for (int h = hour_start; h <= hour_end; h++) {
                        for (int last = last_start; last <= last_end; last++) {
                            int key = a | (r << 3) | (k << 6) | (h << 9) | (last << 11);
                            if (seen[key] != 0) {
                                continue;
                            }

                            seen[key] = 1;
                            output[count++] = (uint16_t)key;
                        }
                    }
                }
            }
        }
    }

    return count;
}

static inline int profile_key(const int16_t *v) {
    int key = 0;
    key |= bucket16(v[2]);
    key |= bucket8(v[7]) << 4;
    key |= bucket4(v[8]) << 7;
    key |= bucket4(v[12]) << 9;
    key |= bucket4(v[0]) << 11;
    key |= (v[5] < 0 ? 1 : 0) << 13;
    key |= (v[9] > 0 ? 1 : 0) << 14;
    key |= (v[10] > 0 ? 1 : 0) << 15;
    key |= (v[11] > 0 ? 1 : 0) << 16;
    key |= bucket4(v[6]) << 17;
    key |= (v[1] > 1000 ? 1 : 0) << 19;
    key |= bucket4(v[13]) << 20;
    return key;
}

static inline int64_t range_distance_squared(int16_t value, int min, int max) {
    if (value < min) {
        int64_t d = (int64_t)min - value;
        return d * d;
    }

    if (value > max) {
        int64_t d = (int64_t)value - max;
        return d * d;
    }

    return 0;
}

static inline int64_t bucket_distance_squared(int16_t value, int bucket, int divisions) {
    int min = bucket == 0 ? 0 : (bucket * (SCALE + 1) + divisions - 1) / divisions;
    int max = bucket == divisions - 1 ? SCALE : (((bucket + 1) * (SCALE + 1)) - 1) / divisions;
    return range_distance_squared(value, min, max);
}

static int64_t risky_bucket_lower_bound(int key, const int16_t *query) {
    int amount = key & 7;
    int ratio = (key >> 3) & 7;
    int km_home = (key >> 6) & 7;
    int hour = (key >> 9) & 3;
    int no_last = (key >> 11) & 1;

    int64_t sum = 0;
    sum += bucket_distance_squared(query[0], amount, 8);
    sum += bucket_distance_squared(query[2], ratio, 8);
    sum += bucket_distance_squared(query[7], km_home, 8);
    sum += bucket_distance_squared(query[3], hour, 4);
    sum += no_last == 0
        ? range_distance_squared(query[5], 0, SCALE)
        : range_distance_squared(query[5], -SCALE, -SCALE);
    return sum;
}

static inline int64_t distance_mapped_scalar(const int16_t *vector, const int16_t *query, int64_t cutoff) {
    int64_t sum = 0;
    for (int i = 0; i < DIM; i++) {
        int64_t d = (int64_t)query[i] - vector[i];
        sum += d * d;
        if (sum >= cutoff) {
            return sum;
        }
    }

    return sum;
}

static inline void insert_candidate(int64_t dist, uint8_t label, int64_t *top_dist, uint8_t *top_label) {
    if (dist < top_dist[0]) {
        top_dist[4] = top_dist[3]; top_dist[3] = top_dist[2]; top_dist[2] = top_dist[1]; top_dist[1] = top_dist[0]; top_dist[0] = dist;
        top_label[4] = top_label[3]; top_label[3] = top_label[2]; top_label[2] = top_label[1]; top_label[1] = top_label[0]; top_label[0] = label;
    } else if (dist < top_dist[1]) {
        top_dist[4] = top_dist[3]; top_dist[3] = top_dist[2]; top_dist[2] = top_dist[1]; top_dist[1] = dist;
        top_label[4] = top_label[3]; top_label[3] = top_label[2]; top_label[2] = top_label[1]; top_label[1] = label;
    } else if (dist < top_dist[2]) {
        top_dist[4] = top_dist[3]; top_dist[3] = top_dist[2]; top_dist[2] = dist;
        top_label[4] = top_label[3]; top_label[3] = top_label[2]; top_label[2] = label;
    } else if (dist < top_dist[3]) {
        top_dist[4] = top_dist[3]; top_dist[3] = dist;
        top_label[4] = top_label[3]; top_label[3] = label;
    } else if (dist < top_dist[4]) {
        top_dist[4] = dist; top_label[4] = label;
    }
}

static inline int count_frauds(const uint8_t *top_label) {
    return (int)top_label[0] + (int)top_label[1] + (int)top_label[2] + (int)top_label[3] + (int)top_label[4];
}

static int high_risk_online_fallback(const int16_t *q) {
    return q[12] >= 8000 &&
           q[1] >= 5500 &&
           q[6] >= 1000 && q[6] <= 1700 &&
           q[7] >= 300 && q[7] <= 4200 &&
           q[8] >= 3800 && q[8] <= 6000 &&
           ((q[0] >= 450 && q[0] <= 600 && q[2] <= 1200) ||
            (q[0] >= 2500 && q[0] <= 3100 && q[2] >= 9000));
}

static int strong_profile_tiebreak(const int16_t *q, int frauds) {
    if (q[5] < 0 || q[13] > 220) {
        return 0;
    }

    if (frauds == 0) {
        return
            (q[9] == 0 &&
             q[10] > 0 &&
             q[12] >= 7500 &&
             q[0] >= 450 && q[0] <= 600 &&
             q[2] >= 1000 && q[2] <= 1200 &&
             q[7] >= 400 && q[7] <= 600 &&
             q[8] >= 4000 && q[8] <= 5000) ||
            (q[9] > 0 &&
             q[10] == 0 &&
             q[12] <= 2500 &&
             q[0] >= 2100 && q[0] <= 2300 &&
             q[2] >= 4400 && q[2] <= 4900 &&
             q[7] >= 700 && q[7] <= 950 &&
             q[8] >= 2000 && q[8] <= 3000) ||
            (q[9] > 0 &&
             q[10] == 0 &&
             q[11] > 0 &&
             q[12] >= 4000 && q[12] <= 5000 &&
             q[0] >= 1200 && q[0] <= 1500 &&
             q[2] >= 3300 && q[2] <= 3800 &&
             q[7] >= 3300 && q[7] <= 3900 &&
             q[8] >= 2000 && q[8] <= 3000);
    }

    return q[9] == 0 &&
           q[10] > 0 &&
           q[12] <= 2500 &&
           q[0] >= 2700 && q[0] <= 3000 &&
           q[2] >= 9000 &&
           q[7] >= 3500 && q[7] <= 4000 &&
           q[8] >= 2500 && q[8] <= 3500;
}

static int strong_fallback_risk(const int16_t *q, int frauds) {
    if (frauds != 0 && frauds != K) {
        return 0;
    }

    if (frauds == 0 && high_risk_online_fallback(q)) {
        return 1;
    }

    if (strong_profile_tiebreak(q, frauds)) {
        return 1;
    }

    if (frauds == K &&
        q[5] >= 600 && q[5] <= 850 &&
        q[9] == 0 &&
        q[10] == 0 &&
        q[11] == 0 &&
        q[12] <= 2000 &&
        q[0] >= 1100 && q[0] <= 1300 &&
        q[2] >= 4000 && q[2] <= 4600 &&
        q[7] >= 550 && q[7] <= 750 &&
        q[8] >= 2000 && q[8] <= 3000 &&
        q[13] >= 220 && q[13] <= 320) {
        return 1;
    }

    return q[5] >= 0 &&
           q[10] == 0 &&
           q[0] >= 450 && q[0] <= 1100 &&
           q[2] >= 900 && q[2] <= 2500 &&
           q[7] >= 500 && q[7] <= 2000 &&
           q[8] >= 2000 && q[8] <= 4500;
}

static int should_use_exact_fallback(const int16_t *q, int frauds) {
    if (frauds > 0 && frauds < K) {
        return 1;
    }

    return strong_fallback_risk(q, frauds);
}

static int needs_full_risky_tiebreak(const int16_t *q, int frauds) {
    if (q[5] < 0 || q[9] <= 0 || q[10] != 0) {
        return 0;
    }

    if (frauds >= 3) {
        return q[11] == 0 &&
               q[12] <= 1700 &&
               q[0] >= 500 && q[0] <= 900 &&
               q[2] >= 1000 && q[2] <= 2200 &&
               q[7] >= 350 && q[7] <= 900 &&
               q[8] >= 1800 && q[8] <= 3000;
    }

    return high_risk_online_fallback(q);
}

static int try_profile_fast_decision(const index_t *index, const settings_t *settings, const int16_t *q, int *fraud_count) {
    *fraud_count = 0;
    if (!settings->profile_fastpath) {
        return 0;
    }

    int key = profile_key(q);
    if (index->profile_compact_counts != NULL) {
        const uint8_t *counts = index->profile_compact_counts + key * 2;
        int profile_legits = counts[0];
        int profile_frauds = counts[1];
        if (profile_frauds == 0) {
            if (profile_legits < settings->profile_legit_min_count) {
                return 0;
            }

            *fraud_count = 0;
            return 1;
        }

        if (profile_legits == 0) {
            if (profile_frauds < settings->profile_fraud_min_count) {
                return 0;
            }

            *fraud_count = K;
            return 1;
        }

        if (settings->profile_dominant_fastpath) {
            if (profile_frauds >= settings->profile_dominant_min_count &&
                profile_legits <= settings->profile_dominant_max_opposite) {
                *fraud_count = K;
                return 1;
            }

            if (profile_legits >= settings->profile_dominant_min_count &&
                profile_frauds <= settings->profile_dominant_max_opposite) {
                *fraud_count = 0;
                return 1;
            }
        }

        return 0;
    }

    uint8_t mask = index->profile_masks[key];
    int profile_count = index->profile_counts[key];
    if (mask == LEGIT_MASK) {
        if (profile_count < settings->profile_legit_min_count) {
            return 0;
        }

        *fraud_count = 0;
        return 1;
    }

    if (mask == FRAUD_MASK) {
        if (profile_count < settings->profile_fraud_min_count) {
            return 0;
        }

        *fraud_count = K;
        return 1;
    }

    if (settings->profile_dominant_fastpath) {
        int profile_frauds = index->profile_fraud_counts[key];
        int profile_legits = profile_count > profile_frauds ? profile_count - profile_frauds : 0;
        if (profile_frauds >= settings->profile_dominant_min_count &&
            profile_legits <= settings->profile_dominant_max_opposite) {
            *fraud_count = K;
            return 1;
        }

        if (profile_legits >= settings->profile_dominant_min_count &&
            profile_frauds <= settings->profile_dominant_max_opposite) {
            *fraud_count = 0;
            return 1;
        }
    }

    return 0;
}

static int classify_flat(const index_t *index, const int16_t *q, int64_t seed_cutoff) {
    int64_t top_dist[K] = {INT64_MAX, INT64_MAX, INT64_MAX, INT64_MAX, INT64_MAX};
    uint8_t top_label[K] = {0, 0, 0, 0, 0};
    int64_t prune_cutoff = seed_cutoff;
    for (int key = 0; key < BUCKET_COUNT; key++) {
        if (prune_cutoff != INT64_MAX && risky_bucket_lower_bound(key, q) > prune_cutoff) {
            continue;
        }

        int start = index->bucket_offsets[key];
        int end = index->bucket_offsets[key + 1];
        for (int id = start; id < end; id++) {
            int64_t dist = distance_mapped_scalar(index->vectors + (int64_t)id * DIM, q, top_dist[K - 1]);
            if (dist < top_dist[K - 1]) {
                insert_candidate(dist, index->labels[id], top_dist, top_label);
            }
        }

        if (top_dist[K - 1] < prune_cutoff) {
            prune_cutoff = top_dist[K - 1];
        }
    }

    return count_frauds(top_label);
}

static int classify_risky_flat(const index_t *index, const int16_t *q) {
    if (index->risky_count < K) {
        return classify_flat(index, q, INT64_MAX);
    }

    int64_t top_dist[K] = {INT64_MAX, INT64_MAX, INT64_MAX, INT64_MAX, INT64_MAX};
    uint8_t top_label[K] = {0, 0, 0, 0, 0};
    int key = bucket_key(q);
    uint16_t generated_neighbor_keys[BUCKET_COUNT];
    const uint16_t *neighbor_keys = index->neighbor_orders + (int64_t)key * index->neighbor_order_count;
    if (index->neighbor_order_count < BUCKET_COUNT) {
        (void)fill_neighbor_keys_for_key(key, generated_neighbor_keys);
        neighbor_keys = generated_neighbor_keys;
    }

    (void)rinha_consider_risky_fine_avx2(
        index->risky_vectors,
        index->risky_labels,
        index->risky_fine_offsets,
        index->risky_coarse_offsets,
        index->risky_fine_keys,
        neighbor_keys,
        q,
        top_dist,
        top_label);

    int frauds = count_frauds(top_label);
    return needs_full_risky_tiebreak(q, frauds) ? classify_flat(index, q, top_dist[K - 1]) : frauds;
}

static int classify(const index_t *index, const settings_t *settings, const int16_t *q) {
    int fraud_count = 0;
    if (try_profile_fast_decision(index, settings, q, &fraud_count)) {
        return fraud_count;
    }

    int64_t top_dist[K] = {INT64_MAX, INT64_MAX, INT64_MAX, INT64_MAX, INT64_MAX};
    uint8_t top_label[K] = {0, 0, 0, 0, 0};
    int key = bucket_key(q);
    const uint16_t *neighbor_keys = index->neighbor_orders + (int64_t)key * index->neighbor_order_count;
    int candidates = rinha_consider_ann_avx2_limited(
        index->vectors,
        index->labels,
        index->bucket_offsets,
        neighbor_keys,
        index->neighbor_order_count,
        q,
        settings->early_candidates,
        settings->min_candidates,
        settings->max_candidates,
        settings->early_edge_fallback,
        top_dist,
        top_label);

    if (candidates < K) {
        return classify_flat(index, q, INT64_MAX);
    }

    if (candidates < settings->min_candidates && index->neighbor_order_count < BUCKET_COUNT) {
        return classify_flat(index, q, top_dist[K - 1]);
    }

    int frauds = count_frauds(top_label);
    if (!should_use_exact_fallback(q, frauds)) {
        return frauds;
    }

    return classify_risky_flat(index, q);
}

static inline response_t response_for_frauds(int fraud_count) {
    if (fraud_count <= 0) {
        return fraud_responses[0];
    }

    if (fraud_count >= K) {
        return fraud_responses[K];
    }

    return fraud_responses[fraud_count];
}

static int find_bytes(const char *haystack, int haystack_len, const char *needle, int needle_len) {
    if (needle_len <= 0 || haystack_len < needle_len) {
        return -1;
    }

    for (int i = 0; i <= haystack_len - needle_len; i++) {
        if (memcmp(haystack + i, needle, (size_t)needle_len) == 0) {
            return i;
        }
    }

    return -1;
}

static int fast_content_length(const char *headers, int len) {
    int pos = find_bytes(headers, len, "\r\nContent-Length:", 17);
    if (pos >= 0) {
        pos += 17;
    } else {
        pos = find_bytes(headers, len, "\r\ncontent-length:", 17);
        if (pos >= 0) {
            pos += 17;
        } else if (len >= 15 && memcmp(headers, "Content-Length:", 15) == 0) {
            pos = 15;
        } else if (len >= 15 && memcmp(headers, "content-length:", 15) == 0) {
            pos = 15;
        } else {
            return 0;
        }
    }

    while (pos < len && (headers[pos] == ' ' || headers[pos] == '\t')) {
        pos++;
    }

    int value = 0;
    while (pos < len && headers[pos] >= '0' && headers[pos] <= '9') {
        value = value * 10 + (headers[pos] - '0');
        pos++;
    }

    return value;
}

static int request_complete(const char *buffer, int used, int *header_end, int *content_length) {
    int pos = find_bytes(buffer, used, "\r\n\r\n", 4);
    if (pos < 0) {
        return 0;
    }

    *header_end = pos;
    *content_length = fast_content_length(buffer, pos + 4);
    return used >= pos + 4 + *content_length;
}

static int send_all(int fd, const char *data, size_t len) {
    size_t sent_total = 0;
    while (sent_total < len) {
        ssize_t sent = send(fd, data + sent_total, len - sent_total, MSG_NOSIGNAL);
        if (sent > 0) {
            sent_total += (size_t)sent;
            continue;
        }

        if (sent < 0 && errno == EINTR) {
            continue;
        }

        return -1;
    }

    return 0;
}

static void handle_request(int fd, const char *buffer, int used, int header_end, int content_length, const index_t *index, const settings_t *settings) {
    if (!(used >= 18 && memcmp(buffer, "POST /fraud-score ", 18) == 0) &&
        !(used >= 18 && memcmp(buffer, "POST /fraud-score?", 18) == 0)) {
        if ((used >= 11 && memcmp(buffer, "GET /ready ", 11) == 0) ||
            (used >= 11 && memcmp(buffer, "GET /ready?", 11) == 0)) {
            (void)send_all(fd, response_ready, sizeof(response_ready) - 1);
            return;
        }

        (void)send_all(fd, response_not_found, sizeof(response_not_found) - 1);
        return;
    }

    int body_start = header_end + 4;
    if (body_start < 0 || content_length < 0 || body_start + content_length > used) {
        (void)send_all(fd, response_00, sizeof(response_00) - 1);
        return;
    }

    int16_t query[PADDED_DIM] = {0};
    if (!build_query(buffer + body_start, content_length, query)) {
        (void)send_all(fd, response_00, sizeof(response_00) - 1);
        return;
    }

    int frauds = classify(index, settings, query);
    response_t response = response_for_frauds(frauds);
    (void)send_all(fd, response.data, response.len);
}

static void *client_thread(void *arg) {
    client_work_t *work = (client_work_t *)arg;
    int fd = work->fd;
    index_t *index = work->index;
    settings_t settings = work->settings;
    free(work);

    char buffer[MAX_REQUEST_BYTES];
    int handled = 0;
    for (;;) {
        int used = 0;
        int header_end = 0;
        int content_length = 0;
        while (!request_complete(buffer, used, &header_end, &content_length)) {
            if (used >= MAX_REQUEST_BYTES) {
                (void)send_all(fd, response_00, sizeof(response_00) - 1);
                close(fd);
                return NULL;
            }

            ssize_t got = recv(fd, buffer + used, sizeof(buffer) - (size_t)used, 0);
            if (got > 0) {
                used += (int)got;
                continue;
            }

            if (got < 0 && errno == EINTR) {
                continue;
            }

            close(fd);
            return NULL;
        }

        handle_request(fd, buffer, used, header_end, content_length, index, &settings);
        handled++;
        if (settings.keep_alive_requests > 0 && handled >= settings.keep_alive_requests) {
            close(fd);
            return NULL;
        }
    }
}

static int recv_fd(int control_fd) {
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

    ssize_t received;
    do {
        received = recvmsg(control_fd, &msg, 0);
    } while (received < 0 && errno == EINTR);

    if (received <= 0) {
        return -1;
    }

    struct cmsghdr *cmsg = CMSG_FIRSTHDR(&msg);
    if (cmsg == NULL || cmsg->cmsg_level != SOL_SOCKET || cmsg->cmsg_type != SCM_RIGHTS) {
        return -1;
    }

    int fd = -1;
    memcpy(&fd, CMSG_DATA(cmsg), sizeof(fd));
    return fd;
}

static int recv_fd_nonblocking(int control_fd) {
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

    int flags = MSG_DONTWAIT;
#ifdef MSG_CMSG_CLOEXEC
    flags |= MSG_CMSG_CLOEXEC;
#endif
    ssize_t received = recvmsg(control_fd, &msg, flags);
    if (received < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) {
        return -2;
    }

    if (received < 0 && errno == EINTR) {
        return -2;
    }

    if (received <= 0) {
        return -1;
    }

    struct cmsghdr *cmsg = CMSG_FIRSTHDR(&msg);
    if (cmsg == NULL || cmsg->cmsg_level != SOL_SOCKET || cmsg->cmsg_type != SCM_RIGHTS) {
        return -1;
    }

    int fd = -1;
    memcpy(&fd, CMSG_DATA(cmsg), sizeof(fd));
    return fd;
}

static int set_nonblocking(int fd) {
    int flags = fcntl(fd, F_GETFL, 0);
    if (flags < 0) {
        return -1;
    }

    return fcntl(fd, F_SETFL, flags | O_NONBLOCK);
}

static int start_client_thread(int fd, index_t *index, const settings_t *settings) {
    client_work_t *work = malloc(sizeof(*work));
    if (work == NULL) {
        close(fd);
        return -1;
    }

    work->fd = fd;
    work->index = index;
    work->settings = *settings;

    pthread_t thread;
    pthread_attr_t attr;
    pthread_attr_init(&attr);
    pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED);
    pthread_attr_setstacksize(&attr, 128 * 1024);
    int rc = pthread_create(&thread, &attr, client_thread, work);
    pthread_attr_destroy(&attr);
    if (rc != 0) {
        free(work);
        close(fd);
        return -1;
    }

    return 0;
}

static void *control_thread(void *arg) {
    control_work_t *work = (control_work_t *)arg;
    int control_fd = work->fd;
    index_t *index = work->index;
    settings_t settings = work->settings;
    free(work);

    for (;;) {
        int fd = recv_fd(control_fd);
        if (fd < 0) {
            close(control_fd);
            return NULL;
        }

        (void)start_client_thread(fd, index, &settings);
    }
}

static int start_control_thread(int fd, index_t *index, const settings_t *settings) {
    control_work_t *work = malloc(sizeof(*work));
    if (work == NULL) {
        close(fd);
        return -1;
    }

    work->fd = fd;
    work->index = index;
    work->settings = *settings;

    pthread_t thread;
    pthread_attr_t attr;
    pthread_attr_init(&attr);
    pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED);
    pthread_attr_setstacksize(&attr, 128 * 1024);
    int rc = pthread_create(&thread, &attr, control_thread, work);
    pthread_attr_destroy(&attr);
    if (rc != 0) {
        free(work);
        close(fd);
        return -1;
    }

    return 0;
}

static int create_unix_listener(const char *path) {
    unlink(path);
    int fd = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
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
    if (listen(fd, 4096) < 0) {
        perror("listen");
        close(fd);
        return -1;
    }

    return fd;
}

static int add_epoll_state(int epoll_fd, epoll_state_t *state) {
    struct epoll_event event;
    memset(&event, 0, sizeof(event));
    event.events = EPOLLIN | EPOLLERR | EPOLLHUP | EPOLLRDHUP;
    event.data.ptr = state;
    return epoll_ctl(epoll_fd, EPOLL_CTL_ADD, state->fd, &event);
}

static void close_epoll_state(int epoll_fd, epoll_state_t *state) {
    if (state == NULL) {
        return;
    }

    (void)epoll_ctl(epoll_fd, EPOLL_CTL_DEL, state->fd, NULL);
    close(state->fd);
    free(state);
}

static void accept_control_epoll(int epoll_fd, int listener) {
    for (;;) {
        int fd = accept4(listener, NULL, NULL, SOCK_NONBLOCK | SOCK_CLOEXEC);
        if (fd < 0) {
            if (errno == EINTR) {
                continue;
            }

            if (errno == EAGAIN || errno == EWOULDBLOCK) {
                return;
            }

            perror("accept control");
            return;
        }

        epoll_state_t *state = calloc(1, sizeof(*state));
        if (state == NULL) {
            close(fd);
            continue;
        }

        state->kind = EPOLL_CONTROL;
        state->fd = fd;
        if (add_epoll_state(epoll_fd, state) < 0) {
            close_epoll_state(epoll_fd, state);
        }
    }
}

static void accept_client_fd_epoll(int epoll_fd, int fd) {
    (void)set_nonblocking(fd);
    epoll_state_t *state = calloc(1, sizeof(*state));
    if (state == NULL) {
        close(fd);
        return;
    }

    state->kind = EPOLL_CLIENT;
    state->fd = fd;
    if (add_epoll_state(epoll_fd, state) < 0) {
        close_epoll_state(epoll_fd, state);
    }
}

static int handle_control_epoll(int epoll_fd, epoll_state_t *state) {
    for (;;) {
        int fd = recv_fd_nonblocking(state->fd);
        if (fd >= 0) {
            accept_client_fd_epoll(epoll_fd, fd);
            continue;
        }

        if (fd == -2) {
            return 0;
        }

        return -1;
    }
}

static int handle_client_epoll(epoll_state_t *state, const index_t *index, const settings_t *settings) {
    for (;;) {
        int header_end = 0;
        int content_length = 0;
        if (request_complete(state->buffer, state->used, &header_end, &content_length)) {
            handle_request(state->fd, state->buffer, state->used, header_end, content_length, index, settings);
            state->used = 0;
            state->handled++;
            if (settings->keep_alive_requests > 0 && state->handled >= settings->keep_alive_requests) {
                return -1;
            }

            return 0;
        }

        if (state->used >= MAX_REQUEST_BYTES) {
            (void)send_all(state->fd, response_00, sizeof(response_00) - 1);
            return -1;
        }

        ssize_t got = recv(state->fd, state->buffer + state->used, sizeof(state->buffer) - (size_t)state->used, MSG_DONTWAIT);
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

        return -1;
    }
}

static int serve_fdpass_epoll(const char *control_path, index_t *index, const settings_t *settings) {
    int listener = create_unix_listener(control_path);
    if (listener < 0) {
        return 1;
    }

    (void)set_nonblocking(listener);
    int epoll_fd = epoll_create1(EPOLL_CLOEXEC);
    if (epoll_fd < 0) {
        perror("epoll_create1");
        close(listener);
        return 1;
    }

    epoll_state_t listener_state;
    memset(&listener_state, 0, sizeof(listener_state));
    listener_state.kind = EPOLL_LISTENER;
    listener_state.fd = listener;
    if (add_epoll_state(epoll_fd, &listener_state) < 0) {
        perror("epoll_ctl listener");
        close(listener);
        close(epoll_fd);
        return 1;
    }

    fprintf(stderr,
        "serving native epoll fd control on %s, index_count=%d, risky=%d, early=%d min=%d max=%d\n",
        control_path,
        index->count,
        index->risky_count,
        settings->early_candidates,
        settings->min_candidates,
        settings->max_candidates);

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
            uint32_t event_flags = events[i].events;
            if (state == &listener_state) {
                accept_control_epoll(epoll_fd, listener);
                continue;
            }

            if ((event_flags & (EPOLLERR | EPOLLHUP | EPOLLRDHUP)) != 0) {
                close_epoll_state(epoll_fd, state);
                continue;
            }

            if (state->kind == EPOLL_CONTROL) {
                if (handle_control_epoll(epoll_fd, state) < 0) {
                    close_epoll_state(epoll_fd, state);
                }
            } else if (state->kind == EPOLL_CLIENT) {
                if (handle_client_epoll(state, index, settings) < 0) {
                    close_epoll_state(epoll_fd, state);
                }
            }
        }
    }

    close(listener);
    close(epoll_fd);
    return 1;
}

static int serve_fdpass(const char *control_path, index_t *index, const settings_t *settings) {
    int listener = create_unix_listener(control_path);
    if (listener < 0) {
        return 1;
    }

    fprintf(stderr,
        "serving native fd control on %s, index_count=%d, risky=%d, early=%d min=%d max=%d\n",
        control_path,
        index->count,
        index->risky_count,
        settings->early_candidates,
        settings->min_candidates,
        settings->max_candidates);

    for (;;) {
        int fd = accept4(listener, NULL, NULL, SOCK_CLOEXEC);
        if (fd < 0) {
            if (errno == EINTR) {
                continue;
            }

            perror("accept control");
            continue;
        }

        (void)start_control_thread(fd, index, settings);
    }
}

static int object_end(const char *s, int len, int start) {
    int p = start;
    if (p >= len || s[p] != '{') {
        return -1;
    }

    int depth = 0;
    int in_string = 0;
    int escaped = 0;
    for (; p < len; p++) {
        char c = s[p];
        if (in_string) {
            if (escaped) {
                escaped = 0;
            } else if (c == '\\') {
                escaped = 1;
            } else if (c == '"') {
                in_string = 0;
            }

            continue;
        }

        if (c == '"') {
            in_string = 1;
        } else if (c == '{') {
            depth++;
        } else if (c == '}') {
            depth--;
            if (depth == 0) {
                return p;
            }
        }
    }

    return -1;
}

static int bool_after_key(const char *s, int len, int start, const char *key, int *value) {
    const char *found = memmem(s + start, (size_t)(len - start), key, strlen(key));
    if (found == NULL) {
        return 0;
    }

    int p = (int)(found - s) + (int)strlen(key);
    while (p < len && s[p] != ':') p++;
    if (p >= len) return 0;
    p++;
    return read_bool(s, len, &p, value);
}

static int run_eval(index_t *index, const settings_t *settings, const char *path) {
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        perror("open eval");
        return 1;
    }

    struct stat st;
    if (fstat(fd, &st) < 0 || st.st_size <= 0) {
        perror("stat eval");
        close(fd);
        return 1;
    }

    char *data = mmap(NULL, (size_t)st.st_size, PROT_READ, MAP_PRIVATE, fd, 0);
    if (data == MAP_FAILED) {
        perror("mmap eval");
        close(fd);
        return 1;
    }

    int len = (int)st.st_size;
    int cursor = 0;
    int total = 0;
    int fp = 0;
    int fn = 0;
    int parse_errors = 0;
    while (cursor < len) {
        const char *found = memmem(data + cursor, (size_t)(len - cursor), "\"request\"", 9);
        if (found == NULL) {
            break;
        }

        int request_key = (int)(found - data);
        int p = request_key + 9;
        while (p < len && data[p] != ':') p++;
        if (p >= len) break;
        p++;
        p = skip_ws(data, len, p);
        int end = object_end(data, len, p);
        if (end < 0) {
            parse_errors++;
            cursor = request_key + 9;
            continue;
        }

        int expected_approved = 1;
        if (!bool_after_key(data, len, end + 1, "\"expected_approved\"", &expected_approved)) {
            break;
        }

        int16_t query[PADDED_DIM] = {0};
        int approved = 1;
        if (build_query(data + p, end - p + 1, query)) {
            int frauds = classify(index, settings, query);
            approved = frauds < 3;
        } else {
            parse_errors++;
        }

        if (approved != expected_approved) {
            if (approved) {
                fn++;
            } else {
                fp++;
            }
        }

        total++;
        cursor = end + 1;
    }

    printf("native_eval total=%d fp=%d fn=%d parse_errors=%d\n", total, fp, fn, parse_errors);
    munmap(data, (size_t)st.st_size);
    close(fd);
    return (fp == 0 && fn == 0 && parse_errors == 0) ? 0 : 2;
}

int main(int argc, char **argv) {
    signal(SIGPIPE, SIG_IGN);
    const char *index_path = getenv("INDEX_PATH");
    if (index_path == NULL || *index_path == '\0') {
        index_path = "/app/data/references.idx";
    }

    index_t index;
    if (open_index(index_path, &index) != 0) {
        return 1;
    }

    settings_t settings = load_settings();
    if (argc >= 3 && strcmp(argv[1], "eval") == 0) {
        int rc = run_eval(&index, &settings, argv[2]);
        close_index(&index);
        return rc;
    }

    const char *bind_addr = getenv("BIND_ADDR");
    if (bind_addr == NULL || *bind_addr == '\0') {
        bind_addr = "fd:/sockets/api1.sock.ctrl";
    }

    int rc;
    if (strncmp(bind_addr, "fd:", 3) == 0) {
        rc = env_bool("NATIVE_EPOLL", 0)
            ? serve_fdpass_epoll(bind_addr + 3, &index, &settings)
            : serve_fdpass(bind_addr + 3, &index, &settings);
    } else {
        fprintf(stderr, "native API currently supports BIND_ADDR=fd:<path> only\n");
        rc = 1;
    }

    close_index(&index);
    return rc;
}
