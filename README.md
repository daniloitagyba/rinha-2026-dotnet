# Fraud Score API

[Portuguese version](README.pt-BR.md)

HTTP API for calculating fraud risk from a transaction payload.

## Architecture

- custom TCP load balancer in C using `epoll`
- two .NET Native AOT API instances
- raw HTTP server, without Kestrel on the main path
- internal communication over Unix sockets
- binary reference index embedded in the Docker image

The load balancer only accepts connections, selects an API instance, and copies
bytes between client and backend. Classification runs only inside the API
instances.

## Classification

The payload is converted into a quantized 14-dimension vector. The API searches
for the 5 nearest neighbors in the reference index and computes:

- `fraud_score = fraud_neighbors / 5`
- `approved = fraud_score < 0.6`

The reference index is preprocessed into a binary format to reduce startup cost
and avoid JSON parsing at runtime.

## Endpoints

- `GET /ready`: reports that the instance is ready
- `POST /fraud-score`: receives the transaction payload and returns the decision

Classification response:

```json
{"approved":true,"fraud_score":0.0}
```

## Implementation Decisions

- allocation-free vectorization on the hot path
- bucket-clustered index to reduce the initial search space
- profile fast path when the local decision is stable
- exact fallback restricted to the highest-risk reference subset
- compact fallback with SIMD (`AVX2` when available, `SSE2` fallback)
- prebuilt HTTP responses for every possible `fraud_score` value

## Structure

- `src/RinhaFraud/`: API, parser, vectorization, and classification index
- `src/lb/`: TCP load balancer
- `scripts/`: local build, validation, and load scripts
- `resources/`: references used to build the binary index
- `test/`: local validation harness

## Configuration

Main API environment variables:

- `BIND_ADDR`: listen address, usually `unix:/sockets/api1.sock`
- `INDEX_PATH`: binary index path
- `SERVER_MODE`: raw HTTP server mode
- `WORKERS`: worker count per instance
- `TP_MIN_THREADS`: ThreadPool minimum thread count
- `EARLY_CANDIDATES`, `MIN_CANDIDATES`, `MAX_CANDIDATES`: search limits
- `PROFILE_FASTPATH`: enables or disables the profile fast path
- `EXACT_FALLBACK`: exact fallback mode
- `RISKY_FINE_BUCKETS`: enables boolean sub-buckets inside the risky fallback
- `RISKY_SIMD`: enables or disables SIMD in the risky fallback

## Commands

Build the index:

```powershell
scripts/build-index.sh resources/references.json.gz data/references.idx
```

Run the self-test:

```powershell
dotnet run -c Release --project src/RinhaFraud/RinhaFraud.csproj -- self-test
```

Build the local image:

```powershell
docker compose build
```
