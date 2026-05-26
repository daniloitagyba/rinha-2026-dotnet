#!/bin/sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
LEAF_SIZES="${LEAF_SIZES:-96 112 128 144 160 192}"
KEY_PROFILES="${KEY_PROFILES:-0}"
KDTREE_MAX_PARTITIONS="${KDTREE_MAX_PARTITIONS:-256}"
OUTPUT="${OUTPUT:-$ROOT/test/kd-matrix.tsv}"
EVAL_LIMIT="${EVAL_LIMIT:-}"

if [ ! -s "$ROOT/test/test-data.json" ] || [ ! -s "$ROOT/resources/references.json.gz" ]; then
  sh "$ROOT/scripts/sync-official-data.sh"
fi

mkdir -p "$ROOT/test"

extract_field() {
  line="$1"
  field="$2"
  printf '%s\n' "$line" | tr ' ' '\n' | grep "^$field=" | tail -1 | cut -d= -f2- || true
}

printf 'key_profile\tleaf_size\tstatus\tclassify_p99_ns\tpath_p99_ns\tavg_kd_scanned_vectors\tp99_kd_scanned_vectors\tavg_kd_visited_nodes\tp99_kd_visited_nodes\tavg_kd_searched_partitions\tp99_kd_searched_partitions\tscore_det\n' > "$OUTPUT"

for key_profile in $KEY_PROFILES; do
for leaf_size in $LEAF_SIZES; do
  image="rinha-dotnet-kd-p$key_profile-leaf-$leaf_size:latest"
  log_path="$ROOT/test/kd-matrix-p$key_profile-leaf-$leaf_size.log"
  report_name="kd-matrix-p$key_profile-leaf-$leaf_size-report.json"

  echo "building KDTREE_KEY_PROFILE=$key_profile KDTREE_LEAF_SIZE=$leaf_size"
  docker build \
    --build-arg TARGETARCH=amd64 \
    --build-arg KDTREE_LEAF_SIZE="$leaf_size" \
    --build-arg KDTREE_KEY_PROFILE="$key_profile" \
    -t "$image" \
    "$ROOT"

  echo "evaluating KDTREE_KEY_PROFILE=$key_profile KDTREE_LEAF_SIZE=$leaf_size"
  if docker run --rm \
    --entrypoint rinha-fraud \
    -v "$ROOT/test:/test" \
    -e KDTREE_NATIVE=1 \
    -e KDTREE_MAX_PARTITIONS="$KDTREE_MAX_PARTITIONS" \
    -e PROFILE_FASTPATH=0 \
    -e PROFILE_DOMINANT_FASTPATH=0 \
    -e EVAL_DIAGNOSTICS=1 \
    -e EVAL_LIMIT="$EVAL_LIMIT" \
    -e EVAL_REPORT_PATH="/test/$report_name" \
    "$image" \
    eval /test/test-data.json > "$log_path"; then
    cat "$log_path"
  else
    cat "$log_path" || true
    printf '%s\t%s\t%s\t\t\t\t\t\t\t\t\t\n' "$key_profile" "$leaf_size" "run_failed" >> "$OUTPUT"
    continue
  fi

  status="ok"
  if ! grep -q 'fp=0 fn=0 parse_errors=0 weighted_errors=0' "$log_path"; then
    status="accuracy_failed"
  fi

  classify_line="$(grep 'classify_latency_ns' "$log_path" | tail -1 || true)"
  path_line="$(grep 'path=native_kdtree' "$log_path" | tail -1 || true)"
  score_line="$(grep 'fp=' "$log_path" | tail -1 || true)"

  classify_p99="$(extract_field "$classify_line" "p99")"
  path_p99="$(extract_field "$path_line" "p99_ns")"
  avg_vectors="$(extract_field "$path_line" "avg_kd_scanned_vectors")"
  p99_vectors="$(extract_field "$path_line" "p99_kd_scanned_vectors")"
  avg_nodes="$(extract_field "$path_line" "avg_kd_visited_nodes")"
  p99_nodes="$(extract_field "$path_line" "p99_kd_visited_nodes")"
  avg_partitions="$(extract_field "$path_line" "avg_kd_searched_partitions")"
  p99_partitions="$(extract_field "$path_line" "p99_kd_searched_partitions")"
  score_det="$(extract_field "$score_line" "score_det")"

  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
    "$key_profile" "$leaf_size" "$status" "$classify_p99" "$path_p99" "$avg_vectors" "$p99_vectors" \
    "$avg_nodes" "$p99_nodes" "$avg_partitions" "$p99_partitions" "$score_det" >> "$OUTPUT"
done
done

cat "$OUTPUT"
echo "kd matrix written: $OUTPUT"
