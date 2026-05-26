#!/bin/sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
IMAGE="${IMAGE:-rinha-dotnet-kd-diagnostics:latest}"
LEAF_SIZE="${KDTREE_LEAF_SIZE:-96}"
KEY_PROFILE="${KDTREE_KEY_PROFILE:-0}"
REBUILD="${REBUILD:-1}"
EVAL_LIMIT="${EVAL_LIMIT:-}"
REPORT_NAME="${REPORT_NAME:-kd-diagnostics-report.json}"
LOG_PATH="${LOG_PATH:-$ROOT/test/kd-diagnostics.log}"
DUMP_NAME="${DUMP_NAME:-}"

if [ ! -s "$ROOT/test/test-data.json" ] || [ ! -s "$ROOT/resources/references.json.gz" ]; then
  sh "$ROOT/scripts/sync-official-data.sh"
fi

mkdir -p "$ROOT/test"

if [ "$REBUILD" = "1" ]; then
  docker build \
    --build-arg TARGETARCH=amd64 \
    --build-arg KDTREE_LEAF_SIZE="$LEAF_SIZE" \
    --build-arg KDTREE_KEY_PROFILE="$KEY_PROFILE" \
    -t "$IMAGE" \
    "$ROOT"
fi

if [ -n "$DUMP_NAME" ]; then
  docker run --rm \
    --entrypoint rinha-fraud \
    -v "$ROOT/test:/test" \
    -e KDTREE_NATIVE=1 \
    -e KDTREE_MAX_PARTITIONS="${KDTREE_MAX_PARTITIONS:-256}" \
    -e PROFILE_FASTPATH=0 \
    -e PROFILE_DOMINANT_FASTPATH=0 \
    -e EVAL_DIAGNOSTICS=1 \
    -e EVAL_LIMIT="$EVAL_LIMIT" \
    -e EVAL_REPORT_PATH="/test/$REPORT_NAME" \
    -e EVAL_DUMP_PATH="/test/$DUMP_NAME" \
    "$IMAGE" \
    eval /test/test-data.json > "$LOG_PATH"
else
  docker run --rm \
    --entrypoint rinha-fraud \
    -v "$ROOT/test:/test" \
    -e KDTREE_NATIVE=1 \
    -e KDTREE_MAX_PARTITIONS="${KDTREE_MAX_PARTITIONS:-256}" \
    -e PROFILE_FASTPATH=0 \
    -e PROFILE_DOMINANT_FASTPATH=0 \
    -e EVAL_DIAGNOSTICS=1 \
    -e EVAL_LIMIT="$EVAL_LIMIT" \
    -e EVAL_REPORT_PATH="/test/$REPORT_NAME" \
    "$IMAGE" \
    eval /test/test-data.json > "$LOG_PATH"
fi

cat "$LOG_PATH"
grep -q 'fp=0 fn=0 parse_errors=0 weighted_errors=0' "$LOG_PATH" || {
  echo "kd diagnostics accuracy gate failed; see $LOG_PATH" >&2
  exit 1
}

echo "kd diagnostics ok: $LOG_PATH"
echo "kd diagnostics report: $ROOT/test/$REPORT_NAME"
