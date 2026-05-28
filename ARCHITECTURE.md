# Architecture

This repository is a hybrid `.NET/C` implementation for Rinha de Backend 2026.
This hybrid architecture must be preserved: the submitted API processes remain
`.NET 10 Native AOT`, while native C is used for the load balancer and for the
classifier search library called from `.NET` through P/Invoke.

## Runtime Topology

Services:

- `lb`: C TCP load balancer, entrypoint `rinha-lb`.
- `api1`: `.NET` API, entrypoint `rinha-fraud serve`.
- `api2`: `.NET` API, entrypoint `rinha-fraud serve`.

Request flow:

1. k6/client sends `POST /fraud-score` to `lb:9999`.
2. `rinha-lb` accepts the TCP connection.
3. In `LB_MODE=fdpass`, the LB transfers the accepted fd through Unix control
   sockets under `/sockets`.
4. `api1` or `api2` receives the fd and handles HTTP in the `.NET` process.
5. The `.NET` API parses the request and calls `librinha_native.so` for the
   native KD-tree search when `KDTREE_NATIVE=1`.
6. The `.NET` API writes one of the prebuilt JSON responses.

The load balancer is layer 4 only for fraud traffic. It must not classify, score
or inspect payload fields for fraud decisions.

## Components

- `src/RinhaFraud/`: `.NET` API, CLI, index builder, self-test, eval and
  integration with native search.
- `src/native/rinha_native.c`: native search library loaded by `.NET`.
- `src/native/rinha_native_api.c`: standalone native API binary kept for
  experiments/validation; not the current API entrypoint.
- `src/lb/rinha-lb.c`: C load balancer.
- `resources/references.json.gz`: official references used to build the binary
  index.
- `data/references.idx` or `/app/data/references.idx`: preprocessed binary
  index.
- `test/rinha-test.js`: local k6 harness using the public scoring formula.

## Classification Contract

The API converts the payload into a quantized vector, searches the reference
index and uses the five nearest neighbors:

- `fraud_score = fraud_neighbors / 5`
- `approved = fraud_score < 0.6`

The response must continue to include both fields:

```json
{"approved":true,"fraud_score":0.0}
```

Removing `fraud_score`, changing response shape or using payload lookup tables
is outside the accepted architecture.

Fast paths are allowed only as accelerators over the same classifier contract.
When enabled, `FASTPATH_CANARY_REQUESTS` / `FASTPATH_CANARY_INTERVAL` can sample
fast-path results against `SearchParams.WithoutFastPaths()`. If a sampled
request diverges, the API disables fast paths in that process and returns the
safe result.

## Index And References

The image embeds a binary index at `/app/data/references.idx`.

Build-time defaults:

- `BUILD_KDTREE_INDEX=1`
- `BUILD_NATIVE_ONLY_INDEX=1`
- `KDTREE_LEAF_SIZE=96`
- `KDTREE_KEY_PROFILE=0`

Reference changes are handled by:

- `scripts/reference-refresh.sh`
- `.github/workflows/refresh-references.yml`
- `.github/reference-state.env`

When `resources/references.json.gz` changes, rebuild the index and run local
validation before any remote issue. Do not assume profile fast paths remain
correct across reference changes.

## Important Runtime Variables

Core:

- `BIND_ADDR=fd:/sockets/apiN.sock.ctrl`
- `INDEX_PATH=/app/data/references.idx`
- `KDTREE_NATIVE=1`
- `FD_RAW=1`
- `TP_MIN_THREADS=64`
- `WORKERS=2`
- `LB_PRECONNECT_CONTROL=1`

Accuracy/default-safe:

- `PROFILE_FASTPATH=0`
- `PROFILE_DOMINANT_FASTPATH=0`
- `EXACT_FALLBACK=risky`

Experimental/default-off:

- `PROFILE_FASTPATH=1` with strict thresholds
- `FD_EPOLL=1`
- `FD_CONTROL_SEQPACKET=1`
- `FD_CONTROL_PREBUFFER=1`
- `FD_PRE_READ=1`
- `TCP_QUICKACK=1`
- `BUCKET_FASTPATH=1`
- `FASTPATH_CANARY_REQUESTS`
- `FASTPATH_CANARY_INTERVAL`
- `CLASSIFIER_PREWARM`
- `TP_PREWARM`

## Invariants

- Keep the hybrid `.NET/C` architecture. The submitted APIs are `.NET`; C is
  allowed for transport and native acceleration.
- APIs remain `.NET` unless explicitly requested otherwise.
- Native C can accelerate search through P/Invoke, but classification ownership
  remains in the API process.
- LB remains transport-only.
- Official payloads must not become lookup keys.
- `PROFILE_FASTPATH` must be treated as dataset-sensitive.
- `validate-local` is the gate before publication or remote testing.
