# Progress

This file keeps the useful conclusions from tuning. It is intentionally a
summary, not a raw session log.

## Current Target

- Goal: local official k6 `p99 <= 0.50ms`, zero errors, APIs still in `.NET`.
- Current best observed full local result on the `.NET` API line:
  `p99=0.53ms`, `final_score=6000`, `fp=0`, `fn=0`, `http_errors=0`,
  `failure_rate=0`. Repeated full runs still vary around `0.53ms-0.55ms`.
- The `0.50ms` target is not achieved yet.

## Current Safe Baseline

The current safe default keeps:

- `api1/api2`: `entrypoint: ["rinha-fraud", "serve"]`
- `PROFILE_FASTPATH=0`
- `PROFILE_DOMINANT_FASTPATH=0`
- `KDTREE_NATIVE=1`
- `KDTREE_LEAF_SIZE=96`
- `KDTREE_KEY_PROFILE=0`
- `lb=0.12 CPU`, `api1/api2=0.44 CPU`
- `TP_MIN_THREADS=64`

Reason: after the reference/test data changed, aggressive profile fast paths
created false positives/false negatives remotely. The safe path prioritizes
zero detection errors.

## Remote History

- Previous official preview data: a `.NET` API version with native KD-tree
  search reached remote `final_score=6000`, `p99=0.96ms`, zero errors.
- After the data changed, the same class of profile shortcuts was no longer
  safe. A safe `.NET` submission with profile fast path off reached
  `p99=1.37ms`, `final_score=5864.77`, zero errors.
- A later `.NET` candidate improved remote to about `p99=1.12ms`,
  `final_score=5949.01`, zero errors.
- Current remote goal for `6000`: keep zero errors and reduce p99 to
  `<=1.00ms`.

## Best Local Candidate Bundle

Best complete local result from the recent `.NET` line:

```sh
MODE=build \
NATIVE_CFLAGS_EXTRA="-DJSON_FIXED_NUMBERS=1 -DKD_BEST_FIRST=1" \
PROFILE_FASTPATH=1 \
PROFILE_LEGIT_MIN_COUNT=15 \
PROFILE_FRAUD_MIN_COUNT=8 \
PROFILE_FRAUD_AMOUNT_MIN=4000 \
PROFILE_FRAUD_LOW_AMOUNT_FASTPATH=1 \
BUCKET_FASTPATH=0 \
ASSUME_BODY_COMPLETE=1 \
ASSUME_FRAUD_SCORE_PATH=1 \
ASSUME_JSON_BODY_START=1 \
TP_PREWARM=64 \
sh scripts/k6-local.sh
```

Result: `p99=0.54ms`, `final_score=6000`, zero errors.

This result also depends on the local Docker bridge options now persisted in
`docker-compose.yml`:

- `com.docker.network.bridge.enable_ip_masquerade=false`
- `com.docker.network.bridge.enable_icc=true`

Use this only as an experimental comparison bundle. It is not the safe default
for changed references.

## What Worked

- Binary preprocessed index embedded in the Docker image.
- C load balancer with fd handoff.
- `.NET Native AOT` API with raw fd handling.
- Native KD-tree search called from `.NET` through P/Invoke.
- `KDTREE_LEAF_SIZE=96` as the best persisted leaf-size signal.
- Local Docker bridge with IP masquerade disabled reduced full k6 from the
  `0.56ms` range to `0.54ms`.
- `[SuppressGCTransition]` on native classifier P/Invokes improved eval
  latency and remained correct in k6, but the full-k6 gain is dominated by
  network/transport.
- `PROFILE_FASTPATH=0` as the safe default after reference changes.
- Automated reference refresh workflow that rebuilds the index and validates
  before publishing a candidate image.

## What Not To Repeat Without New Evidence

Transport and LB:

- `FD_EPOLL=1`: correct, but complete k6 stayed around `0.57ms-0.59ms`.
- `FD_CONTROL_SEQPACKET=1` and `FD_CONTROL_PREBUFFER=1`: correct after the
  `send_fd` fix, but did not beat the baseline.
- `FD_CONTROL_CONNECTIONS=2`: multiple fdpass control channels were correct,
  but complete k6 stayed at `p99=0.54ms`; implementation was removed.
- `FD_PRE_READ=1`: sometimes matched the floor, did not improve complete k6.
- `FD_SOCKET_ASYNC`: smoke worsened to about `0.71ms`; removed.
- `FD_DEDICATED_THREADS=1`: correct, no sustained gain.
- `LB_FAST2=1`: no gain and can create errors in combinations.
- `EPOLL_ET=1`: smoke looked good, complete k6 only tied around `0.56ms`.
- `TCP_QUICKACK=1`: no stable gain.
- `TCP_DEFER_ACCEPT=1`: smoke worsened to about `0.60ms`.
- socket buffer sweeps and `LB_SOCKET_BUFFERS=0`: no stable gain.
- lowering `LB_TCP_NODELAY`: worsened.
- `[SuppressGCTransition]` on libc `send(2)`: short k6 reached `0.53ms`,
  but full k6 regressed to `0.56ms`; keep it off.
- Docker network `internal: true`: backend did not become ready through the
  published port; keep it off for the official local/remote path.
- `FD_RECEIVERS=3`: complete k6 worsened to `p99=0.55ms`.
- bind-mounting `/sockets` from `/tmp` or `/dev/shm`: no complete-k6 gain;
  `/dev/shm` reached `0.53ms` in short k6 but `0.55ms` in complete k6.
- `FD_CONTROL_SEQPACKET=1` with `FD_CONTROL_PREBUFFER=1`: short k6 matched
  the baseline, but complete k6 produced `209` HTTP errors and `p99=0.60ms`.
- `LB_FDPASS_BLOCKING_ACCEPT=1`: blocking accept loop in the LB worsened short
  k6 to `p99=0.55ms`; experiment was removed.
- `FD_PRE_READ=1`: short k6 stayed at `p99=0.54ms`.
- `FD_DEDICATED_THREADS=1` with `FD_THREAD_STACK_KB=64`: short k6 worsened to
  `p99=0.58ms`.
- Unix datagram fdpass (`FD_CONTROL_DGRAM=1`) was correct but did not improve:
  short k6 stayed at `p99=0.54ms`; with prebuffer it worsened to `0.55ms`.
- `LB_FDPASS_NONBLOCK=1` was rejected: accepted sockets reached the API as
  nonblocking fds and short k6 produced `5633` HTTP errors.

Runtime:

- `TP_MIN_THREADS=32`: caused major cauda and errors.
- `TP_MIN_THREADS=96/128/192+`: no stable gain.
- `TP_PREFER_LOCAL=1`: complete k6 worsened to `p99=0.55ms`.
- `DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=64`: smoke looked better, but
  complete k6 worsened to `p99=0.55ms`.
- `DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=0`: short k6 reached `p99=0.53ms`,
  matching the historical floor but not proving a better complete result.
- `DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=256`: short k6 stayed at
  `p99=0.54ms`.
- `DOTNET_PROCESSOR_COUNT=1/2/4`: no gain.
- `GC_LATENCY_MODE=sustained-low-latency`, `DOTNET_GCHeapCount`,
  `DOTNET_GCConserveMemory`: no gain.
- cpuset changes and CPU splits around `0.08/0.46`, `0.10/0.45`,
  `0.14/0.43`: no stable complete-k6 gain.
- isolating app cpusets to leave a core for k6 (`lb=0`, `api1=1`,
  `api2=2`) did not improve smoke.
- moving `api1/api2` to `network_mode: none` was correct but complete k6
  stayed at `p99=0.54ms`.
- CPU split recheck after bridge opts did not produce a candidate:
  `lb=0.10/api=0.45` only matched short k6 at `0.53ms`, while
  `lb=0.08/api=0.46` and `lb=0.14/api=0.43` stayed around `0.54ms`.
- later CPU split recheck with more LB budget also did not produce a target
  candidate: `lb=0.16/api=0.42` and `lb=0.18/api=0.41` reached `0.53ms` in
  short k6, but `lb=0.16/api=0.42` complete k6 stayed at `p99=0.54ms`;
  `lb=0.20/api=0.40` worsened short k6 to `0.54ms`.
- `lb=0.16/api=0.42` combined with `LB_FAST2=1` worsened short k6 to
  `p99=0.55ms`.
- broad cpusets (`LB_CPUSET=0,1,2,3` and `API_CPUSET=0,1,2,3`) worsened short
  k6 to `p99=0.55ms`.
- increasing API memory to `159MB` each worsened short k6 to `p99=0.55ms`.
- limiting keep-alive requests did not help in direct tests:
  `KEEP_ALIVE_REQUESTS=32` worsened short k6 to `0.55ms`; `128` stayed at
  `0.54ms`.

Classifier/index:

- `KDTREE_LEAF_SIZE=64/80/112/128`: did not beat leaf `96` on complete k6.
- `KDTREE_KEY_PROFILE=1+`: may improve eval in spots, did not beat k6.
- `KDTREE_MAX_PARTITIONS` reduction: no useful gain and can risk accuracy.
- `KD_NODE_QUEUE_SIZE=512` looked promising in short k6 (`p99=0.51ms`) but
  complete k6 regressed to `p99=0.56ms`; `256` was already worse in short k6
  at `p99=0.55ms`.
- `KD_SCALAR_EARLY=1` worsened short k6 to `p99=0.57ms`; keep AVX2 leaf
  distance.
- disabling `KD_BEST_FIRST=1` stayed at `p99=0.54ms` in short k6; keep
  best-first enabled.
- `PROFILE_DOMINANT_FASTPATH=1`: small coverage increase, but generated false
  positives.
- `BUCKET_FASTPATH=1`: JSON path produced false positives or worse k6.
- More aggressive `PROFILE_FRAUD_AMOUNT_MIN` below about `3910`: false
  positives on current public data.
- `PROFILE_FRAUD_AMOUNT_MIN=3950` passed native JSON eval with zero errors but
  short k6 stayed at `p99=0.54ms`; keep `4000` as the less aggressive
  reference.
- `PROFILE_LEGIT_MIN_COUNT=6`: false negative.
- reducing candidate counts: no complete-k6 win.

Build/codegen:

- `-flto`: worsened smoke.
- `-Ofast`: did not improve complete results.
- `-fno-plt -fno-semantic-interposition`: no gain.
- `-march=znver2`: not portable and no durable gain.
- `-falign-functions=32 -falign-loops=32`: worsened short k6 to `p99=0.55ms`.
- `.NET` `DisableRuntimeMarshalling=true`: build completed but the backend did
  not become ready, so it is not compatible with the current interop surface.
- native `-fno-unwind-tables -fno-asynchronous-unwind-tables` worsened short k6
  to `p99=0.55ms`.
- direct native JSON function-pointer call via `NativeLibrary` was correct but
  worsened short k6 to `p99=0.55ms`; experiment was removed.

Response/header micro-optimizations:

- removing reason phrase `OK`: no gain.
- removing/changing `fraud_score`: not allowed for the contract and did not
  help.
- `SelectResponseBytes`/`ReadOnlyMemory` rewrite: no gain.
- body-end/header fast paths: no gain.
- raw-fd stack buffer instead of `ArrayPool`: smoke worsened to `p99=0.55ms`;
  reverted.
- reducing `.NET` `MaxRequestBytes` from 8 KiB to 2 KiB was correct on current
  data but complete k6 worsened to `p99=0.55ms`; reverted.
- reducing `.NET` `MaxRequestBytes` from 8 KiB to 4 KiB was correct but short
  k6 stayed at `p99=0.54ms`; reverted.
- `ASSUME_NATIVE_JSON=1`: skipping the managed fallback after native JSON
  failure worsened short k6 to `p99=0.55ms`; experiment was removed.
- `THREAD_STATIC_BUFFER=1`: replacing `ArrayPool` with a thread-static raw-fd
  buffer worsened short k6 to `p99=0.55ms`; experiment was removed.
- `ASSUME_NO_EXCEPTIONS=1`: removing the hot-path `try/catch` via toggle stayed
  at `p99=0.54ms`; experiment was removed.
- `FAST_LIBC_IO=1`: using `send/recv` P/Invokes without `SetLastError` stayed
  at `p99=0.54ms`; experiment was removed.
- `FD_SINGLE_READ_FASTPATH=1`: one-read raw-fd request path reached `0.53ms`
  in short k6 but complete k6 stayed at `p99=0.54ms`; experiment was removed.
- fixed fd worker queue instead of `ThreadPool.UnsafeQueueUserWorkItem` was
  rejected: short k6 stayed at `0.55ms` with 64 workers, `0.54ms` with 128
  workers and `0.56ms` with 256 workers; experiment was removed.
- reverse search for JSON body start was rejected: short k6 produced `5925`
  false negatives because the request JSON has nested `{` characters; experiment
  was removed.
- skipping the `POST /fraud-score` prefix check inside `RequestComplete` when
  `ASSUME_FRAUD_SCORE_PATH=1` stayed at `p99=0.54ms` in short k6; experiment
  was removed.
- assuming single `send()` writes the whole tiny response was correct but
  worsened short k6 to `p99=0.56ms`; experiment was removed.
- using `read(2)` instead of `recv(2)` for blocking raw-fd request reads was
  correct but stayed at `p99=0.54ms`; experiment was removed.

## Profiling Notes

- A short `perf record` during local k6 showed most sampled CPU in the k6
  runner and kernel bridge/netfilter paths; `.NET` worker samples were much
  smaller and led by Docker/network syscalls plus native KD-tree search.
- Temporarily disabling WSL bridge netfilter was only diagnostic and was
  restored; it did not provide a publishable application optimization.

## Reference Change Rule

When references change:

1. Rebuild `references.idx`.
2. Run `validate-local`.
3. Verify `PROFILE_FASTPATH=0` and `PROFILE_DOMINANT_FASTPATH=0` for the safe
   path.
4. Use `EVAL_NATIVE_JSON=1` when validating native JSON fast paths.
5. Publish a candidate image only after local `eval` and k6 are clean.
6. Do not move the `submission` branch or open remote automatically.

## Next Plausible Work

- Profile the clean `.NET` fdpass path after `TP_PREWARM=64` to identify the
  remaining p99 source.
- Focus on reducing runtime/socket scheduling overhead; classifier-only gains
  have not moved official k6 enough.
- Treat any new profile shortcut as unsafe until it passes `EVAL_NATIVE_JSON=1`
  and full k6 with zero errors.
