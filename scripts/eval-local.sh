#!/bin/sh
set -eu

TEST_DATA="${1:-test/test-data.json}"
INDEX_PATH="${INDEX_PATH:-data/references.idx}"

dotnet build -c Release src/RinhaFraud/RinhaFraud.csproj >/dev/null
INDEX_PATH="$INDEX_PATH" \
dotnet run -c Release --no-build --project src/RinhaFraud/RinhaFraud.csproj -- eval "$TEST_DATA"
