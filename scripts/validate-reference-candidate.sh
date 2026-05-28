#!/bin/sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
IMAGE="${IMAGE:-rinha-dotnet-reference-candidate:latest}"
PROJECT_NAME="${PROJECT_NAME:-rinha-reference-candidate}"
RUN_K6="${RUN_K6:-1}"
RUN_MIXED_K6="${RUN_MIXED_K6:-1}"
EVAL_SAFE_LOG="${EVAL_SAFE_LOG:-$ROOT/test/eval-reference-safe.log}"
EVAL_CANDIDATE_LOG="${EVAL_CANDIDATE_LOG:-$ROOT/test/eval-reference-candidate.log}"

need_file() {
  if [ ! -s "$1" ]; then
    echo "missing required file: $1" >&2
    exit 1
  fi
}

sha256_file() {
  sha256sum "$1" | awk '{print $1}'
}

need_file "$ROOT/resources/references.json.gz"
need_file "$ROOT/test/test-data.json"

refs_sha="$(sha256_file "$ROOT/resources/references.json.gz")"
test_sha="$(sha256_file "$ROOT/test/test-data.json")"
references_checksum="$(grep -o '"references_checksum_sha256":"[^"]*"' "$ROOT/test/test-data.json" | head -1 | cut -d: -f2 | tr -d '"')"

docker build \
  --build-arg TARGETARCH=amd64 \
  -t "$IMAGE" \
  "$ROOT"

docker run --rm --entrypoint rinha-fraud "$IMAGE" index-info /app/data/references.idx \
  | tee "$ROOT/test/index-info.log"

grep -q "references_gzip_sha256=$refs_sha" "$ROOT/test/index-info.log" || {
  echo "index metadata does not match resources/references.json.gz" >&2
  exit 1
}

docker run --rm \
  --entrypoint rinha-fraud \
  -v "$ROOT/test:/test" \
  -e EXPECTED_REFERENCES_GZIP_SHA256="$refs_sha" \
  -e KDTREE_NATIVE=1 \
  -e PROFILE_FASTPATH=0 \
  -e PROFILE_DOMINANT_FASTPATH=0 \
  "$IMAGE" \
  eval /test/test-data.json > "$EVAL_SAFE_LOG"

cat "$EVAL_SAFE_LOG"
grep -q 'fp=0 fn=0 parse_errors=0 weighted_errors=0' "$EVAL_SAFE_LOG" || {
  echo "safe reference eval failed; see $EVAL_SAFE_LOG" >&2
  exit 1
}

docker run --rm \
  --entrypoint rinha-fraud \
  -v "$ROOT/test:/test" \
  -e EXPECTED_REFERENCES_GZIP_SHA256="$refs_sha" \
  -e PROFILE_FASTPATH_REFERENCE_SHA256="$refs_sha" \
  -e EVAL_NATIVE_JSON=1 \
  -e KDTREE_NATIVE=1 \
  -e PROFILE_FASTPATH=1 \
  -e PROFILE_MIN_COUNT=15 \
  -e PROFILE_LEGIT_MIN_COUNT=15 \
  -e PROFILE_FRAUD_MIN_COUNT=8 \
  -e PROFILE_FRAUD_AMOUNT_MIN=4000 \
  -e PROFILE_FRAUD_LOW_AMOUNT_FASTPATH=1 \
  -e PROFILE_DOMINANT_FASTPATH=0 \
  -e BUCKET_FASTPATH=0 \
  -e EXACT_FALLBACK=risky \
  "$IMAGE" \
  eval /test/test-data.json > "$EVAL_CANDIDATE_LOG"

cat "$EVAL_CANDIDATE_LOG"
grep -q 'fp=0 fn=0 parse_errors=0 weighted_errors=0' "$EVAL_CANDIDATE_LOG" || {
  echo "candidate fast path is not safe for this reference set; keep PROFILE_FASTPATH=0" >&2
  exit 1
}

if [ "$RUN_K6" = "1" ]; then
  MODE=build \
  PROJECT_NAME="$PROJECT_NAME" \
  PROFILE_FASTPATH_REFERENCE_SHA256="$refs_sha" \
  EXPECTED_REFERENCES_GZIP_SHA256="$refs_sha" \
  PROFILE_FASTPATH=1 \
  PROFILE_LEGIT_MIN_COUNT=15 \
  PROFILE_FRAUD_MIN_COUNT=8 \
  PROFILE_FRAUD_AMOUNT_MIN=4000 \
  PROFILE_FRAUD_LOW_AMOUNT_FASTPATH=1 \
  BUCKET_FASTPATH=0 \
  ASSUME_BODY_COMPLETE=0 \
  ASSUME_FRAUD_SCORE_PATH=0 \
  ASSUME_JSON_BODY_START=0 \
  FASTPATH_CANARY_REQUESTS=0 \
  FASTPATH_CANARY_INTERVAL=0 \
  CLASSIFIER_PREWARM=64 \
  TP_PREWARM=64 \
  TP_MIN_IO_THREADS=4 \
  sh "$ROOT/scripts/k6-local.sh"

  grep -q '"false_positive_detections": 0' "$ROOT/test/results.json" || exit 1
  grep -q '"false_negative_detections": 0' "$ROOT/test/results.json" || exit 1
  grep -q '"http_errors": 0' "$ROOT/test/results.json" || exit 1
  grep -q '"final_score": 6000' "$ROOT/test/results.json" || exit 1
fi

if [ "$RUN_MIXED_K6" = "1" ]; then
  MODE=build \
  PROJECT_NAME="$PROJECT_NAME-mixed" \
  PAYLOAD_VARIANT=mixed \
  PROFILE_FASTPATH_REFERENCE_SHA256="$refs_sha" \
  EXPECTED_REFERENCES_GZIP_SHA256="$refs_sha" \
  PROFILE_FASTPATH=1 \
  PROFILE_LEGIT_MIN_COUNT=15 \
  PROFILE_FRAUD_MIN_COUNT=8 \
  PROFILE_FRAUD_AMOUNT_MIN=4000 \
  PROFILE_FRAUD_LOW_AMOUNT_FASTPATH=1 \
  BUCKET_FASTPATH=0 \
  ASSUME_BODY_COMPLETE=0 \
  ASSUME_FRAUD_SCORE_PATH=0 \
  ASSUME_JSON_BODY_START=0 \
  FASTPATH_CANARY_REQUESTS=0 \
  FASTPATH_CANARY_INTERVAL=0 \
  CLASSIFIER_PREWARM=64 \
  TP_PREWARM=64 \
  TP_MIN_IO_THREADS=4 \
  sh "$ROOT/scripts/k6-local.sh"

  grep -q '"false_positive_detections": 0' "$ROOT/test/results.json" || exit 1
  grep -q '"false_negative_detections": 0' "$ROOT/test/results.json" || exit 1
  grep -q '"http_errors": 0' "$ROOT/test/results.json" || exit 1
  grep -q '"final_score": 6000' "$ROOT/test/results.json" || exit 1
fi

cat > "$ROOT/test/reference-candidate-summary.env" <<EOF
REFERENCES_GZIP_SHA256=$refs_sha
TEST_DATA_SHA256=$test_sha
REFERENCES_CHECKSUM_SHA256=$references_checksum
IMAGE=$IMAGE
PROFILE_FASTPATH_REFERENCE_SHA256=$refs_sha
EOF

echo "reference candidate validation ok: $refs_sha"
