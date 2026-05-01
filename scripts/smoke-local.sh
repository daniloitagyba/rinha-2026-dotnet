#!/bin/sh
set -eu

PORT="${PORT:-18080}"
INDEX_PATH="${INDEX_PATH:-data/example-references.idx}"

dotnet build -c Release src/RinhaFraud/RinhaFraud.csproj
scripts/build-index.sh resources/example-references.json "$INDEX_PATH"

BIND_ADDR="127.0.0.1:$PORT" \
INDEX_PATH="$INDEX_PATH" \
MIN_CANDIDATES=5 \
MAX_CANDIDATES=100 \
dotnet run -c Release --no-build --project src/RinhaFraud/RinhaFraud.csproj -- serve &

pid="$!"
trap 'kill "$pid" 2>/dev/null || true' EXIT INT TERM

sleep 1

curl -fsS "http://127.0.0.1:$PORT/ready"
echo
curl -fsS -H 'Content-Type: application/json' \
  --data-binary @resources/example-payload-legit.json \
  "http://127.0.0.1:$PORT/fraud-score"
echo
curl -fsS -H 'Content-Type: application/json' \
  --data-binary @resources/example-payload-fraud.json \
  "http://127.0.0.1:$PORT/fraud-score"
echo
