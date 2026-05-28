#!/bin/sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
RINHA_REF="${RINHA_REF:-main}"
STATE_PATH="${STATE_PATH:-$ROOT/.github/reference-state.env}"
WORK_DIR="${WORK_DIR:-$ROOT/.reference-refresh}"
IMAGE_NAME="${IMAGE_NAME:-ghcr.io/daniloitagyba/rinha-2026-dotnet-tcp}"
LOCAL_IMAGE="${LOCAL_IMAGE:-rinha-dotnet-reference-refresh:latest}"
PROJECT_NAME="${PROJECT_NAME:-rinha-reference-refresh}"
FORCE_REFRESH="${FORCE_REFRESH:-0}"
RUN_VALIDATE="${RUN_VALIDATE:-1}"
RUN_K6="${RUN_K6:-1}"
RUN_MIXED_K6="${RUN_MIXED_K6:-1}"
PUSH_IMAGE="${PUSH_IMAGE:-0}"
UPDATE_STATE="${UPDATE_STATE:-1}"

BASE_URL="${BASE_URL:-https://raw.githubusercontent.com/zanfranceschi/rinha-de-backend-2026/$RINHA_REF}"
REFS_URL="$BASE_URL/resources/references.json.gz"
TEST_URL="$BASE_URL/test/test-data.json"

mkdir -p "$WORK_DIR" "$ROOT/resources" "$ROOT/test"

download() {
  url="$1"
  output="$2"
  echo "download $url"
  curl -fsSL "$url" -o "$output"
}

sha256_file() {
  sha256sum "$1" | awk '{print $1}'
}

state_value() {
  key="$1"
  if [ ! -f "$STATE_PATH" ]; then
    return 0
  fi

  grep -E "^$key=" "$STATE_PATH" | tail -1 | cut -d= -f2- || true
}

set_output() {
  key="$1"
  value="$2"
  if [ -n "${GITHUB_OUTPUT:-}" ]; then
    printf '%s=%s\n' "$key" "$value" >> "$GITHUB_OUTPUT"
  fi
}

download "$REFS_URL" "$WORK_DIR/references.json.gz"
download "$TEST_URL" "$WORK_DIR/test-data.json"

refs_sha="$(sha256_file "$WORK_DIR/references.json.gz")"
test_sha="$(sha256_file "$WORK_DIR/test-data.json")"
old_refs_sha="$(state_value REFERENCES_GZIP_SHA256)"
refs_short="$(printf '%s' "$refs_sha" | cut -c1-12)"
image_tag="reference-$refs_short-${GITHUB_SHA:-local}"
candidate_image="$IMAGE_NAME:$image_tag"
stable_reference_image="$IMAGE_NAME:reference-$refs_short"
changed="0"

if [ "$FORCE_REFRESH" = "1" ] || [ "$refs_sha" != "$old_refs_sha" ]; then
  changed="1"
fi

set_output "changed" "$changed"
set_output "refs_sha" "$refs_sha"
set_output "refs_short" "$refs_short"
set_output "test_sha" "$test_sha"
set_output "image_tag" "$image_tag"
set_output "candidate_image" "$candidate_image"
set_output "state_updated" "0"

if [ "$changed" != "1" ]; then
  echo "references unchanged: $refs_sha"
  exit 0
fi

if [ "$RUN_VALIDATE" != "1" ] && { [ "$PUSH_IMAGE" = "1" ] || [ "$UPDATE_STATE" = "1" ]; }; then
  echo "refusing to publish or update reference state without validation" >&2
  exit 2
fi

cp "$WORK_DIR/references.json.gz" "$ROOT/resources/references.json.gz"
cp "$WORK_DIR/test-data.json" "$ROOT/test/test-data.json"

if [ "$RUN_VALIDATE" = "1" ]; then
  REFRESH_DATA=0 \
    FETCH_REFERENCES=1 \
    RUN_K6=0 \
    IMAGE="$LOCAL_IMAGE" \
    PROJECT_NAME="$PROJECT_NAME" \
    sh "$ROOT/scripts/validate-local.sh"

  RUN_K6="$RUN_K6" \
    RUN_MIXED_K6="$RUN_MIXED_K6" \
    IMAGE="$LOCAL_IMAGE" \
    PROJECT_NAME="$PROJECT_NAME-candidate" \
    sh "$ROOT/scripts/validate-reference-candidate.sh"
fi

if [ "$PUSH_IMAGE" = "1" ]; then
  docker buildx build \
    --platform linux/amd64 \
    --push \
    --provenance=false \
    -t "$candidate_image" \
    -t "$stable_reference_image" \
    "$ROOT"
fi

references_checksum="$(grep -o '"references_checksum_sha256":"[^"]*"' "$ROOT/test/test-data.json" | head -1 | cut -d: -f2 | tr -d '"')"
validated_at="$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
validated_commit="${GITHUB_SHA:-$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || echo local)}"

cat > "$ROOT/test/reference-refresh-summary.env" <<EOF
RINHA_REF=$RINHA_REF
REFERENCES_GZIP_SHA256=$refs_sha
TEST_DATA_SHA256=$test_sha
REFERENCES_CHECKSUM_SHA256=$references_checksum
CANDIDATE_IMAGE=$candidate_image
STABLE_REFERENCE_IMAGE=$stable_reference_image
VALIDATED_COMMIT=$validated_commit
VALIDATED_AT=$validated_at
EOF

if [ "$UPDATE_STATE" = "1" ]; then
  cat > "$STATE_PATH" <<EOF
# Last official reference dataset validated by the scheduled refresh pipeline.
RINHA_REF=$RINHA_REF
REFERENCES_GZIP_SHA256=$refs_sha
TEST_DATA_SHA256=$test_sha
REFERENCES_CHECKSUM_SHA256=$references_checksum
VALIDATED_IMAGE=$candidate_image
VALIDATED_COMMIT=$validated_commit
VALIDATED_AT=$validated_at
EOF
  set_output "state_updated" "1"
fi

echo "reference refresh validated: $refs_sha"
echo "candidate image: $candidate_image"
