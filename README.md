# Fraud Score API

[Portuguese version](README.pt-BR.md)

Rinha de Backend 2026 fraud-scoring submission.

This repository is now a hybrid .NET/C implementation. The .NET project still
owns the model pipeline, reference preprocessing, diagnostics, self-test, and
offline evaluation. The published hot path that serves benchmark traffic runs
through native C binaries.

## Current Result

Published official run:

- issue: `#5616`
- p99: `0.98ms`
- final score: `6000`
- false positives: `0`
- false negatives: `0`
- HTTP errors: `0`

## Architecture

The competitive Docker image contains three main binaries:

- `rinha-fraud`: .NET 10 Native AOT CLI used for `build-index`, `eval`,
  `self-test`, and as a fallback server
- `rinha-lb`: C TCP load balancer
- `rinha-native-api`: C API used by `api1` and `api2` in the submitted compose

Runtime topology:

- `lb` listens on port `9999`
- `lb` uses fd handoff over Unix sockets to distribute accepted TCP connections
- `api1` and `api2` run `rinha-native-api`
- the binary reference index is embedded in the Docker image
- `KDTREE_INDEX=1` enables the current exact KD-tree search path

For fraud traffic, the load balancer only accepts and distributes connections.
It does not classify transactions and does not use fraud-related payload data.
Classification happens in the API process.

## .NET Role

.NET is still the organizing core of the repository:

- builds the reference index from `references.json.gz`
- writes the binary `references.idx`
- owns self-test and offline evaluation commands
- keeps the original C# classifier, parser, vectorizer, and diagnostic code
- drives the Docker build through `dotnet publish` with Native AOT

The current runtime winner is not a pure .NET API. It is better described as:

```text
.NET 10 Native AOT pipeline + C native hot path
```

## Classification

The payload is converted into a quantized vector. The API searches for the 5
nearest neighbors in the reference index and computes:

- `fraud_score = fraud_neighbors / 5`
- `approved = fraud_score < 0.6`

The index is preprocessed into a binary format before the image is built. The
current published index includes partitioned KD-tree sections so runtime search
does not need to scan the full reference set for every request.

## Endpoints

- `GET /ready`: readiness check
- `POST /fraud-score`: transaction classification

Classification response:

```json
{"approved":true,"fraud_score":0.0}
```

## Implementation Decisions

- preprocessed binary index in the Docker image
- exact partitioned KD-tree search for the current public baseline
- native C API for the benchmark hot path
- C TCP load balancer using fd handoff
- prebuilt JSON responses for every possible `fraud_score`
- profile and risky fallback logic retained as validated paths
- no payload lookup table and no fraud logic in the load balancer

## Structure

- `src/RinhaFraud/`: .NET CLI, index builder, eval, self-test, and original classifier
- `src/native/`: native API and native classifier/search runtime
- `src/lb/`: TCP load balancer
- `scripts/`: local build, validation, release, and load scripts
- `resources/`: references used to build the binary index
- `test/`: local validation harness and remote result snapshots
- `docker-compose.yml`: local build and benchmark topology
- `submission` branch: official compose and metadata used by the bot

## Configuration

Main runtime variables:

- `BIND_ADDR`: listen or fd-handoff address
- `INDEX_PATH`: binary index path
- `KDTREE_INDEX`: enables KD-tree sections in the native runtime
- `WORKERS`: worker count per API instance
- `EARLY_CANDIDATES`, `MIN_CANDIDATES`, `MAX_CANDIDATES`: search limits for non-KD paths
- `PROFILE_FASTPATH`: enables the profile fast path
- `EXACT_FALLBACK`: exact fallback mode
- `RISKY_NATIVE_FINE`: enables the native fine fallback path
- `LB_MODE`: load-balancer mode, currently `fdpass`

## Commands

Run the self-test:

```powershell
dotnet run -c Release --project src/RinhaFraud/RinhaFraud.csproj -- self-test
```

Build the local image:

```powershell
docker compose build
```

Run the official-like local benchmark:

```powershell
.\scripts\k6-local.ps1 -Mode build
```

Run the same path from WSL/Linux:

```sh
MODE=build sh scripts/k6-local.sh
```
