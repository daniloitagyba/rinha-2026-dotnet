#!/bin/sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
IMAGE="${IMAGE:-rinha-dotnet-local-validate:latest}"
PROJECT_NAME="${PROJECT_NAME:-rinha-local-validate}"
REFRESH_DATA="${REFRESH_DATA:-0}"
FETCH_REFERENCES="${FETCH_REFERENCES:-1}"
RUN_K6="${RUN_K6:-1}"
RUN_PROFILE_CHECK="${RUN_PROFILE_CHECK:-1}"
MANIFEST_PATH="${MANIFEST_PATH:-$ROOT/test/dataset-manifest.json}"
EVAL_LOG="${EVAL_LOG:-$ROOT/test/eval-accuracy.log}"
NATIVE_EVAL_LOG="${NATIVE_EVAL_LOG:-$ROOT/test/eval-native-accuracy.log}"
PROFILE_LOG="${PROFILE_LOG:-$ROOT/test/eval-profile-fastpath.log}"

need_file() {
  if [ ! -s "$1" ]; then
    echo "missing required file: $1" >&2
    exit 1
  fi
}

json_number() {
  key="$1"
  grep -o "\"$key\":[0-9.]*" "$ROOT/test/test-data.json" | head -1 | cut -d: -f2
}

sha256_file() {
  sha256sum "$1" | awk '{print $1}'
}

check_compose_safety() {
  compose_file="$1"
  compose_name="$2"

  if [ ! -f "$compose_file" ]; then
    return 0
  fi

  grep -q 'KDTREE_NATIVE: "1"' "$compose_file" || {
    echo "unsafe $compose_name: KDTREE_NATIVE must be enabled" >&2
    exit 1
  }

  grep -q 'PROFILE_FASTPATH: "0"' "$compose_file" || {
    echo "unsafe $compose_name: PROFILE_FASTPATH must be explicitly disabled" >&2
    exit 1
  }

  grep -q 'PROFILE_DOMINANT_FASTPATH: "0"' "$compose_file" || {
    echo "unsafe $compose_name: PROFILE_DOMINANT_FASTPATH must be explicitly disabled" >&2
    exit 1
  }

  if grep -q 'PROFILE_FASTPATH: "1"' "$compose_file"; then
    echo "unsafe $compose_name: submitted profile fast path must stay disabled" >&2
    exit 1
  fi

  if grep -q 'PROFILE_DOMINANT_FASTPATH: "1"' "$compose_file"; then
    echo "unsafe $compose_name: submitted dominant profile fast path must stay disabled" >&2
    exit 1
  fi
}

if [ "$REFRESH_DATA" = "1" ] || [ ! -s "$ROOT/test/test-data.json" ] || [ ! -s "$ROOT/resources/references.json.gz" ]; then
  FORCE="$REFRESH_DATA" FETCH_REFERENCES="$FETCH_REFERENCES" sh "$ROOT/scripts/sync-official-data.sh"
fi

need_file "$ROOT/test/test-data.json"
need_file "$ROOT/resources/references.json.gz"

mkdir -p "$ROOT/test"

test_sha="$(sha256_file "$ROOT/test/test-data.json")"
refs_sha="$(sha256_file "$ROOT/resources/references.json.gz")"
total="$(json_number total)"
fraud_count="$(json_number fraud_count)"
legit_count="$(json_number legit_count)"
edge_case_count="$(json_number edge_case_count)"
references_checksum="$(grep -o '"references_checksum_sha256":"[^"]*"' "$ROOT/test/test-data.json" | head -1 | cut -d: -f2 | tr -d '"')"

cat > "$MANIFEST_PATH" <<EOF
{
  "source": "zanfranceschi/rinha-de-backend-2026",
  "ref": "${RINHA_REF:-main}",
  "test_data_sha256": "$test_sha",
  "references_gzip_sha256": "$refs_sha",
  "references_checksum_sha256": "$references_checksum",
  "total": $total,
  "fraud_count": $fraud_count,
  "legit_count": $legit_count,
  "edge_case_count": $edge_case_count
}
EOF

grep -q 'ENV PROFILE_FASTPATH=0' "$ROOT/Dockerfile" || {
  echo "unsafe Dockerfile: PROFILE_FASTPATH must default to 0" >&2
  exit 1
}

check_compose_safety "$ROOT/docker-compose.yml" "docker-compose.yml"
check_compose_safety "$ROOT/submission/docker-compose.yml" "submission/docker-compose.yml"
check_compose_safety "$ROOT/../rinha-2026-submission/docker-compose.yml" "../rinha-2026-submission/docker-compose.yml"
check_compose_safety "/mnt/c/tmp/rinha-2026-submission/docker-compose.yml" "/mnt/c/tmp/rinha-2026-submission/docker-compose.yml"

docker build --build-arg TARGETARCH=amd64 -t "$IMAGE" "$ROOT"

docker run --rm \
  --entrypoint rinha-fraud \
  -v "$ROOT/test:/test" \
  -e KDTREE_NATIVE=1 \
  -e PROFILE_FASTPATH=0 \
  -e PROFILE_DOMINANT_FASTPATH=0 \
  "$IMAGE" \
  eval /test/test-data.json > "$EVAL_LOG"

cat "$EVAL_LOG"
grep -q 'fp=0 fn=0 parse_errors=0 weighted_errors=0' "$EVAL_LOG" || {
  echo "accuracy gate failed; see $EVAL_LOG" >&2
  exit 1
}

docker run --rm \
  --entrypoint rinha-native-api \
  -v "$ROOT/test:/test" \
  -e KDTREE_INDEX=1 \
  -e PROFILE_FASTPATH=0 \
  -e PROFILE_DOMINANT_FASTPATH=0 \
  "$IMAGE" \
  eval /test/test-data.json > "$NATIVE_EVAL_LOG"

cat "$NATIVE_EVAL_LOG"
grep -q 'native_eval total=54100 fp=0 fn=0 parse_errors=0' "$NATIVE_EVAL_LOG" || {
  echo "native accuracy gate failed; see $NATIVE_EVAL_LOG" >&2
  exit 1
}

if [ "$RUN_PROFILE_CHECK" = "1" ]; then
  docker run --rm \
    --entrypoint rinha-fraud \
    -v "$ROOT/test:/test" \
    -e KDTREE_NATIVE=1 \
    -e PROFILE_FASTPATH=1 \
    -e PROFILE_MIN_COUNT=15 \
    -e PROFILE_LEGIT_MIN_COUNT=5 \
    -e PROFILE_FRAUD_MIN_COUNT=15 \
    -e PROFILE_DOMINANT_FASTPATH=1 \
    -e PROFILE_DOMINANT_MIN_COUNT=15 \
    -e PROFILE_DOMINANT_MAX_OPPOSITE=2 \
    "$IMAGE" \
    eval /test/test-data.json > "$PROFILE_LOG" || true

  if grep -q 'fp=0 fn=0 parse_errors=0 weighted_errors=0' "$PROFILE_LOG"; then
    echo "profile fast path happened to pass this dataset; keep it disabled unless it is proven exact"
  else
    echo "profile fast path divergence captured in $PROFILE_LOG"
  fi
fi

if [ "$RUN_K6" = "1" ]; then
  PROJECT_NAME="$PROJECT_NAME" MODE=build sh "$ROOT/scripts/k6-local.sh"
  grep -q '"false_positive_detections": 0' "$ROOT/test/results.json" || exit 1
  grep -q '"false_negative_detections": 0' "$ROOT/test/results.json" || exit 1
  grep -q '"http_errors": 0' "$ROOT/test/results.json" || exit 1
  grep -q '"final_score": 6000' "$ROOT/test/results.json" || exit 1
fi

echo "local validation ok"
