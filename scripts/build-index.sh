#!/bin/sh
set -eu

INPUT="${1:-resources/references.json.gz}"
OUTPUT="${2:-data/references.idx}"

dotnet build -c Release src/RinhaFraud/RinhaFraud.csproj >/dev/null
mkdir -p "$(dirname "$OUTPUT")"

case "$INPUT" in
  *.gz)
    refs_sha="$(sha256sum "$INPUT" | awk '{print $1}')"
    gzip -dc "$INPUT" | REFERENCES_GZIP_SHA256="$refs_sha" dotnet run -c Release --no-build --project src/RinhaFraud/RinhaFraud.csproj -- build-index "$OUTPUT"
    ;;
  *) dotnet run -c Release --no-build --project src/RinhaFraud/RinhaFraud.csproj -- build-index "$OUTPUT" < "$INPUT" ;;
esac
