#!/bin/sh
set -eu

REF="${RINHA_REF:-main}"
ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
FORCE="${FORCE:-0}"
FETCH_REFERENCES="${FETCH_REFERENCES:-0}"

mkdir -p "$ROOT/test" "$ROOT/resources"

download_official_file() {
  path="$1"
  output="$2"

  if [ -f "$output" ] && [ "$FORCE" != "1" ]; then
    echo "exists $output"
    return
  fi

  url="https://raw.githubusercontent.com/zanfranceschi/rinha-de-backend-2026/$REF/$path"
  echo "download $url"
  curl -fsSL "$url" -o "$output"
}

download_official_file "test/test-data.json" "$ROOT/test/test-data.json"

if [ "$FETCH_REFERENCES" = "1" ]; then
  download_official_file "resources/references.json.gz" "$ROOT/resources/references.json.gz"
fi
