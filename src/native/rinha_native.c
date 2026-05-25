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
#define KD_PARTITION_RECORD_SIZE 72
#define KD_NODE_RECORD_SIZE 80
#define KD_STACK_SIZE 128

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

static inline int kd_partition_key(const int16_t *v) {
    int key = v[5] < 0 ? 1 : 0;
    key |= (v[9] > 0 ? 1 : 0) << 1;
    key |= (v[10] > 0 ? 1 : 0) << 2;
    key |= (v[11] > 0 ? 1 : 0) << 3;
    key |= kd_bucket8(v[0]) << 4;
    key |= (v[8] >= 3500 ? 1 : 0) << 7;
    return key;
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
        int64_t dist = distance_risky_avx2(vectors + (int64_t)pos * KD_VECTOR_STRIDE, query);
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
        kd_search_node(nodes, vectors, labels, ids, node_count, query, primary_root, top_dist, top_label, top_id);
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
        kd_search_node(nodes, vectors, labels, ids, node_count, query, kd_rd32s(record), top_dist, top_label, top_id);
        searched_partitions++;
        if (searched_partitions >= max_partitions) {
            break;
        }
    }

    return kd_count_frauds(top_label);
}
