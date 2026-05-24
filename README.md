# Fraud Score API

[Portuguese version](README.pt-BR.md)

Rinha de Backend 2026 fraud-scoring submission.

This repository is a hybrid .NET/C implementation. The competitive submission
keeps the .NET 10 Native AOT CLI as the index builder, evaluator, and fallback
API, while the current latency-oriented API process is the native C runtime.

## Architecture

The competitive Docker image contains three main runtime components:

- `rinha-fraud`: .NET 10 Native AOT CLI, index builder, evaluator, and fallback
  HTTP server
- `rinha-native-api`: native C HTTP/fd-handoff API used by submitted `api1`
  and `api2`
- `rinha-lb`: C TCP load balancer
- `librinha_native.so`: shared native classifier used by the runtimes

Runtime topology:

- `lb` listens on port `9999`
- `lb` uses fd handoff over Unix sockets to distribute accepted TCP connections
- `api1` and `api2` run `rinha-native-api`
- the binary reference index is embedded in the Docker image
- `KDTREE_INDEX=1` enables exact partitioned KD-tree search in the native API

For fraud traffic, the load balancer only accepts and distributes connections.
It does not classify transactions and does not use fraud-related payload data.
Classification happens in the API process.

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
- native C API for the submitted hot path
- .NET Native AOT retained for build, eval, self-test, and fallback API work
- C TCP load balancer using fd handoff
- prebuilt JSON responses for every possible `fraud_score`
- KD-tree search used as the accuracy path for submitted requests
- profile and risky fallback logic retained for controlled experiments
- no payload lookup table and no fraud logic in the load balancer

## Structure

- `src/RinhaFraud/`: .NET API, CLI, index builder, eval, self-test, and classifier integration
- `src/native/`: native classifier/search runtime and submitted native API
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
- `KDTREE_INDEX`: enables KD-tree sections in the native API runtime
- `KDTREE_NATIVE`: enables KD-tree search through the native library from .NET
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

Run the full local gate before publishing:

```sh
sh scripts/validate-local.sh
```

From PowerShell:

```powershell
.\scripts\validate-local.ps1
```
