#include <immintrin.h>
#include <stdint.h>

#define DIM 14
#define RISKY_STRIDE 16
#define BUCKET_COUNT 4096
#define FINE_EXTRA_BITS 3
#define FINE_PER_COARSE (1 << FINE_EXTRA_BITS)
#define SCALE 10000
#define K 5

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
