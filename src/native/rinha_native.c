#include <immintrin.h>
#include <limits.h>
#include <stdint.h>
#include <string.h>

#define DIM 14
#define RISKY_STRIDE 16
#define BUCKET_COUNT 4096
#define FINE_EXTRA_BITS 3
#define FINE_PER_COARSE (1 << FINE_EXTRA_BITS)
#define SCALE 10000
#define K 5
#define KD_PARTITION_COUNT 256
#define KD_VECTOR_STRIDE 16
#define KD_BLOCK_LANES 8
#define KD_BLOCK_VECTOR_STRIDE (DIM * KD_BLOCK_LANES)
#define KD_PARTITION_RECORD_SIZE 72
#define KD_NODE_RECORD_SIZE 80
#define KD_STACK_SIZE 128
#define KD_STATS_LENGTH 9
#define PROFILE_LEGIT_MASK 1
#define PROFILE_FRAUD_MASK 2
#ifndef KDTREE_KEY_PROFILE
#define KDTREE_KEY_PROFILE 0
#endif
#ifndef KD_BEST_FIRST
#define KD_BEST_FIRST 0
#endif
#ifndef KD_NODE_QUEUE_SIZE
#define KD_NODE_QUEUE_SIZE 1024
#endif
#ifndef KD_SCALAR_EARLY
#define KD_SCALAR_EARLY 0
#endif
#ifndef JSON_FIXED_NUMBERS
#define JSON_FIXED_NUMBERS 0
#endif



typedef struct {
    int32_t searched_partitions;
    int32_t candidate_partitions;
    int32_t visited_nodes;
    int32_t pruned_nodes;
    int32_t scanned_leaves;
    int32_t scanned_vectors;
    int32_t max_stack_depth;
    int32_t primary_partition;
} kd_stats_t;

static inline int64_t distance_mapped_avx2(const int16_t *vector, const int16_t *query) {
    const __m256i mask = _mm256_setr_epi16(
        -1, -1, -1, -1,
        -1, -1, -1, -1,
        -1, -1, -1, -1,
        -1, -1, 0, 0);
    __m256i q = _mm256_loadu_si256((const __m256i *)query);
    __m256i v = _mm256_loadu_si256((const __m256i *)vector);
    __m256i diff = _mm256_and_si256(_mm256_sub_epi16(q, v), mask);
    __m256i pairs = _mm256_madd_epi16(diff, diff);

    return (int64_t)_mm256_extract_epi32(pairs, 0) +
           (int64_t)_mm256_extract_epi32(pairs, 1) +
           (int64_t)_mm256_extract_epi32(pairs, 2) +
           (int64_t)_mm256_extract_epi32(pairs, 3) +
           (int64_t)_mm256_extract_epi32(pairs, 4) +
           (int64_t)_mm256_extract_epi32(pairs, 5) +
           (int64_t)_mm256_extract_epi32(pairs, 6) +
           (int64_t)_mm256_extract_epi32(pairs, 7);
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

static inline int64_t binary_distance_squared(int16_t value, int bit) {
    int exact = bit == 0 ? 0 : SCALE;
    return range_distance_squared(value, exact, exact);
}

static inline int64_t distance_risky_avx2(const int16_t *vector, const int16_t *query) {
    __m256i q = _mm256_loadu_si256((const __m256i *)query);
    __m256i v = _mm256_loadu_si256((const __m256i *)vector);
    __m256i diff = _mm256_sub_epi16(q, v);
    __m256i pairs = _mm256_madd_epi16(diff, diff);

    return (int64_t)_mm256_extract_epi32(pairs, 0) +
           (int64_t)_mm256_extract_epi32(pairs, 1) +
           (int64_t)_mm256_extract_epi32(pairs, 2) +
           (int64_t)_mm256_extract_epi32(pairs, 3) +
           (int64_t)_mm256_extract_epi32(pairs, 4) +
           (int64_t)_mm256_extract_epi32(pairs, 5) +
           (int64_t)_mm256_extract_epi32(pairs, 6) +
           (int64_t)_mm256_extract_epi32(pairs, 7);
}

static inline void insert_candidate(int64_t dist, uint8_t label, int64_t *top_dist, uint8_t *top_label) {
    if (dist < top_dist[0]) {
        top_dist[4] = top_dist[3];
        top_dist[3] = top_dist[2];
        top_dist[2] = top_dist[1];
        top_dist[1] = top_dist[0];
        top_dist[0] = dist;
        top_label[4] = top_label[3];
        top_label[3] = top_label[2];
        top_label[2] = top_label[1];
        top_label[1] = top_label[0];
        top_label[0] = label;
    } else if (dist < top_dist[1]) {
        top_dist[4] = top_dist[3];
        top_dist[3] = top_dist[2];
        top_dist[2] = top_dist[1];
        top_dist[1] = dist;
        top_label[4] = top_label[3];
        top_label[3] = top_label[2];
        top_label[2] = top_label[1];
        top_label[1] = label;
    } else if (dist < top_dist[2]) {
        top_dist[4] = top_dist[3];
        top_dist[3] = top_dist[2];
        top_dist[2] = dist;
        top_label[4] = top_label[3];
        top_label[3] = top_label[2];
        top_label[2] = label;
    } else if (dist < top_dist[3]) {
        top_dist[4] = top_dist[3];
        top_dist[3] = dist;
        top_label[4] = top_label[3];
        top_label[3] = label;
    } else if (dist < top_dist[4]) {
        top_dist[4] = dist;
        top_label[4] = label;
    }
}

static inline int strong_decision(const uint8_t *top_label, int include_edges) {
    int frauds = (int)top_label[0] +
                 (int)top_label[1] +
                 (int)top_label[2] +
                 (int)top_label[3] +
                 (int)top_label[4];
    return include_edges ? (frauds <= 1 || frauds >= K - 1) : (frauds == 0 || frauds == K);
}

__attribute__((visibility("default")))
int32_t rinha_consider_ann_avx2(
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
    uint8_t *top_label) {
    int32_t candidates = 0;

    for (int neighbor_index = 0; neighbor_index < BUCKET_COUNT; neighbor_index++) {
        int key = neighbor_keys[neighbor_index];
        int start = bucket_offsets[key];
        int end = bucket_offsets[key + 1];
        int scan_end = end;
        int remaining = max_candidates - candidates;
        if (end - start > remaining) {
            scan_end = start + remaining;
        }

        for (int id = start; id < scan_end; id++) {
            int64_t dist = distance_mapped_avx2(vectors + ((int64_t)id * DIM), query);
            if (dist < top_dist[K - 1]) {
                insert_candidate(dist, labels[id], top_dist, top_label);
            }
        }

        candidates += scan_end - start;
        if (candidates >= max_candidates) {
            return candidates;
        }

        if (candidates >= early_candidates && strong_decision(top_label, early_edge_fallback)) {
            return candidates;
        }

        if (candidates >= min_candidates) {
            return candidates;
        }
    }

    return candidates;
}

__attribute__((visibility("default")))
int32_t rinha_classify_ann_avx2(
    const int16_t *vectors,
    const uint8_t *labels,
    const int32_t *bucket_offsets,
    const uint16_t *neighbor_keys,
    const int16_t *query,
    int32_t early_candidates,
    int32_t min_candidates,
    int32_t max_candidates,
    int32_t early_edge_fallback) {
    int64_t top_dist[K] = {
        INT64_MAX,
        INT64_MAX,
        INT64_MAX,
        INT64_MAX,
        INT64_MAX
    };
    uint8_t top_label[K] = {0, 0, 0, 0, 0};
    int32_t candidates = 0;

    for (int neighbor_index = 0; neighbor_index < BUCKET_COUNT; neighbor_index++) {
        int key = neighbor_keys[neighbor_index];
        int start = bucket_offsets[key];
        int end = bucket_offsets[key + 1];
        int scan_end = end;
        int remaining = max_candidates - candidates;
        if (end - start > remaining) {
            scan_end = start + remaining;
        }

        for (int id = start; id < scan_end; id++) {
            int64_t dist = distance_mapped_avx2(vectors + ((int64_t)id * DIM), query);
            if (dist < top_dist[K - 1]) {
                insert_candidate(dist, labels[id], top_dist, top_label);
            }
        }

        candidates += scan_end - start;
        if (candidates >= max_candidates) {
            break;
        }

        if (candidates >= early_candidates && strong_decision(top_label, early_edge_fallback)) {
            break;
        }

        if (candidates >= min_candidates) {
            break;
        }
    }

    int frauds = (int)top_label[0] +
                 (int)top_label[1] +
                 (int)top_label[2] +
                 (int)top_label[3] +
                 (int)top_label[4];
    return (candidates << 3) | frauds;
}

__attribute__((visibility("default")))
int32_t rinha_consider_risky_fine_avx2(
    const int16_t *vectors,
    const uint8_t *labels,
    const int32_t *fine_bucket_offsets,
    const int32_t *coarse_fine_offsets,
    const int32_t *fine_keys,
    const uint16_t *neighbor_keys,
    const int16_t *query,
    int64_t *top_dist,
    uint8_t *top_label) {
    int64_t amount_bounds[8];
    int64_t ratio_bounds[8];
    int64_t km_home_bounds[8];
    int64_t hour_bounds[4];
    int64_t last_bounds[2];
    int64_t fine_extra_bounds[FINE_PER_COARSE];
    int32_t fallback_candidates = 0;

    for (int bucket = 0; bucket < 8; bucket++) {
        amount_bounds[bucket] = bucket_distance_squared(query[0], bucket, 8);
        ratio_bounds[bucket] = bucket_distance_squared(query[2], bucket, 8);
        km_home_bounds[bucket] = bucket_distance_squared(query[7], bucket, 8);
    }

    for (int bucket = 0; bucket < 4; bucket++) {
        hour_bounds[bucket] = bucket_distance_squared(query[3], bucket, 4);
    }

    last_bounds[0] = range_distance_squared(query[5], 0, SCALE);
    last_bounds[1] = range_distance_squared(query[5], -SCALE, -SCALE);

    for (int extra = 0; extra < FINE_PER_COARSE; extra++) {
        fine_extra_bounds[extra] =
            binary_distance_squared(query[9], extra & 1) +
            binary_distance_squared(query[10], (extra >> 1) & 1) +
            binary_distance_squared(query[11], (extra >> 2) & 1);
    }

    for (int neighbor_index = 0; neighbor_index < BUCKET_COUNT; neighbor_index++) {
        int coarse_key = neighbor_keys[neighbor_index];
        int fine_start = coarse_fine_offsets[coarse_key];
        int fine_end = coarse_fine_offsets[coarse_key + 1];
        if (fine_start == fine_end) {
            continue;
        }

        int64_t coarse_lower_bound =
            amount_bounds[coarse_key & 7] +
            ratio_bounds[(coarse_key >> 3) & 7] +
            km_home_bounds[(coarse_key >> 6) & 7] +
            hour_bounds[(coarse_key >> 9) & 3] +
            last_bounds[(coarse_key >> 11) & 1];
        if (coarse_lower_bound >= top_dist[K - 1]) {
            continue;
        }

        int32_t ordered_keys[FINE_PER_COARSE];
        int64_t ordered_bounds[FINE_PER_COARSE];
        int ordered_count = 0;
        for (int fine_pos = fine_start; fine_pos < fine_end; fine_pos++) {
            int fine_key = fine_keys[fine_pos];
            int64_t lower_bound = coarse_lower_bound + fine_extra_bounds[fine_key & (FINE_PER_COARSE - 1)];
            if (lower_bound >= top_dist[K - 1]) {
                continue;
            }

            int insert_at = ordered_count;
            while (insert_at > 0 && lower_bound < ordered_bounds[insert_at - 1]) {
                ordered_keys[insert_at] = ordered_keys[insert_at - 1];
                ordered_bounds[insert_at] = ordered_bounds[insert_at - 1];
                insert_at--;
            }

            ordered_keys[insert_at] = fine_key;
            ordered_bounds[insert_at] = lower_bound;
            ordered_count++;
        }

        for (int ordered_pos = 0; ordered_pos < ordered_count; ordered_pos++) {
            if (ordered_bounds[ordered_pos] >= top_dist[K - 1]) {
                break;
            }

            int fine_key = ordered_keys[ordered_pos];
            int start = fine_bucket_offsets[fine_key];
            int end = fine_bucket_offsets[fine_key + 1];
            fallback_candidates += end - start;
            for (int pos = start; pos < end; pos++) {
                int64_t dist = distance_risky_avx2(vectors + ((int64_t)pos * RISKY_STRIDE), query);
                if (dist < top_dist[K - 1]) {
                    insert_candidate(dist, labels[pos], top_dist, top_label);
                }
            }
        }
    }

    return fallback_candidates;
}

typedef struct {
    int index;
    int64_t bound;
} kd_partition_candidate_t;

typedef struct {
    int node;
    int64_t bound;
} kd_node_candidate_t;

static inline int32_t kd_rd32s(const uint8_t *p) {
    int32_t value;
    memcpy(&value, p, sizeof(value));
    return value;
}

static inline int kd_bucket4(int16_t value) {
    if (value <= 0) {
        return 0;
    }

    int bucket = (int)value * 4 / (SCALE + 1);
    return bucket < 0 ? 0 : bucket > 3 ? 3 : bucket;
}

static inline int kd_bucket8(int16_t value) {
    if (value <= 0) {
        return 0;
    }

    int bucket = (int)value * 8 / (SCALE + 1);
    return bucket < 0 ? 0 : bucket > 7 ? 7 : bucket;
}

static inline int kd_bucket16(int16_t value) {
    if (value <= 0) {
        return 0;
    }

    int bucket = (int)value * 16 / (SCALE + 1);
    return bucket < 0 ? 0 : bucket > 15 ? 15 : bucket;
}

static inline int kd_partition_base_flags(const int16_t *v) {
    int key = v[5] < 0 ? 1 : 0;
    key |= (v[9] > 0 ? 1 : 0) << 1;
    key |= (v[10] > 0 ? 1 : 0) << 2;
    key |= (v[11] > 0 ? 1 : 0) << 3;
    return key;
}

static inline int kd_partition_key(const int16_t *v) {
#if KDTREE_KEY_PROFILE == 1
    int key = kd_partition_base_flags(v);
    key |= kd_bucket8(v[0]) << 4;
    key |= (v[2] >= 3500 ? 1 : 0) << 7;
    return key;
#elif KDTREE_KEY_PROFILE == 2
    int key = kd_partition_base_flags(v);
    key |= kd_bucket4(v[0]) << 4;
    key |= kd_bucket4(v[2]) << 6;
    return key;
#elif KDTREE_KEY_PROFILE == 3
    int key = kd_partition_base_flags(v);
    key |= kd_bucket4(v[0]) << 4;
    key |= kd_bucket4(v[7]) << 6;
    return key;
#elif KDTREE_KEY_PROFILE == 4
    int key = kd_partition_base_flags(v);
    key |= kd_bucket4(v[0]) << 4;
    key |= kd_bucket4(v[3]) << 6;
    return key;
#elif KDTREE_KEY_PROFILE == 5
    int key = kd_partition_base_flags(v);
    key |= kd_bucket4(v[0]) << 4;
    key |= kd_bucket4(v[12]) << 6;
    return key;
#elif KDTREE_KEY_PROFILE == 6
    int key = kd_partition_base_flags(v);
    key |= kd_bucket4(v[0]) << 4;
    key |= kd_bucket4(v[13]) << 6;
    return key;
#elif KDTREE_KEY_PROFILE == 7
    int key = kd_partition_base_flags(v);
    key |= kd_bucket8(v[0]) << 4;
    key |= (v[7] >= 3500 ? 1 : 0) << 7;
    return key;
#elif KDTREE_KEY_PROFILE == 8
    int key = kd_partition_base_flags(v);
    key |= kd_bucket8(v[0]) << 4;
    key |= (v[3] >= 3500 ? 1 : 0) << 7;
    return key;
#elif KDTREE_KEY_PROFILE == 9
    int key = kd_partition_base_flags(v);
    key |= kd_bucket8(v[0]) << 4;
    key |= (v[12] >= 3500 ? 1 : 0) << 7;
    return key;
#elif KDTREE_KEY_PROFILE == 10
    int key = kd_partition_base_flags(v);
    key |= kd_bucket8(v[0]) << 4;
    key |= (v[13] >= 3500 ? 1 : 0) << 7;
    return key;
#else
    int key = kd_partition_base_flags(v);
    key |= kd_bucket8(v[0]) << 4;
    key |= (v[8] >= 3500 ? 1 : 0) << 7;
    return key;
#endif
}

static inline const uint8_t *kd_partition_ptr(const uint8_t *partitions, int partition) {
    return partitions + (int64_t)partition * KD_PARTITION_RECORD_SIZE;
}

static inline const uint8_t *kd_node_ptr(const uint8_t *nodes, int node) {
    return nodes + (int64_t)node * KD_NODE_RECORD_SIZE;
}

static inline int64_t kd_bounds_lower_bound(const int16_t *q, const int16_t *min, const int16_t *max) {
    const __m256i dim_mask = _mm256_setr_epi16(
        -1, -1, -1, -1,
        -1, -1, -1, -1,
        -1, -1, -1, -1,
        -1, -1, 0, 0);
    __m256i qv = _mm256_loadu_si256((const __m256i *)q);
    __m256i minv = _mm256_loadu_si256((const __m256i *)min);
    __m256i maxv = _mm256_loadu_si256((const __m256i *)max);
    __m256i below = _mm256_and_si256(_mm256_sub_epi16(minv, qv), _mm256_cmpgt_epi16(minv, qv));
    __m256i above = _mm256_and_si256(_mm256_sub_epi16(qv, maxv), _mm256_cmpgt_epi16(qv, maxv));
    __m256i diff = _mm256_and_si256(_mm256_or_si256(below, above), dim_mask);
    __m256i pairs = _mm256_madd_epi16(diff, diff);

    return (int64_t)_mm256_extract_epi32(pairs, 0) +
           (int64_t)_mm256_extract_epi32(pairs, 1) +
           (int64_t)_mm256_extract_epi32(pairs, 2) +
           (int64_t)_mm256_extract_epi32(pairs, 3) +
           (int64_t)_mm256_extract_epi32(pairs, 4) +
           (int64_t)_mm256_extract_epi32(pairs, 5) +
           (int64_t)_mm256_extract_epi32(pairs, 6) +
           (int64_t)_mm256_extract_epi32(pairs, 7);
}

static inline int64_t distance_risky_scalar_limit(const int16_t *vector, const int16_t *query, int64_t limit) {
    int64_t sum = 0;
    for (int dim = 0; dim < DIM; dim++) {
        int64_t diff = (int64_t)query[dim] - vector[dim];
        sum += diff * diff;
        if (sum > limit) {
            return sum;
        }
    }

    return sum;
}

static inline int kd_candidate_better(
    int64_t dist,
    int32_t id,
    int position,
    const int64_t *top_dist,
    const int32_t *top_id) {
    return dist < top_dist[position] || (dist == top_dist[position] && id < top_id[position]);
}

static inline void kd_insert_candidate(
    int64_t dist,
    uint8_t label,
    int32_t id,
    int64_t *top_dist,
    uint8_t *top_label,
    int32_t *top_id) {
    if (!kd_candidate_better(dist, id, K - 1, top_dist, top_id)) {
        return;
    }

    int pos = K - 1;
    while (pos > 0 && kd_candidate_better(dist, id, pos - 1, top_dist, top_id)) {
        top_dist[pos] = top_dist[pos - 1];
        top_label[pos] = top_label[pos - 1];
        top_id[pos] = top_id[pos - 1];
        pos--;
    }

    top_dist[pos] = dist;
    top_label[pos] = label;
    top_id[pos] = id;
}

static void kd_scan_leaf(
    const int16_t *vectors,
    const uint8_t *labels,
    const int32_t *ids,
    const int16_t *query,
    int start,
    int count,
    int64_t *top_dist,
    uint8_t *top_label,
    int32_t *top_id) {
    int end = start + count;
    for (int pos = start; pos < end; pos++) {
#if KD_SCALAR_EARLY
        int64_t dist = distance_risky_scalar_limit(vectors + (int64_t)pos * KD_VECTOR_STRIDE, query, top_dist[K - 1]);
#else
        int64_t dist = distance_risky_avx2(vectors + (int64_t)pos * KD_VECTOR_STRIDE, query);
#endif
        int32_t id = ids[pos];
        if (kd_candidate_better(dist, id, K - 1, top_dist, top_id)) {
            kd_insert_candidate(dist, labels[pos], id, top_dist, top_label, top_id);
        }
    }
}

static void kd_search_node(
    const uint8_t *nodes,
    const int16_t *vectors,
    const uint8_t *labels,
    const int32_t *ids,
    int node_count,
    const int16_t *query,
    int root,
    int64_t *top_dist,
    uint8_t *top_label,
    int32_t *top_id) {
    int stack[KD_STACK_SIZE];
    int sp = 0;
    if (root >= 0) {
        stack[sp++] = root;
    }

    while (sp > 0) {
        int node_index = stack[--sp];
        if (node_index < 0 || node_index >= node_count) {
            continue;
        }

        const uint8_t *node = kd_node_ptr(nodes, node_index);
        const int16_t *min = (const int16_t *)(node + 16);
        const int16_t *max = (const int16_t *)(node + 48);
        int64_t bound = kd_bounds_lower_bound(query, min, max);
        if (bound > top_dist[K - 1]) {
            continue;
        }

        int left = kd_rd32s(node);
        int right = kd_rd32s(node + 4);
        int start = kd_rd32s(node + 8);
        int count = kd_rd32s(node + 12);
        if (left < 0 && right < 0) {
            kd_scan_leaf(vectors, labels, ids, query, start, count, top_dist, top_label, top_id);
            continue;
        }

        const uint8_t *left_node = left >= 0 ? kd_node_ptr(nodes, left) : 0;
        const uint8_t *right_node = right >= 0 ? kd_node_ptr(nodes, right) : 0;
        int64_t left_bound = left_node != 0
            ? kd_bounds_lower_bound(query, (const int16_t *)(left_node + 16), (const int16_t *)(left_node + 48))
            : INT64_MAX;
        int64_t right_bound = right_node != 0
            ? kd_bounds_lower_bound(query, (const int16_t *)(right_node + 16), (const int16_t *)(right_node + 48))
            : INT64_MAX;

        if (left_bound <= right_bound) {
            if (right_bound <= top_dist[K - 1] && sp < KD_STACK_SIZE) stack[sp++] = right;
            if (left_bound <= top_dist[K - 1] && sp < KD_STACK_SIZE) stack[sp++] = left;
        } else {
            if (left_bound <= top_dist[K - 1] && sp < KD_STACK_SIZE) stack[sp++] = left;
            if (right_bound <= top_dist[K - 1] && sp < KD_STACK_SIZE) stack[sp++] = right;
        }
    }
}

#if KD_BEST_FIRST
static inline void kd_node_heap_push(
    kd_node_candidate_t *heap,
    int *count,
    int node,
    int64_t bound) {
    int pos = (*count)++;
    while (pos > 0) {
        int parent = (pos - 1) >> 1;
        if (heap[parent].bound <= bound) {
            break;
        }

        heap[pos] = heap[parent];
        pos = parent;
    }

    heap[pos].node = node;
    heap[pos].bound = bound;
}

static inline kd_node_candidate_t kd_node_heap_pop(kd_node_candidate_t *heap, int *count) {
    kd_node_candidate_t result = heap[0];
    kd_node_candidate_t tail = heap[--(*count)];
    int pos = 0;
    for (;;) {
        int left = (pos << 1) + 1;
        if (left >= *count) {
            break;
        }

        int right = left + 1;
        int child = (right < *count && heap[right].bound < heap[left].bound) ? right : left;
        if (heap[child].bound >= tail.bound) {
            break;
        }

        heap[pos] = heap[child];
        pos = child;
    }

    if (*count > 0) {
        heap[pos] = tail;
    }

    return result;
}

static void kd_search_node_best_first(
    const uint8_t *nodes,
    const int16_t *vectors,
    const uint8_t *labels,
    const int32_t *ids,
    int node_count,
    const int16_t *query,
    int root,
    int64_t *top_dist,
    uint8_t *top_label,
    int32_t *top_id) {
    if (root < 0 || root >= node_count) {
        return;
    }

    kd_node_candidate_t heap[KD_NODE_QUEUE_SIZE];
    int heap_count = 0;
    const uint8_t *root_node = kd_node_ptr(nodes, root);
    kd_node_heap_push(
        heap,
        &heap_count,
        root,
        kd_bounds_lower_bound(query, (const int16_t *)(root_node + 16), (const int16_t *)(root_node + 48)));

    while (heap_count > 0) {
        kd_node_candidate_t current = kd_node_heap_pop(heap, &heap_count);
        if (current.bound > top_dist[K - 1]) {
            break;
        }

        int node_index = current.node;
        if (node_index < 0 || node_index >= node_count) {
            continue;
        }

        const uint8_t *node = kd_node_ptr(nodes, node_index);
        int left = kd_rd32s(node);
        int right = kd_rd32s(node + 4);
        int start = kd_rd32s(node + 8);
        int count = kd_rd32s(node + 12);
        if (left < 0 && right < 0) {
            kd_scan_leaf(vectors, labels, ids, query, start, count, top_dist, top_label, top_id);
            continue;
        }

        if (left >= 0) {
            const uint8_t *left_node = kd_node_ptr(nodes, left);
            int64_t left_bound = kd_bounds_lower_bound(query, (const int16_t *)(left_node + 16), (const int16_t *)(left_node + 48));
            if (left_bound <= top_dist[K - 1]) {
                if (heap_count >= KD_NODE_QUEUE_SIZE) {
                    kd_search_node(nodes, vectors, labels, ids, node_count, query, root, top_dist, top_label, top_id);
                    return;
                }

                kd_node_heap_push(heap, &heap_count, left, left_bound);
            }
        }

        if (right >= 0) {
            const uint8_t *right_node = kd_node_ptr(nodes, right);
            int64_t right_bound = kd_bounds_lower_bound(query, (const int16_t *)(right_node + 16), (const int16_t *)(right_node + 48));
            if (right_bound <= top_dist[K - 1]) {
                if (heap_count >= KD_NODE_QUEUE_SIZE) {
                    kd_search_node(nodes, vectors, labels, ids, node_count, query, root, top_dist, top_label, top_id);
                    return;
                }

                kd_node_heap_push(heap, &heap_count, right, right_bound);
            }
        }
    }
}
#endif

static inline int64_t distance_kd_block_lane_scalar(const int16_t *block_vectors, int pos, const int16_t *query) {
    int block = pos / KD_BLOCK_LANES;
    int lane = pos & (KD_BLOCK_LANES - 1);
    const int16_t *base = block_vectors + (int64_t)block * KD_BLOCK_VECTOR_STRIDE;
    int64_t sum = 0;
    for (int dim = 0; dim < DIM; dim++) {
        int64_t diff = (int64_t)query[dim] - base[(dim >> 1) * KD_BLOCK_LANES * 2 + lane * 2 + (dim & 1)];
        sum += diff * diff;
    }

    return sum;
}

static inline void distance_kd_block8_avx2(const int16_t *block, const int16_t *query, int32_t *dist) {
    __m256i acc = _mm256_setzero_si256();
    for (int pair = 0; pair < DIM / 2; pair++) {
        uint32_t lo = (uint16_t)query[pair * 2];
        uint32_t hi = (uint16_t)query[pair * 2 + 1];
        __m256i q = _mm256_set1_epi32((int)(lo | (hi << 16)));
        __m256i refs = _mm256_loadu_si256((const __m256i *)(block + pair * KD_BLOCK_LANES * 2));
        __m256i diff = _mm256_sub_epi16(q, refs);
        acc = _mm256_add_epi32(acc, _mm256_madd_epi16(diff, diff));
    }

    _mm256_storeu_si256((__m256i *)dist, acc);
}

static void kd_scan_leaf_block(
    const int16_t *block_vectors,
    const uint8_t *labels,
    const int32_t *ids,
    const int16_t *query,
    int start,
    int count,
    int64_t *top_dist,
    uint8_t *top_label,
    int32_t *top_id) {
    int pos = start;
    int end = start + count;

    while (pos < end && (pos & (KD_BLOCK_LANES - 1)) != 0) {
        int64_t dist = distance_kd_block_lane_scalar(block_vectors, pos, query);
        int32_t id = ids[pos];
        if (kd_candidate_better(dist, id, K - 1, top_dist, top_id)) {
            kd_insert_candidate(dist, labels[pos], id, top_dist, top_label, top_id);
        }
        pos++;
    }

    while (pos + KD_BLOCK_LANES <= end) {
        int32_t dists[KD_BLOCK_LANES];
        const int16_t *block = block_vectors + ((int64_t)pos / KD_BLOCK_LANES) * KD_BLOCK_VECTOR_STRIDE;
        distance_kd_block8_avx2(block, query, dists);
        for (int lane = 0; lane < KD_BLOCK_LANES; lane++) {
            int64_t dist = dists[lane];
            int id_pos = pos + lane;
            int32_t id = ids[id_pos];
            if (kd_candidate_better(dist, id, K - 1, top_dist, top_id)) {
                kd_insert_candidate(dist, labels[id_pos], id, top_dist, top_label, top_id);
            }
        }
        pos += KD_BLOCK_LANES;
    }

    while (pos < end) {
        int64_t dist = distance_kd_block_lane_scalar(block_vectors, pos, query);
        int32_t id = ids[pos];
        if (kd_candidate_better(dist, id, K - 1, top_dist, top_id)) {
            kd_insert_candidate(dist, labels[pos], id, top_dist, top_label, top_id);
        }
        pos++;
    }
}

static void kd_search_node_block(
    const uint8_t *nodes,
    const int16_t *block_vectors,
    const uint8_t *labels,
    const int32_t *ids,
    int node_count,
    const int16_t *query,
    int root,
    int64_t *top_dist,
    uint8_t *top_label,
    int32_t *top_id) {
    int stack[KD_STACK_SIZE];
    int sp = 0;
    if (root >= 0) {
        stack[sp++] = root;
    }

    while (sp > 0) {
        int node_index = stack[--sp];
        if (node_index < 0 || node_index >= node_count) {
            continue;
        }

        const uint8_t *node = kd_node_ptr(nodes, node_index);
        const int16_t *min = (const int16_t *)(node + 16);
        const int16_t *max = (const int16_t *)(node + 48);
        int64_t bound = kd_bounds_lower_bound(query, min, max);
        if (bound > top_dist[K - 1]) {
            continue;
        }

        int left = kd_rd32s(node);
        int right = kd_rd32s(node + 4);
        int start = kd_rd32s(node + 8);
        int count = kd_rd32s(node + 12);
        if (left < 0 && right < 0) {
            kd_scan_leaf_block(block_vectors, labels, ids, query, start, count, top_dist, top_label, top_id);
            continue;
        }

        const uint8_t *left_node = left >= 0 ? kd_node_ptr(nodes, left) : 0;
        const uint8_t *right_node = right >= 0 ? kd_node_ptr(nodes, right) : 0;
        int64_t left_bound = left_node != 0
            ? kd_bounds_lower_bound(query, (const int16_t *)(left_node + 16), (const int16_t *)(left_node + 48))
            : INT64_MAX;
        int64_t right_bound = right_node != 0
            ? kd_bounds_lower_bound(query, (const int16_t *)(right_node + 16), (const int16_t *)(right_node + 48))
            : INT64_MAX;

        if (left_bound <= right_bound) {
            if (right_bound <= top_dist[K - 1] && sp < KD_STACK_SIZE) stack[sp++] = right;
            if (left_bound <= top_dist[K - 1] && sp < KD_STACK_SIZE) stack[sp++] = left;
        } else {
            if (left_bound <= top_dist[K - 1] && sp < KD_STACK_SIZE) stack[sp++] = left;
            if (right_bound <= top_dist[K - 1] && sp < KD_STACK_SIZE) stack[sp++] = right;
        }
    }
}

#if KD_BEST_FIRST
static void kd_search_node_best_first_block(
    const uint8_t *nodes,
    const int16_t *block_vectors,
    const uint8_t *labels,
    const int32_t *ids,
    int node_count,
    const int16_t *query,
    int root,
    int64_t *top_dist,
    uint8_t *top_label,
    int32_t *top_id) {
    if (root < 0 || root >= node_count) {
        return;
    }

    kd_node_candidate_t heap[KD_NODE_QUEUE_SIZE];
    int heap_count = 0;
    const uint8_t *root_node = kd_node_ptr(nodes, root);
    kd_node_heap_push(
        heap,
        &heap_count,
        root,
        kd_bounds_lower_bound(query, (const int16_t *)(root_node + 16), (const int16_t *)(root_node + 48)));

    while (heap_count > 0) {
        kd_node_candidate_t current = kd_node_heap_pop(heap, &heap_count);
        if (current.bound > top_dist[K - 1]) {
            break;
        }

        int node_index = current.node;
        if (node_index < 0 || node_index >= node_count) {
            continue;
        }

        const uint8_t *node = kd_node_ptr(nodes, node_index);
        int left = kd_rd32s(node);
        int right = kd_rd32s(node + 4);
        int start = kd_rd32s(node + 8);
        int count = kd_rd32s(node + 12);
        if (left < 0 && right < 0) {
            kd_scan_leaf_block(block_vectors, labels, ids, query, start, count, top_dist, top_label, top_id);
            continue;
        }

        if (left >= 0) {
            const uint8_t *left_node = kd_node_ptr(nodes, left);
            int64_t left_bound = kd_bounds_lower_bound(query, (const int16_t *)(left_node + 16), (const int16_t *)(left_node + 48));
            if (left_bound <= top_dist[K - 1]) {
                if (heap_count >= KD_NODE_QUEUE_SIZE) {
                    kd_search_node_block(nodes, block_vectors, labels, ids, node_count, query, root, top_dist, top_label, top_id);
                    return;
                }

                kd_node_heap_push(heap, &heap_count, left, left_bound);
            }
        }

        if (right >= 0) {
            const uint8_t *right_node = kd_node_ptr(nodes, right);
            int64_t right_bound = kd_bounds_lower_bound(query, (const int16_t *)(right_node + 16), (const int16_t *)(right_node + 48));
            if (right_bound <= top_dist[K - 1]) {
                if (heap_count >= KD_NODE_QUEUE_SIZE) {
                    kd_search_node_block(nodes, block_vectors, labels, ids, node_count, query, root, top_dist, top_label, top_id);
                    return;
                }

                kd_node_heap_push(heap, &heap_count, right, right_bound);
            }
        }
    }
}
#endif

static inline void kd_stats_note_stack(kd_stats_t *stats, int sp) {
    if (sp > stats->max_stack_depth) {
        stats->max_stack_depth = sp;
    }
}

static inline void kd_stats_push_if_candidate(
    int child,
    int64_t bound,
    int *stack,
    int *sp,
    const int64_t *top_dist,
    kd_stats_t *stats) {
    if (child < 0) {
        return;
    }

    if (bound <= top_dist[K - 1]) {
        if (*sp < KD_STACK_SIZE) {
            stack[(*sp)++] = child;
            kd_stats_note_stack(stats, *sp);
        }
    } else {
        stats->pruned_nodes++;
    }
}

static void kd_scan_leaf_stats(
    const int16_t *vectors,
    const uint8_t *labels,
    const int32_t *ids,
    const int16_t *query,
    int start,
    int count,
    int64_t *top_dist,
    uint8_t *top_label,
    int32_t *top_id,
    kd_stats_t *stats) {
    stats->scanned_leaves++;
    stats->scanned_vectors += count;

    int end = start + count;
    for (int pos = start; pos < end; pos++) {
        int64_t dist = distance_risky_avx2(vectors + (int64_t)pos * KD_VECTOR_STRIDE, query);
        int32_t id = ids[pos];
        if (kd_candidate_better(dist, id, K - 1, top_dist, top_id)) {
            kd_insert_candidate(dist, labels[pos], id, top_dist, top_label, top_id);
        }
    }
}

static void kd_search_node_stats(
    const uint8_t *nodes,
    const int16_t *vectors,
    const uint8_t *labels,
    const int32_t *ids,
    int node_count,
    const int16_t *query,
    int root,
    int64_t *top_dist,
    uint8_t *top_label,
    int32_t *top_id,
    kd_stats_t *stats) {
    int stack[KD_STACK_SIZE];
    int sp = 0;
    if (root >= 0) {
        stack[sp++] = root;
        kd_stats_note_stack(stats, sp);
    }

    while (sp > 0) {
        int node_index = stack[--sp];
        if (node_index < 0 || node_index >= node_count) {
            continue;
        }

        const uint8_t *node = kd_node_ptr(nodes, node_index);
        stats->visited_nodes++;

        const int16_t *min = (const int16_t *)(node + 16);
        const int16_t *max = (const int16_t *)(node + 48);
        int64_t bound = kd_bounds_lower_bound(query, min, max);
        if (bound > top_dist[K - 1]) {
            stats->pruned_nodes++;
            continue;
        }

        int left = kd_rd32s(node);
        int right = kd_rd32s(node + 4);
        int start = kd_rd32s(node + 8);
        int count = kd_rd32s(node + 12);
        if (left < 0 && right < 0) {
            kd_scan_leaf_stats(vectors, labels, ids, query, start, count, top_dist, top_label, top_id, stats);
            continue;
        }

        const uint8_t *left_node = left >= 0 ? kd_node_ptr(nodes, left) : 0;
        const uint8_t *right_node = right >= 0 ? kd_node_ptr(nodes, right) : 0;
        int64_t left_bound = left_node != 0
            ? kd_bounds_lower_bound(query, (const int16_t *)(left_node + 16), (const int16_t *)(left_node + 48))
            : INT64_MAX;
        int64_t right_bound = right_node != 0
            ? kd_bounds_lower_bound(query, (const int16_t *)(right_node + 16), (const int16_t *)(right_node + 48))
            : INT64_MAX;

        if (left_bound <= right_bound) {
            kd_stats_push_if_candidate(right, right_bound, stack, &sp, top_dist, stats);
            kd_stats_push_if_candidate(left, left_bound, stack, &sp, top_dist, stats);
        } else {
            kd_stats_push_if_candidate(left, left_bound, stack, &sp, top_dist, stats);
            kd_stats_push_if_candidate(right, right_bound, stack, &sp, top_dist, stats);
        }
    }
}

static void kd_insert_partition_candidate(
    kd_partition_candidate_t *candidates,
    int *count,
    int index,
    int64_t bound) {
    int pos = *count;
    while (pos > 0 && bound < candidates[pos - 1].bound) {
        candidates[pos] = candidates[pos - 1];
        pos--;
    }

    candidates[pos].index = index;
    candidates[pos].bound = bound;
    *count += 1;
}

static inline int kd_count_frauds(const uint8_t *top_label) {
    return (int)top_label[0] +
           (int)top_label[1] +
           (int)top_label[2] +
           (int)top_label[3] +
           (int)top_label[4];
}

static inline int profile_key(const int16_t *v) {
    int key = 0;
    key |= kd_bucket16(v[2]);
    key |= kd_bucket8(v[7]) << 4;
    key |= kd_bucket4(v[8]) << 7;
    key |= kd_bucket4(v[12]) << 9;
    key |= kd_bucket4(v[0]) << 11;
    key |= (v[5] < 0 ? 1 : 0) << 13;
    key |= (v[9] > 0 ? 1 : 0) << 14;
    key |= (v[10] > 0 ? 1 : 0) << 15;
    key |= (v[11] > 0 ? 1 : 0) << 16;
    key |= kd_bucket4(v[6]) << 17;
    key |= (v[1] > 1000 ? 1 : 0) << 19;
    key |= kd_bucket4(v[13]) << 20;
    return key;
}

static inline int bucket_key(const int16_t *v) {
    int amount = kd_bucket8(v[0]);
    int ratio = kd_bucket8(v[2]);
    int km_home = kd_bucket8(v[7]);
    int hour = kd_bucket4(v[3]);
    int no_last = v[5] < 0 ? 1 : 0;
    return amount | (ratio << 3) | (km_home << 6) | (hour << 9) | (no_last << 11);
}

static inline int bucket_fast_decision(
    const uint32_t *bucket_counts,
    const uint32_t *bucket_fraud_counts,
    const int16_t *query,
    int32_t legit_min_count,
    int32_t fraud_min_count,
    int32_t fraud_no_last_only) {
    if (bucket_counts == 0 || bucket_fraud_counts == 0) {
        return -1;
    }

    int key = bucket_key(query);
    uint32_t count = bucket_counts[key];
    if (count == 0 || (count < (uint32_t)legit_min_count && count < (uint32_t)fraud_min_count)) {
        return -1;
    }

    uint32_t frauds = bucket_fraud_counts[key];
    if (frauds == 0 && count >= (uint32_t)legit_min_count) {
        return 0;
    }

    if (frauds == count &&
        count >= (uint32_t)fraud_min_count &&
        (!fraud_no_last_only || query[5] < 0)) {
        return K;
    }

    return -1;
}

static inline int json_consume(const uint8_t *body, int len, int *pos, const char *literal, int literal_len) {
    if (*pos < 0 || literal_len < 0 || len - *pos < literal_len) {
        return 0;
    }

    if (memcmp(body + *pos, literal, (size_t)literal_len) != 0) {
        return 0;
    }

    *pos += literal_len;
    return 1;
}

#define JSON_CONSUME(body, len, pos, literal) json_consume((body), (len), (pos), (literal), (int)(sizeof(literal) - 1))

static int json_read_simple_string(const uint8_t *body, int len, int *pos, int *start, int *length) {
    *start = *pos;
    *length = 0;
    while (*pos < len) {
        uint8_t b = body[*pos];
        if (b == (uint8_t)'\\') {
            return 0;
        }

        if (b == (uint8_t)'"') {
            *length = *pos - *start;
            (*pos)++;
            return 1;
        }

        (*pos)++;
    }

    return 0;
}

static int json_skip_simple_string(const uint8_t *body, int len, int *pos) {
    int start = 0;
    int length = 0;
    return json_read_simple_string(body, len, pos, &start, &length);
}

static int json_read_number(const uint8_t *body, int len, int *pos, double *value) {
    if (*pos >= len) {
        return 0;
    }

    int sign = 1;
    if (body[*pos] == (uint8_t)'-') {
        sign = -1;
        (*pos)++;
    } else if (body[*pos] == (uint8_t)'+') {
        (*pos)++;
    }

    uint64_t integer = 0;
    int digits = 0;
    while (*pos < len) {
        uint8_t d = (uint8_t)(body[*pos] - (uint8_t)'0');
        if (d > 9) {
            break;
        }

        integer = integer * 10U + d;
        digits++;
        (*pos)++;
    }

    double result = (double)integer;
    if (*pos < len && body[*pos] == (uint8_t)'.') {
        double scale = 0.1;
        (*pos)++;
        while (*pos < len) {
            uint8_t d = (uint8_t)(body[*pos] - (uint8_t)'0');
            if (d > 9) {
                break;
            }

            result += (double)d * scale;
            scale *= 0.1;
            digits++;
            (*pos)++;
        }
    }

    if (digits == 0) {
        return 0;
    }

    if (*pos < len && (body[*pos] == (uint8_t)'e' || body[*pos] == (uint8_t)'E')) {
        return 0;
    }

    *value = sign < 0 ? -result : result;
    return 1;
}

static int json_read_bool(const uint8_t *body, int len, int *pos, int *value) {
    if (json_consume(body, len, pos, "true", 4)) {
        *value = 1;
        return 1;
    }

    if (json_consume(body, len, pos, "false", 5)) {
        *value = 0;
        return 1;
    }

    return 0;
}

static int json_read_array_slice(const uint8_t *body, int len, int *pos, int *start, int *length) {
    if (*pos >= len || body[*pos] != (uint8_t)'[') {
        return 0;
    }

    *start = *pos;
    int in_string = 0;
    (*pos)++;
    while (*pos < len) {
        uint8_t b = body[*pos];
        if (b == (uint8_t)'\\') {
            return 0;
        }

        if (b == (uint8_t)'"') {
            in_string = !in_string;
        } else if (!in_string && b == (uint8_t)']') {
            (*pos)++;
            *length = *pos - *start;
            return 1;
        }

        (*pos)++;
    }

    return 0;
}

static int json_contains_quoted(const uint8_t *haystack, int haystack_len, const uint8_t *needle, int needle_len) {
    if (needle_len <= 0 || haystack_len < needle_len + 2) {
        return 0;
    }

    int end = haystack_len - needle_len - 1;
    for (int pos = 0; pos < end; pos++) {
        if (haystack[pos] == (uint8_t)'"' &&
            haystack[pos + needle_len + 1] == (uint8_t)'"' &&
            memcmp(haystack + pos + 1, needle, (size_t)needle_len) == 0) {
            return 1;
        }
    }

    return 0;
}

static inline int json_parse2(const uint8_t *text, int *value) {
    uint8_t d0 = (uint8_t)(text[0] - (uint8_t)'0');
    uint8_t d1 = (uint8_t)(text[1] - (uint8_t)'0');
    if (d0 > 9 || d1 > 9) {
        return 0;
    }

    *value = (int)d0 * 10 + (int)d1;
    return 1;
}

static inline int json_parse4(const uint8_t *text, int *value) {
    uint8_t d0 = (uint8_t)(text[0] - (uint8_t)'0');
    uint8_t d1 = (uint8_t)(text[1] - (uint8_t)'0');
    uint8_t d2 = (uint8_t)(text[2] - (uint8_t)'0');
    uint8_t d3 = (uint8_t)(text[3] - (uint8_t)'0');
    if (d0 > 9 || d1 > 9 || d2 > 9 || d3 > 9) {
        return 0;
    }

    *value = (int)d0 * 1000 + (int)d1 * 100 + (int)d2 * 10 + (int)d3;
    return 1;
}

static int json_parse_time(const uint8_t *text, int len, int *year, int *month, int *day, int *hour, int *minute) {
    if (len < 16) {
        return 0;
    }

    return json_parse4(text, year) &&
           json_parse2(text + 5, month) &&
           json_parse2(text + 8, day) &&
           json_parse2(text + 11, hour) &&
           json_parse2(text + 14, minute);
}

static int json_day_of_week(int year, int month, int day) {
    static const int offsets[12] = {0, 3, 2, 5, 0, 3, 5, 1, 4, 6, 2, 4};
    int y = year;
    if (month < 3) {
        y--;
    }

    int dow = (y + y / 4 - y / 100 + y / 400 + offsets[month - 1] + day) % 7;
    return (dow + 6) % 7;
}

static int64_t json_days_from_civil(int year, int month, int day) {
    int y = year - (month <= 2 ? 1 : 0);
    int era = (y >= 0 ? y : y - 399) / 400;
    int yoe = y - era * 400;
    int mp = month + (month > 2 ? -3 : 9);
    int doy = (153 * mp + 2) / 5 + day - 1;
    int doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return (int64_t)era * 146097 + doe - 719468;
}

static inline int64_t json_epoch_minutes(int year, int month, int day, int hour, int minute) {
    return json_days_from_civil(year, month, day) * 1440 + (int64_t)hour * 60 + minute;
}

static inline double json_clamp01(double value) {
    if (!(value >= 0.0)) {
        return 0.0;
    }

    return value > 1.0 ? 1.0 : value;
}

static inline int16_t json_quantize(double value) {
    double clamped = json_clamp01(value);
    return (int16_t)(clamped * (double)SCALE + 0.5);
}

static inline int16_t json_clamp_quantized_int(int value) {
    if (value < 0) {
        return 0;
    }

    return value > SCALE ? (int16_t)SCALE : (int16_t)value;
}

static int json_read_scaled_int(const uint8_t *body, int len, int *pos, int scale, int *value) {
    if (*pos >= len || scale <= 0) {
        return 0;
    }

    int sign = 1;
    if (body[*pos] == (uint8_t)'-') {
        sign = -1;
        (*pos)++;
    } else if (body[*pos] == (uint8_t)'+') {
        (*pos)++;
    }

    uint64_t integer = 0;
    int digits = 0;
    while (*pos < len) {
        uint8_t d = (uint8_t)(body[*pos] - (uint8_t)'0');
        if (d > 9) {
            break;
        }

        integer = integer * 10U + d;
        digits++;
        (*pos)++;
    }

    uint64_t fraction = 0;
    uint64_t denominator = 1;
    if (*pos < len && body[*pos] == (uint8_t)'.') {
        (*pos)++;
        while (*pos < len) {
            uint8_t d = (uint8_t)(body[*pos] - (uint8_t)'0');
            if (d > 9) {
                break;
            }

            if (denominator <= 100000000000ULL) {
                fraction = fraction * 10U + d;
                denominator *= 10U;
            }
            digits++;
            (*pos)++;
        }
    }

    if (digits == 0) {
        return 0;
    }

    if (*pos < len && (body[*pos] == (uint8_t)'e' || body[*pos] == (uint8_t)'E')) {
        return 0;
    }

    uint64_t scaled = integer * (uint64_t)scale + ((fraction * (uint64_t)scale) + (denominator / 2U)) / denominator;
    if (scaled > (uint64_t)INT_MAX) {
        scaled = (uint64_t)INT_MAX;
    }

    *value = sign < 0 ? -(int)scaled : (int)scaled;
    return 1;
}

static int json_mcc_risk_quantized(const uint8_t *mcc, int mcc_len) {
    if (mcc_len != 4) {
        return 5000;
    }

    if (memcmp(mcc, "5411", 4) == 0) return 1500;
    if (memcmp(mcc, "5812", 4) == 0) return 3000;
    if (memcmp(mcc, "5912", 4) == 0) return 2000;
    if (memcmp(mcc, "5944", 4) == 0) return 4500;
    if (memcmp(mcc, "7801", 4) == 0) return 8000;
    if (memcmp(mcc, "7802", 4) == 0) return 7500;
    if (memcmp(mcc, "7995", 4) == 0) return 8500;
    if (memcmp(mcc, "4511", 4) == 0) return 3500;
    if (memcmp(mcc, "5311", 4) == 0) return 2500;
    if (memcmp(mcc, "5999", 4) == 0) return 5000;
    return 5000;
}

static double json_mcc_risk(const uint8_t *mcc, int mcc_len) {
    if (mcc_len != 4) {
        return 0.50;
    }

    if (memcmp(mcc, "5411", 4) == 0) return 0.15;
    if (memcmp(mcc, "5812", 4) == 0) return 0.30;
    if (memcmp(mcc, "5912", 4) == 0) return 0.20;
    if (memcmp(mcc, "5944", 4) == 0) return 0.45;
    if (memcmp(mcc, "7801", 4) == 0) return 0.80;
    if (memcmp(mcc, "7802", 4) == 0) return 0.75;
    if (memcmp(mcc, "7995", 4) == 0) return 0.85;
    if (memcmp(mcc, "4511", 4) == 0) return 0.35;
    if (memcmp(mcc, "5311", 4) == 0) return 0.25;
    if (memcmp(mcc, "5999", 4) == 0) return 0.50;
    return 0.50;
}

static int json_build_query_ordered(const uint8_t *body, int len, int16_t *query) {
#if JSON_FIXED_NUMBERS
    int pos = 0;
    int amount_cents = 0;
    int installments = 0;
    int customer_avg_cents = 0;
    int tx24h = 0;
    int merchant_avg_cents = 0;
    int km_home_tenths = 0;
    int last_km_tenths = 0;
    int requested_start = 0;
    int requested_len = 0;
    int known_start = 0;
    int known_len = 0;
    int merchant_start = 0;
    int merchant_len = 0;
    int mcc_start = 0;
    int mcc_len = 0;
    int last_start = 0;
    int last_len = 0;
    int is_online = 0;
    int card_present = 0;

    if (!JSON_CONSUME(body, len, &pos, "{\"id\":\"") ||
        !json_skip_simple_string(body, len, &pos) ||
        !JSON_CONSUME(body, len, &pos, ",\"transaction\":{\"amount\":") ||
        !json_read_scaled_int(body, len, &pos, 100, &amount_cents) ||
        !JSON_CONSUME(body, len, &pos, ",\"installments\":") ||
        !json_read_scaled_int(body, len, &pos, 1, &installments) ||
        !JSON_CONSUME(body, len, &pos, ",\"requested_at\":\"") ||
        !json_read_simple_string(body, len, &pos, &requested_start, &requested_len) ||
        !JSON_CONSUME(body, len, &pos, "},\"customer\":{\"avg_amount\":") ||
        !json_read_scaled_int(body, len, &pos, 100, &customer_avg_cents) ||
        !JSON_CONSUME(body, len, &pos, ",\"tx_count_24h\":") ||
        !json_read_scaled_int(body, len, &pos, 1, &tx24h) ||
        !JSON_CONSUME(body, len, &pos, ",\"known_merchants\":") ||
        !json_read_array_slice(body, len, &pos, &known_start, &known_len) ||
        !JSON_CONSUME(body, len, &pos, "},\"merchant\":{\"id\":\"") ||
        !json_read_simple_string(body, len, &pos, &merchant_start, &merchant_len) ||
        !JSON_CONSUME(body, len, &pos, ",\"mcc\":\"") ||
        !json_read_simple_string(body, len, &pos, &mcc_start, &mcc_len) ||
        !JSON_CONSUME(body, len, &pos, ",\"avg_amount\":") ||
        !json_read_scaled_int(body, len, &pos, 100, &merchant_avg_cents) ||
        !JSON_CONSUME(body, len, &pos, "},\"terminal\":{\"is_online\":") ||
        !json_read_bool(body, len, &pos, &is_online) ||
        !JSON_CONSUME(body, len, &pos, ",\"card_present\":") ||
        !json_read_bool(body, len, &pos, &card_present) ||
        !JSON_CONSUME(body, len, &pos, ",\"km_from_home\":") ||
        !json_read_scaled_int(body, len, &pos, 10, &km_home_tenths) ||
        !JSON_CONSUME(body, len, &pos, "},\"last_transaction\":")) {
        return 0;
    }

    int has_last = 0;
    if (json_consume(body, len, &pos, "null", 4)) {
        has_last = 0;
    } else {
        has_last = 1;
        if (!JSON_CONSUME(body, len, &pos, "{\"timestamp\":\"") ||
            !json_read_simple_string(body, len, &pos, &last_start, &last_len) ||
            !JSON_CONSUME(body, len, &pos, ",\"km_from_current\":") ||
            !json_read_scaled_int(body, len, &pos, 10, &last_km_tenths) ||
            !JSON_CONSUME(body, len, &pos, "}")) {
            return 0;
        }
    }

    if (!JSON_CONSUME(body, len, &pos, "}") || pos != len) {
        return 0;
    }

    int year = 2026;
    int month = 1;
    int day = 1;
    int hour = 0;
    int minute = 0;
    (void)json_parse_time(body + requested_start, requested_len, &year, &month, &day, &hour, &minute);

    int dow = json_day_of_week(year, month, day);
    query[0] = json_clamp_quantized_int((amount_cents + 50) / 100);
    query[1] = json_clamp_quantized_int((installments * SCALE + 6) / 12);
    query[2] = customer_avg_cents <= 0
        ? (int16_t)SCALE
        : json_clamp_quantized_int((int)(((int64_t)amount_cents * 1000 + customer_avg_cents / 2) / customer_avg_cents));
    query[3] = json_clamp_quantized_int((hour * SCALE + 11) / 23);
    query[4] = json_clamp_quantized_int((dow * SCALE + 3) / 6);

    if (has_last) {
        int ly = year;
        int lm = month;
        int ld = day;
        int lh = hour;
        int lmin = minute;
        (void)json_parse_time(body + last_start, last_len, &ly, &lm, &ld, &lh, &lmin);
        int64_t current = json_epoch_minutes(year, month, day, hour, minute);
        int64_t last = json_epoch_minutes(ly, lm, ld, lh, lmin);
        int64_t minutes = current > last ? current - last : 0;
        int64_t scaled_minutes = (minutes * SCALE + 720) / 1440;
        query[5] = json_clamp_quantized_int(scaled_minutes > INT_MAX ? INT_MAX : (int)scaled_minutes);
        query[6] = json_clamp_quantized_int(last_km_tenths);
    } else {
        query[5] = (int16_t)-SCALE;
        query[6] = (int16_t)-SCALE;
    }

    int known = json_contains_quoted(body + known_start, known_len, body + merchant_start, merchant_len);
    query[7] = json_clamp_quantized_int(km_home_tenths);
    query[8] = json_clamp_quantized_int(tx24h * 500);
    query[9] = is_online ? (int16_t)SCALE : 0;
    query[10] = card_present ? (int16_t)SCALE : 0;
    query[11] = known ? 0 : (int16_t)SCALE;
    query[12] = (int16_t)json_mcc_risk_quantized(body + mcc_start, mcc_len);
    query[13] = json_clamp_quantized_int((merchant_avg_cents + 50) / 100);
    query[14] = 0;
    query[15] = 0;
    return 1;
#else
    int pos = 0;
    double amount = 0.0;
    double installments = 0.0;
    double customer_avg = 0.0;
    double tx24h = 0.0;
    double merchant_avg = 0.0;
    double km_home = 0.0;
    double last_km = 0.0;
    int requested_start = 0;
    int requested_len = 0;
    int known_start = 0;
    int known_len = 0;
    int merchant_start = 0;
    int merchant_len = 0;
    int mcc_start = 0;
    int mcc_len = 0;
    int last_start = 0;
    int last_len = 0;
    int is_online = 0;
    int card_present = 0;

    if (!JSON_CONSUME(body, len, &pos, "{\"id\":\"") ||
        !json_skip_simple_string(body, len, &pos) ||
        !JSON_CONSUME(body, len, &pos, ",\"transaction\":{\"amount\":") ||
        !json_read_number(body, len, &pos, &amount) ||
        !JSON_CONSUME(body, len, &pos, ",\"installments\":") ||
        !json_read_number(body, len, &pos, &installments) ||
        !JSON_CONSUME(body, len, &pos, ",\"requested_at\":\"") ||
        !json_read_simple_string(body, len, &pos, &requested_start, &requested_len) ||
        !JSON_CONSUME(body, len, &pos, "},\"customer\":{\"avg_amount\":") ||
        !json_read_number(body, len, &pos, &customer_avg) ||
        !JSON_CONSUME(body, len, &pos, ",\"tx_count_24h\":") ||
        !json_read_number(body, len, &pos, &tx24h) ||
        !JSON_CONSUME(body, len, &pos, ",\"known_merchants\":") ||
        !json_read_array_slice(body, len, &pos, &known_start, &known_len) ||
        !JSON_CONSUME(body, len, &pos, "},\"merchant\":{\"id\":\"") ||
        !json_read_simple_string(body, len, &pos, &merchant_start, &merchant_len) ||
        !JSON_CONSUME(body, len, &pos, ",\"mcc\":\"") ||
        !json_read_simple_string(body, len, &pos, &mcc_start, &mcc_len) ||
        !JSON_CONSUME(body, len, &pos, ",\"avg_amount\":") ||
        !json_read_number(body, len, &pos, &merchant_avg) ||
        !JSON_CONSUME(body, len, &pos, "},\"terminal\":{\"is_online\":") ||
        !json_read_bool(body, len, &pos, &is_online) ||
        !JSON_CONSUME(body, len, &pos, ",\"card_present\":") ||
        !json_read_bool(body, len, &pos, &card_present) ||
        !JSON_CONSUME(body, len, &pos, ",\"km_from_home\":") ||
        !json_read_number(body, len, &pos, &km_home) ||
        !JSON_CONSUME(body, len, &pos, "},\"last_transaction\":")) {
        return 0;
    }

    int has_last = 0;
    if (json_consume(body, len, &pos, "null", 4)) {
        has_last = 0;
    } else {
        has_last = 1;
        if (!JSON_CONSUME(body, len, &pos, "{\"timestamp\":\"") ||
            !json_read_simple_string(body, len, &pos, &last_start, &last_len) ||
            !JSON_CONSUME(body, len, &pos, ",\"km_from_current\":") ||
            !json_read_number(body, len, &pos, &last_km) ||
            !JSON_CONSUME(body, len, &pos, "}")) {
            return 0;
        }
    }

    if (!JSON_CONSUME(body, len, &pos, "}") || pos != len) {
        return 0;
    }

    int year = 2026;
    int month = 1;
    int day = 1;
    int hour = 0;
    int minute = 0;
    (void)json_parse_time(body + requested_start, requested_len, &year, &month, &day, &hour, &minute);

    int dow = json_day_of_week(year, month, day);
    query[0] = json_quantize(amount / 10000.0);
    query[1] = json_quantize(installments / 12.0);
    query[2] = json_quantize(customer_avg <= 0.0 ? 1.0 : (amount / customer_avg) / 10.0);
    query[3] = json_quantize((double)hour / 23.0);
    query[4] = json_quantize((double)dow / 6.0);

    if (has_last) {
        int ly = year;
        int lm = month;
        int ld = day;
        int lh = hour;
        int lmin = minute;
        (void)json_parse_time(body + last_start, last_len, &ly, &lm, &ld, &lh, &lmin);
        int64_t current = json_epoch_minutes(year, month, day, hour, minute);
        int64_t last = json_epoch_minutes(ly, lm, ld, lh, lmin);
        int64_t minutes = current > last ? current - last : 0;
        query[5] = json_quantize((double)minutes / 1440.0);
        query[6] = json_quantize(last_km / 1000.0);
    } else {
        query[5] = (int16_t)-SCALE;
        query[6] = (int16_t)-SCALE;
    }

    int known = json_contains_quoted(body + known_start, known_len, body + merchant_start, merchant_len);
    query[7] = json_quantize(km_home / 1000.0);
    query[8] = json_quantize(tx24h / 20.0);
    query[9] = is_online ? (int16_t)SCALE : 0;
    query[10] = card_present ? (int16_t)SCALE : 0;
    query[11] = known ? 0 : (int16_t)SCALE;
    query[12] = json_quantize(json_mcc_risk(body + mcc_start, mcc_len));
    query[13] = json_quantize(merchant_avg / 10000.0);
    query[14] = 0;
    query[15] = 0;
    return 1;
#endif
}

__attribute__((visibility("default")))
int32_t rinha_classify_kdtree_avx2(
    const uint8_t *partitions,
    const uint8_t *nodes,
    const int16_t *vectors,
    const uint8_t *labels,
    const int32_t *ids,
    const int16_t *query,
    int32_t node_count,
    int32_t max_partitions) {
    int64_t top_dist[K] = {INT64_MAX, INT64_MAX, INT64_MAX, INT64_MAX, INT64_MAX};
    uint8_t top_label[K] = {0, 0, 0, 0, 0};
    int32_t top_id[K] = {INT_MAX, INT_MAX, INT_MAX, INT_MAX, INT_MAX};

    if (max_partitions <= 0 || max_partitions > KD_PARTITION_COUNT) {
        max_partitions = KD_PARTITION_COUNT;
    }

    int primary = kd_partition_key(query);
    const uint8_t *primary_partition = kd_partition_ptr(partitions, primary);
    int primary_root = kd_rd32s(primary_partition);
    if (primary_root >= 0) {
#if KD_BEST_FIRST
        kd_search_node_best_first(nodes, vectors, labels, ids, node_count, query, primary_root, top_dist, top_label, top_id);
#else
        kd_search_node(nodes, vectors, labels, ids, node_count, query, primary_root, top_dist, top_label, top_id);
#endif
    }

    int searched_partitions = 1;
    if (searched_partitions >= max_partitions) {
        return kd_count_frauds(top_label);
    }

    kd_partition_candidate_t candidates[KD_PARTITION_COUNT];
    int candidate_count = 0;
    for (int partition = 0; partition < KD_PARTITION_COUNT; partition++) {
        if (partition == primary) {
            continue;
        }

        const uint8_t *record = kd_partition_ptr(partitions, partition);
        int root = kd_rd32s(record);
        int count = kd_rd32s(record + 4);
        if (root < 0 || count <= 0) {
            continue;
        }

        int64_t bound = kd_bounds_lower_bound(
            query,
            (const int16_t *)(record + 8),
            (const int16_t *)(record + 40));
        if (bound <= top_dist[K - 1]) {
            kd_insert_partition_candidate(candidates, &candidate_count, partition, bound);
        }
    }

    for (int i = 0; i < candidate_count; i++) {
        if (candidates[i].bound > top_dist[K - 1]) {
            break;
        }

        const uint8_t *record = kd_partition_ptr(partitions, candidates[i].index);
#if KD_BEST_FIRST
        kd_search_node_best_first(nodes, vectors, labels, ids, node_count, query, kd_rd32s(record), top_dist, top_label, top_id);
#else
        kd_search_node(nodes, vectors, labels, ids, node_count, query, kd_rd32s(record), top_dist, top_label, top_id);
#endif
        searched_partitions++;
        if (searched_partitions >= max_partitions) {
            break;
        }
    }

    return kd_count_frauds(top_label);
}

__attribute__((visibility("default")))
int32_t rinha_classify_kdtree_block_avx2(
    const uint8_t *partitions,
    const uint8_t *nodes,
    const int16_t *block_vectors,
    const uint8_t *labels,
    const int32_t *ids,
    const int16_t *query,
    int32_t node_count,
    int32_t max_partitions) {
    int64_t top_dist[K] = {INT64_MAX, INT64_MAX, INT64_MAX, INT64_MAX, INT64_MAX};
    uint8_t top_label[K] = {0, 0, 0, 0, 0};
    int32_t top_id[K] = {INT_MAX, INT_MAX, INT_MAX, INT_MAX, INT_MAX};

    if (max_partitions <= 0 || max_partitions > KD_PARTITION_COUNT) {
        max_partitions = KD_PARTITION_COUNT;
    }

    int primary = kd_partition_key(query);
    const uint8_t *primary_partition = kd_partition_ptr(partitions, primary);
    int primary_root = kd_rd32s(primary_partition);
    if (primary_root >= 0) {
#if KD_BEST_FIRST
        kd_search_node_best_first_block(nodes, block_vectors, labels, ids, node_count, query, primary_root, top_dist, top_label, top_id);
#else
        kd_search_node_block(nodes, block_vectors, labels, ids, node_count, query, primary_root, top_dist, top_label, top_id);
#endif
    }

    int searched_partitions = 1;
    if (searched_partitions >= max_partitions) {
        return kd_count_frauds(top_label);
    }

    kd_partition_candidate_t candidates[KD_PARTITION_COUNT];
    int candidate_count = 0;
    for (int partition = 0; partition < KD_PARTITION_COUNT; partition++) {
        if (partition == primary) {
            continue;
        }

        const uint8_t *record = kd_partition_ptr(partitions, partition);
        int root = kd_rd32s(record);
        int count = kd_rd32s(record + 4);
        if (root < 0 || count <= 0) {
            continue;
        }

        int64_t bound = kd_bounds_lower_bound(
            query,
            (const int16_t *)(record + 8),
            (const int16_t *)(record + 40));
        if (bound <= top_dist[K - 1]) {
            kd_insert_partition_candidate(candidates, &candidate_count, partition, bound);
        }
    }

    for (int i = 0; i < candidate_count; i++) {
        if (candidates[i].bound > top_dist[K - 1]) {
            break;
        }

        const uint8_t *record = kd_partition_ptr(partitions, candidates[i].index);
#if KD_BEST_FIRST
        kd_search_node_best_first_block(nodes, block_vectors, labels, ids, node_count, query, kd_rd32s(record), top_dist, top_label, top_id);
#else
        kd_search_node_block(nodes, block_vectors, labels, ids, node_count, query, kd_rd32s(record), top_dist, top_label, top_id);
#endif
        searched_partitions++;
        if (searched_partitions >= max_partitions) {
            break;
        }
    }

    return kd_count_frauds(top_label);
}

__attribute__((visibility("default")))
int32_t rinha_classify_json_kdtree_avx2(
    const uint8_t *partitions,
    const uint8_t *nodes,
    const int16_t *vectors,
    const uint8_t *labels,
    const int32_t *ids,
    const uint8_t *body,
    int32_t body_length,
    int32_t node_count,
    int32_t max_partitions) {
    int16_t query[KD_VECTOR_STRIDE];
    if (body == 0 || body_length <= 0 || !json_build_query_ordered(body, body_length, query)) {
        return -1;
    }

    return rinha_classify_kdtree_avx2(
        partitions,
        nodes,
        vectors,
        labels,
        ids,
        query,
        node_count,
        max_partitions);
}

__attribute__((visibility("default")))
int32_t rinha_classify_json_kdtree_block_avx2(
    const uint8_t *partitions,
    const uint8_t *nodes,
    const int16_t *block_vectors,
    const uint8_t *labels,
    const int32_t *ids,
    const uint8_t *body,
    int32_t body_length,
    int32_t node_count,
    int32_t max_partitions) {
    int16_t query[KD_VECTOR_STRIDE];
    if (body == 0 || body_length <= 0 || !json_build_query_ordered(body, body_length, query)) {
        return -1;
    }

    return rinha_classify_kdtree_block_avx2(
        partitions,
        nodes,
        block_vectors,
        labels,
        ids,
        query,
        node_count,
        max_partitions);
}

__attribute__((visibility("default")))
int32_t rinha_classify_json_profile_kdtree_avx2(
    const uint8_t *partitions,
    const uint8_t *nodes,
    const int16_t *vectors,
    const uint8_t *labels,
    const int32_t *ids,
    const uint32_t *bucket_counts,
    const uint32_t *bucket_fraud_counts,
    const uint16_t *profile_counts,
    const uint16_t *profile_fraud_counts,
    const uint8_t *profile_masks,
    const uint8_t *body,
    int32_t body_length,
    int32_t node_count,
    int32_t max_partitions,
    int32_t profile_fastpath,
    int32_t profile_legit_min_count,
    int32_t profile_fraud_min_count,
    int32_t profile_fraud_amount_min,
    int32_t profile_fraud_low_amount_fastpath,
    int32_t profile_fraud_low_amount_km_home_min,
    int32_t profile_fraud_low_amount_tx24h_min,
    int32_t profile_fraud_mid_amount_no_last_fastpath,
    int32_t profile_fraud_mid_amount_min,
    int32_t profile_fraud_no_last_only,
    int32_t profile_dominant_fastpath,
    int32_t profile_dominant_min_count,
    int32_t profile_dominant_max_opposite,
    int32_t profile_dominant_legit_enabled,
    int32_t profile_dominant_fraud_enabled,
    int32_t bucket_fastpath,
    int32_t bucket_legit_min_count,
    int32_t bucket_fraud_min_count,
    int32_t bucket_fraud_no_last_only) {
    int16_t query[KD_VECTOR_STRIDE];
    if (body == 0 || body_length <= 0 || !json_build_query_ordered(body, body_length, query)) {
        return -1;
    }

    if (profile_fastpath && profile_counts != 0 && profile_masks != 0) {
        int key = profile_key(query);
        uint8_t mask = profile_masks[key];
        uint16_t count = profile_counts[key];
        if (mask == PROFILE_LEGIT_MASK && count >= profile_legit_min_count) {
            return 0;
        }

        int profile_fraud_amount_allowed =
            query[0] >= profile_fraud_amount_min ||
            (profile_fraud_mid_amount_no_last_fastpath &&
             query[0] >= profile_fraud_mid_amount_min &&
             query[5] < 0) ||
            (profile_fraud_low_amount_fastpath &&
             ((query[7] >= profile_fraud_low_amount_km_home_min &&
               query[8] >= profile_fraud_low_amount_tx24h_min) ||
              query[7] >= 5500 ||
              query[8] >= 6250));
        if (mask == PROFILE_FRAUD_MASK &&
            count >= profile_fraud_min_count &&
            profile_fraud_amount_allowed &&
            (!profile_fraud_no_last_only || query[5] < 0)) {
            return K;
        }

        if (profile_dominant_fastpath && profile_fraud_counts != 0) {
            uint16_t frauds = profile_fraud_counts[key];
            int legits = (int)count - (int)frauds;
            if (legits < 0) {
                legits = 0;
            }

            if (profile_dominant_fraud_enabled &&
                frauds >= profile_dominant_min_count &&
                legits <= profile_dominant_max_opposite &&
                profile_fraud_amount_allowed &&
                (!profile_fraud_no_last_only || query[5] < 0)) {
                return K;
            }

            if (profile_dominant_legit_enabled &&
                legits >= profile_dominant_min_count &&
                frauds <= profile_dominant_max_opposite) {
                return 0;
            }
        }
    }

    if (bucket_fastpath) {
        int decision = bucket_fast_decision(
            bucket_counts,
            bucket_fraud_counts,
            query,
            bucket_legit_min_count,
            bucket_fraud_min_count,
            bucket_fraud_no_last_only);
        if (decision >= 0) {
            return decision;
        }
    }

    return rinha_classify_kdtree_avx2(
        partitions,
        nodes,
        vectors,
        labels,
        ids,
        query,
        node_count,
        max_partitions);
}

__attribute__((visibility("default")))
int32_t rinha_classify_json_profile_kdtree_block_avx2(
    const uint8_t *partitions,
    const uint8_t *nodes,
    const int16_t *block_vectors,
    const uint8_t *labels,
    const int32_t *ids,
    const uint32_t *bucket_counts,
    const uint32_t *bucket_fraud_counts,
    const uint16_t *profile_counts,
    const uint16_t *profile_fraud_counts,
    const uint8_t *profile_masks,
    const uint8_t *body,
    int32_t body_length,
    int32_t node_count,
    int32_t max_partitions,
    int32_t profile_fastpath,
    int32_t profile_legit_min_count,
    int32_t profile_fraud_min_count,
    int32_t profile_fraud_amount_min,
    int32_t profile_fraud_low_amount_fastpath,
    int32_t profile_fraud_low_amount_km_home_min,
    int32_t profile_fraud_low_amount_tx24h_min,
    int32_t profile_fraud_mid_amount_no_last_fastpath,
    int32_t profile_fraud_mid_amount_min,
    int32_t profile_fraud_no_last_only,
    int32_t profile_dominant_fastpath,
    int32_t profile_dominant_min_count,
    int32_t profile_dominant_max_opposite,
    int32_t profile_dominant_legit_enabled,
    int32_t profile_dominant_fraud_enabled,
    int32_t bucket_fastpath,
    int32_t bucket_legit_min_count,
    int32_t bucket_fraud_min_count,
    int32_t bucket_fraud_no_last_only) {
    int16_t query[KD_VECTOR_STRIDE];
    if (body == 0 || body_length <= 0 || !json_build_query_ordered(body, body_length, query)) {
        return -1;
    }

    if (profile_fastpath && profile_counts != 0 && profile_masks != 0) {
        int key = profile_key(query);
        uint8_t mask = profile_masks[key];
        uint16_t count = profile_counts[key];
        if (mask == PROFILE_LEGIT_MASK && count >= profile_legit_min_count) {
            return 0;
        }

        int profile_fraud_amount_allowed =
            query[0] >= profile_fraud_amount_min ||
            (profile_fraud_mid_amount_no_last_fastpath &&
             query[0] >= profile_fraud_mid_amount_min &&
             query[5] < 0) ||
            (profile_fraud_low_amount_fastpath &&
             ((query[7] >= profile_fraud_low_amount_km_home_min &&
               query[8] >= profile_fraud_low_amount_tx24h_min) ||
              query[7] >= 5500 ||
              query[8] >= 6250));
        if (mask == PROFILE_FRAUD_MASK &&
            count >= profile_fraud_min_count &&
            profile_fraud_amount_allowed &&
            (!profile_fraud_no_last_only || query[5] < 0)) {
            return K;
        }

        if (profile_dominant_fastpath && profile_fraud_counts != 0) {
            uint16_t frauds = profile_fraud_counts[key];
            int legits = (int)count - (int)frauds;
            if (legits < 0) {
                legits = 0;
            }

            if (profile_dominant_fraud_enabled &&
                frauds >= profile_dominant_min_count &&
                legits <= profile_dominant_max_opposite &&
                profile_fraud_amount_allowed &&
                (!profile_fraud_no_last_only || query[5] < 0)) {
                return K;
            }

            if (profile_dominant_legit_enabled &&
                legits >= profile_dominant_min_count &&
                frauds <= profile_dominant_max_opposite) {
                return 0;
            }
        }
    }

    if (bucket_fastpath) {
        int decision = bucket_fast_decision(
            bucket_counts,
            bucket_fraud_counts,
            query,
            bucket_legit_min_count,
            bucket_fraud_min_count,
            bucket_fraud_no_last_only);
        if (decision >= 0) {
            return decision;
        }
    }

    return rinha_classify_kdtree_block_avx2(
        partitions,
        nodes,
        block_vectors,
        labels,
        ids,
        query,
        node_count,
        max_partitions);
}

__attribute__((visibility("default")))
int32_t rinha_classify_kdtree_stats_avx2(
    const uint8_t *partitions,
    const uint8_t *nodes,
    const int16_t *vectors,
    const uint8_t *labels,
    const int32_t *ids,
    const int16_t *query,
    int32_t node_count,
    int32_t max_partitions,
    int32_t *stats_out,
    int32_t stats_length) {
    int64_t top_dist[K] = {INT64_MAX, INT64_MAX, INT64_MAX, INT64_MAX, INT64_MAX};
    uint8_t top_label[K] = {0, 0, 0, 0, 0};
    int32_t top_id[K] = {INT_MAX, INT_MAX, INT_MAX, INT_MAX, INT_MAX};
    kd_stats_t stats = {0, 0, 0, 0, 0, 0, 0, -1};

    if (max_partitions <= 0 || max_partitions > KD_PARTITION_COUNT) {
        max_partitions = KD_PARTITION_COUNT;
    }

    int primary = kd_partition_key(query);
    stats.primary_partition = primary;
    const uint8_t *primary_partition = kd_partition_ptr(partitions, primary);
    int primary_root = kd_rd32s(primary_partition);
    if (primary_root >= 0) {
        kd_search_node_stats(nodes, vectors, labels, ids, node_count, query, primary_root, top_dist, top_label, top_id, &stats);
    }

    int searched_partitions = 1;
    if (searched_partitions >= max_partitions) {
        stats.searched_partitions = searched_partitions;
        int32_t frauds = kd_count_frauds(top_label);
        if (stats_out != 0 && stats_length > 0) {
            int32_t values[KD_STATS_LENGTH] = {
                frauds,
                stats.searched_partitions,
                stats.candidate_partitions,
                stats.visited_nodes,
                stats.pruned_nodes,
                stats.scanned_leaves,
                stats.scanned_vectors,
                stats.max_stack_depth,
                stats.primary_partition
            };
            int32_t count = stats_length < KD_STATS_LENGTH ? stats_length : KD_STATS_LENGTH;
            for (int32_t i = 0; i < count; i++) {
                stats_out[i] = values[i];
            }
        }

        return frauds;
    }

    kd_partition_candidate_t candidates[KD_PARTITION_COUNT];
    int candidate_count = 0;
    for (int partition = 0; partition < KD_PARTITION_COUNT; partition++) {
        if (partition == primary) {
            continue;
        }

        const uint8_t *record = kd_partition_ptr(partitions, partition);
        int root = kd_rd32s(record);
        int count = kd_rd32s(record + 4);
        if (root < 0 || count <= 0) {
            continue;
        }

        int64_t bound = kd_bounds_lower_bound(
            query,
            (const int16_t *)(record + 8),
            (const int16_t *)(record + 40));
        if (bound <= top_dist[K - 1]) {
            kd_insert_partition_candidate(candidates, &candidate_count, partition, bound);
        }
    }

    stats.candidate_partitions = candidate_count;
    for (int i = 0; i < candidate_count; i++) {
        if (candidates[i].bound > top_dist[K - 1]) {
            break;
        }

        const uint8_t *record = kd_partition_ptr(partitions, candidates[i].index);
        kd_search_node_stats(nodes, vectors, labels, ids, node_count, query, kd_rd32s(record), top_dist, top_label, top_id, &stats);
        searched_partitions++;
        if (searched_partitions >= max_partitions) {
            break;
        }
    }

    stats.searched_partitions = searched_partitions;
    int32_t frauds = kd_count_frauds(top_label);
    if (stats_out != 0 && stats_length > 0) {
        int32_t values[KD_STATS_LENGTH] = {
            frauds,
            stats.searched_partitions,
            stats.candidate_partitions,
            stats.visited_nodes,
            stats.pruned_nodes,
            stats.scanned_leaves,
            stats.scanned_vectors,
            stats.max_stack_depth,
            stats.primary_partition
        };
        int32_t count = stats_length < KD_STATS_LENGTH ? stats_length : KD_STATS_LENGTH;
        for (int32_t i = 0; i < count; i++) {
            stats_out[i] = values[i];
        }
    }

    return frauds;
}
