# Fraud Score API

[Portuguese version](README.pt-BR.md)

Rinha de Backend 2026 fraud-scoring submission.

This repository is a hybrid .NET/C implementation. The submitted runtime keeps
the API process in .NET 10 Native AOT and calls a native C KD-tree classifier
through P/Invoke for the critical nearest-neighbor search.

## Architecture

The competitive Docker image contains three main runtime components:

- `rinha-fraud`: .NET 10 Native AOT CLI and HTTP server used by the submitted
  `api1` and `api2` services
- `rinha-lb`: C TCP load balancer
- `librinha_native.so`: C native classifier loaded by the .NET server through
  P/Invoke

Runtime topology:

- `lb` listens on port `9999`
- `lb` uses fd handoff over Unix sockets to distribute accepted TCP connections
- `api1` and `api2` run `rinha-fraud serve`
- the binary reference index is embedded in the Docker image
- `KDTREE_NATIVE=1` enables the native exact KD-tree search path inside the
  .NET API process

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
- .NET Native AOT API for the submitted hot path
- native C KD-tree search called through P/Invoke
- C TCP load balancer using fd handoff
- prebuilt JSON responses for every possible `fraud_score`
- profile and risky fallback logic retained as validated paths
- no payload lookup table and no fraud logic in the load balancer

## Structure

- `src/RinhaFraud/`: .NET API, CLI, index builder, eval, self-test, and classifier integration
- `src/native/`: native classifier/search runtime
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
