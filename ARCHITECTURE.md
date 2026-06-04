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
- Compose networking must remain Docker bridge/default. Do not use host
  networking, `network_mode: "none"` or privileged containers.

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
Profile/bucket fast paths are dataset-sensitive and must be explicitly tied to
the reference fingerprint through `PROFILE_FASTPATH_REFERENCE_SHA256`; without a
matching fingerprint the `.NET` API disables those fast paths even if
`PROFILE_FASTPATH=1` is present.
When enabled, `FASTPATH_CANARY_REQUESTS` / `FASTPATH_CANARY_INTERVAL` can sample
fast-path results against `SearchParams.WithoutFastPaths()`. If a sampled
request diverges, the API disables fast paths in that process and returns the
safe result.

## Index And References

The image embeds a binary index at `/app/data/references.idx`.
The index carries a small build-info section that records reference count,
decompressed JSON SHA-256, optional gzip SHA-256, KD-tree leaf size and key
profile. The CLI command `rinha-fraud index-info /app/data/references.idx`
prints this metadata.

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

Runtime reference guards:

- `EXPECTED_REFERENCES_GZIP_SHA256`: fail API startup/eval if the embedded index
  was not built from the expected compressed references.
- `EXPECTED_REFERENCES_JSON_SHA256`: same check for decompressed JSON content.
- `PROFILE_FASTPATH_REFERENCE_SHA256`: comma-separated allow-list for
  dataset-sensitive fast paths. If absent or mismatched, profile/bucket fast
  paths are disabled.

`scripts/reference-refresh.sh` runs the safe validation path and then
`scripts/validate-reference-candidate.sh`, which verifies index metadata,
safe `eval`, gated fast-path `eval`, and optional k6 before a reference image is
considered publishable.

## Important Runtime Variables

Core:

- `BIND_ADDR=fd:/sockets/apiN.sock.ctrl`
- `INDEX_PATH=/app/data/references.idx`
- `KDTREE_NATIVE=1`
- `FD_RAW=1`
- `FD_EPOLL=1` can be enabled for the current best local bundle, keeping the API
  in `.NET` while using a managed epoll loop for accepted fds.
- `FD_EPOLL_TIMEOUT_MS=1` is the current best local epoll setting; the default
  remains `-1` unless explicitly set by the benchmark/submission environment.
- `TP_MIN_THREADS=64`
- `WORKERS=2`
- `LB_PRECONNECT_CONTROL=1`

Accuracy/default-safe:

- `PROFILE_FASTPATH=0`
- `PROFILE_DOMINANT_FASTPATH=0`
- `EXACT_FALLBACK=risky`
- `EXPECTED_REFERENCES_GZIP_SHA256` when validating a known reference set

Experimental/default-off:

- `PROFILE_FASTPATH=1` with strict thresholds
- `PROFILE_FASTPATH_REFERENCE_SHA256=<current references sha256>`
- `FD_EPOLL=1` / `FD_EPOLL_TIMEOUT_MS=1`
- `FD_CONTROL_SEQPACKET=1`
- `FD_CONTROL_PREBUFFER=1`
- `FD_PRE_READ=1`
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
- Compose remains on Docker bridge/default networking; host networking,
  `network_mode: none` and `privileged: true` are not allowed.
- Official payloads must not become lookup keys.
- `PROFILE_FASTPATH` must be treated as dataset-sensitive.
- `validate-local` is the gate before publication or remote testing.
