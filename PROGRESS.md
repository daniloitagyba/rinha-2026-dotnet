# Progress

This file keeps the useful conclusions from tuning. It is intentionally a
summary, not a raw session log.

## Current Target

- Goal: local official k6 `p99 <= 0.50ms`, zero errors, APIs still in `.NET`.
- Best observed full local result on the `.NET` API line:
  `p99=0.46ms`, `final_score=6000`, `fp=0`, `fn=0`, `http_errors=0`,
  `failure_rate=0`, with the reference-gated profile fast path plus
  `FD_EPOLL=1` and `FD_EPOLL_TIMEOUT_MS=1`.
  The same bundle was immediately repeated at `p99=0.47ms` and `p99=0.46ms`.
- The local `0.50ms` / `0.52ms` targets are achieved for the current reference
  set by that protected experimental bundle. This has not been promoted to the
  safe default and has not been remote-tested yet.

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
- `TP_MIN_IO_THREADS=4`
- `lb.sysctls.net.core.somaxconn=65535`
- `LB_PRECONNECT_CONTROL=1` in fdpass LB, to open backend control channels
  before the first client traffic when possible.
- Current best local bundle additionally enables `FD_EPOLL=1` and
  `FD_EPOLL_TIMEOUT_MS=1`; keep this treated as an explicit benchmark/submission
  setting, not as a generic safe default.

Reason: after the reference/test data changed, aggressive profile fast paths
created false positives/false negatives remotely. The safe path prioritizes
zero detection errors.

## Final Test Preparation

Implemented guards for final-test drift:

- `SearchParams.WithoutFastPaths()` gives the API a safe classifier profile
  that disables profile/bucket fast paths while preserving candidate limits.
- `FASTPATH_CANARY_REQUESTS` and `FASTPATH_CANARY_INTERVAL` can sample fast
  path decisions against the safe classifier. On the first divergence, the API
  disables fast paths process-wide and returns the safe result for that request.
- `CLASSIFIER_PREWARM` can warm native JSON parsing, native KD-tree search,
  and safe fallback search during API startup.
- `PAYLOAD_VARIANT` in the k6 runner can test `pretty`, `reordered`,
  `padded-reordered`, and `mixed` JSON payloads.

Validation on 2026-05-28:

- Conservative final-safe mode, with profile/bucket fast paths off and JSON
  assumptions off: full local k6 `p99=0.57ms`, `final_score=6000`, zero
  errors.
- Optimized profile with light canary
  (`FASTPATH_CANARY_REQUESTS=256`, `FASTPATH_CANARY_INTERVAL=512`) and
  `CLASSIFIER_PREWARM=64`: full local k6 `p99=0.57ms`,
  `final_score=6000`, zero errors.
- Optimized profile with light canary and `PAYLOAD_VARIANT=mixed`: full local
  k6 `p99=0.56ms`, `final_score=6000`, zero errors. This validates reordered,
  pretty, and padded JSON fallbacks against the current data.
- Forced-bad fast-path thresholds proved the limit of sampling: the light
  canary missed a rare mismatch and allowed `1` false positive. Strong initial
  canary (`FASTPATH_CANARY_REQUESTS=4096`, interval `0`) caught the issue and
  kept zero errors, but raised local p99 to about `0.74ms`.
- Pressure test above the official local rate (`TARGET_RATE=1200`) had zero
  classification divergences but still produced `33-36` HTTP timeouts. Treat
  this as overload/startup sensitivity under non-official load; do not assume a
  heavier final script is safe without a matching gate.

Decision on 2026-05-28: because the official final test will use a different
payload after the preview window, publish a final-prep candidate that prioritizes
zero errors over the preview-best p99. For `submission`, prefer
`PROFILE_FASTPATH=0`, `BUCKET_FASTPATH=0`, and `ASSUME_* = 0`, with
`CLASSIFIER_PREWARM=64`. Keep the canary available in the image for diagnostics
or a later faster candidate, but do not rely on light sampling as a correctness
proof for unknown data.

Remote result for that final-prep candidate, issue
`https://github.com/zanfranceschi/rinha-de-backend-2026/issues/7092`, was worse
than the previous best: image
`ghcr.io/daniloitagyba/rinha-2026-dotnet-tcp:208d0da52aa614618d3c0614f80c5e09bacb4187`,
submission commit `18b7d44`, `p99=1.12ms`, `final_score=5950.75`, zero
errors. The `submission` branch was rolled back to commit `48379b5`, pointing
again to the best remote image
`ghcr.io/daniloitagyba/rinha-2026-dotnet-tcp:8fd4821774e3cd7adde699547dd6663ea4883d99`.

Next local-only candidate after the rollback: keep profile fast path enabled,
but turn off the fragile JSON/body/path assumptions:

```sh
MODE=build \
PROFILE_FASTPATH=1 \
PROFILE_LEGIT_MIN_COUNT=15 \
PROFILE_FRAUD_MIN_COUNT=8 \
PROFILE_FRAUD_AMOUNT_MIN=4000 \
PROFILE_FRAUD_LOW_AMOUNT_FASTPATH=1 \
BUCKET_FASTPATH=0 \
ASSUME_BODY_COMPLETE=0 \
ASSUME_FRAUD_SCORE_PATH=0 \
ASSUME_JSON_BODY_START=0 \
FASTPATH_CANARY_REQUESTS=0 \
FASTPATH_CANARY_INTERVAL=0 \
CLASSIFIER_PREWARM=64 \
TP_PREWARM=64 \
TP_MIN_IO_THREADS=4 \
sh scripts/k6-local.sh
```

Local results: default payload `p99=0.54ms`, `final_score=6000`, zero errors;
`PAYLOAD_VARIANT=mixed` `p99=0.55ms`, `final_score=6000`, zero errors. This is
the best current direction before another remote attempt, but it is still a
profile fast-path candidate and must be treated as data-sensitive.

Additional final-candidate rechecks after comparing with the current top
`.NET` entry (`fksegundo/rinha-dotnet`):

- Canary does not improve p99. Short k6: no canary `0.52ms`,
  `FASTPATH_CANARY_REQUESTS=128` `0.53ms`, `256` `0.54ms`, `512` `0.56ms`;
  complete k6 with canary `64/128` stayed at `0.55ms`, while no canary
  rechecked at `0.54ms`.
- Prewarm tuning did not beat the current candidate. Short k6:
  `CLASSIFIER_PREWARM=0 TP_PREWARM=0` `0.56ms`,
  `0/64` `0.53ms`, `64/0` `0.57ms`, `128/64` `0.53ms`.
  Complete k6: `0/64` `0.55ms`, `128/64` `0.54ms`. Keep
  `CLASSIFIER_PREWARM=64` and `TP_PREWARM=64` only as the current balanced
  default.
- `MALLOC_ARENA_MAX=2` and `DOTNET_gcServer=0`, copied from the top `.NET`
  runtime posture, did not produce a new candidate. Short k6 sequence on the
  current profile bundle: baseline `0.55ms`, `MALLOC_ARENA_MAX=2` `0.54ms`,
  `DOTNET_gcServer=0` `0.55ms`, both `0.54ms`; no case beat the known
  `0.54ms` full-k6 floor.
- Rechecking the `.NET` fd event-loop path against the current profile bundle
  did not change the previous rejection: `FD_EPOLL=1` short k6 was `0.55ms`;
  a one-receiver/one-worker variant only matched `0.54ms`.
- 2026-05-28 follow-up after WSL/Docker idle: protected control smoke matched
  the floor at `p99=0.53ms`, score `6000`, zero errors. Combining
  `FD_EPOLL=1`, `FD_RECEIVERS=1`, `LB_FDPASS_NONBLOCK=1`, and
  `TCP_QUICKACK=1` also only reached `0.53ms`. An external-repo-inspired
  isolated cpuset/resource layout (`lb=2,3`, `api1=0`, `api2=1`,
  `lb/api=0.10/0.45`) produced timeouts (`244` HTTP errors,
  `p99=2001ms`) and is rejected. A `.NET` `FD_EPOLL_GREEDY_READ` experiment,
  which tried an immediate read after receiving the passed fd, stayed correct
  but worsened short k6 to `p99=0.54ms`; the code was removed. A follow-up
  `FD_EPOLL_DEFER_ADD` variant, which delayed epoll registration and handled
  the received fd immediately, also stayed correct but only reached
  `p99=0.54ms` in short k6; combining it with `LB_FDPASS_NONBLOCK=1` and
  `TCP_QUICKACK=1` remained at `0.54ms`, so it was removed too. Adding
  API-side edge-triggered epoll (`FD_EPOLL_ET=1`) reached `0.52ms` in one
  short smoke, but the full k6 regressed to `p99=0.54ms`; combining it with
  LB `EPOLL_ET=1` worsened smoke to `0.54ms`, and leaving default receiver
  count only reached `0.53ms`. The flag was removed. Replacing the fdpass LB
  listener `epoll_wait` loop with a poll-based accept loop inspired by C++ LBs
  stayed correct but only reached `p99=0.53ms` in smoke; it was removed.
  After fixing the local runner so `LB_PRECONNECT_CONTROL` is emitted when used
  alone, disabling it (`LB_PRECONNECT_CONTROL=0`) worsened smoke to
  `p99=0.54ms`; keep the default `1`. Testing the top `.NET` cpuset shape
  alone (`api1=0`, `api2=1`, `lb=2,3`) while keeping current CPU budgets
  worsened smoke to `p99=0.55ms`; keep the current local cpusets. ASM-inspired
  listener `TCP_FASTOPEN=1` did not help because k6 does not appear to benefit
  from TFO here; smoke was `p99=0.54ms`, and the flag was removed. Rechecking
  the top `.NET` LB event size with `MAX_EVENTS=256` matched only
  `p99=0.53ms` in protected smoke. Adding a temporary fdpass accept burst limit
  of `64`, as used by several external LBs, worsened protected smoke to
  `p99=0.55ms`; the code was removed. A table-based `.NET` fd epoll variant,
  using the numeric fd as the epoll token instead of allocating a `GCHandle`
  per client, reached `p99=0.52ms` in short smoke but regressed to
  `p99=0.56ms` in the full 2-minute k6. Adding `TCP_QUICKACK=1` to that
  variant also worsened smoke to `p99=0.56ms`; the code was removed.
- A full 2-minute k6 recheck of the best protected bundle, after reverting the
  rejected experiments, returned `p99=0.55ms`, score `6000`, zero FP/FN/HTTP
  errors. This confirms the current complete-k6 floor is still above the
  `0.52ms` target.
- Later on 2026-05-28, the refreshed preview ranking still had the same
  applicable shape: top `.NET` was `fksegundo/rinha-dotnet` at remote
  `p99=0.43ms`, with `vinicius-piassa` ASM and `dalvorsn`/`fksegundo` native
  entries ahead. A fresh full protected recheck of the current publishable
  bundle, including the default native flags
  `-DJSON_FIXED_NUMBERS=1 -DKD_BEST_FIRST=1`, returned `p99=0.54ms`, score
  `6000`, zero FP/FN/HTTP errors. Two quick local probes also failed to
  improve the floor: `FD_RAW=0` stayed at `p99=0.54ms`, and
  `LB_CPU=0.13/API_CPU=0.435` worsened to `p99=0.55ms`.
- Additional top-`.NET` inspired probes were rejected. Limiting the managed
  fd-epoll control drain to `FD_EPOLL_RECV_BUDGET=32` stayed correct but
  worsened short k6 to `p99=0.55ms`; the temporary code was removed.
  Installing `libmimalloc2.0` and setting
  `LD_PRELOAD=/usr/lib/x86_64-linux-gnu/libmimalloc.so.2`, as a local analogue
  to the top `.NET` image, stayed at `p99=0.54ms` in short k6 and was removed.
  Upgrading to `.NET 11 preview` was not pursued because the host currently has
  only SDK `10.0.101`, and that would break the documented local commands.
- The missing signal from the top `.NET` event-loop was not the recv-fd budget,
  but the short epoll timeout. Adding `FD_EPOLL_TIMEOUT_MS` to the managed fd
  epoll loop and running with `FD_EPOLL=1 FD_EPOLL_TIMEOUT_MS=1` produced a
  protected short smoke of `p99=0.45ms`, score `6000`, zero errors. Two full
  2-minute k6 runs then sustained the result: `p99=0.47ms` and `p99=0.46ms`,
  both with `final_score=6000`, zero FP/FN/HTTP errors. The command included
  both `PROFILE_FASTPATH_REFERENCE_SHA256` and
  `EXPECTED_REFERENCES_GZIP_SHA256` for the current references hash
  `43d10de80609e77ce25740f375607afce7561ec44da50c27c142493db8fcab67`.

## Remote History

- Previous official preview data: a `.NET` API version with native KD-tree
  search reached remote `final_score=6000`, `p99=0.96ms`, zero errors.
- After the data changed, the same class of profile shortcuts was no longer
  safe. A safe `.NET` submission with profile fast path off reached
  `p99=1.37ms`, `final_score=5864.77`, zero errors.
- A later `.NET` candidate improved remote to about `p99=1.12ms`,
  `final_score=5949.01`, zero errors.
- Best current remote result: issue
  `https://github.com/zanfranceschi/rinha-de-backend-2026/issues/7077`,
  submission commit `89244ac`, image
  `ghcr.io/daniloitagyba/rinha-2026-dotnet-tcp:8fd4821774e3cd7adde699547dd6663ea4883d99`,
  `p99=0.98ms`, `final_score=6000`, zero errors.
  The first attempt with the same image was rejected because remote submission
  does not allow `security_opt: seccomp=unconfined` or service `sysctls`; keep
  those options out of the `submission` branch.
- Later final-prep attempt, issue
  `https://github.com/zanfranceschi/rinha-de-backend-2026/issues/7092`,
  image
  `ghcr.io/daniloitagyba/rinha-2026-dotnet-tcp:208d0da52aa614618d3c0614f80c5e09bacb4187`,
  returned `p99=1.12ms`, `final_score=5950.75`, zero errors. Because it was
  worse than issue `#7077`, `submission` was rolled back to image
  `8fd4821774e3cd7adde699547dd6663ea4883d99` at commit `48379b5`.
- Best-local submission attempt, issue
  `https://github.com/zanfranceschi/rinha-de-backend-2026/issues/7126`,
  reused image
  `ghcr.io/daniloitagyba/rinha-2026-dotnet-tcp:208d0da52aa614618d3c0614f80c5e09bacb4187`
  but pinned the validated local runtime config in `submission`: profile fast
  path on, `ASSUME_* = 0`, no `NATIVE_ANN`, and explicit
  `CLASSIFIER_PREWARM=64`/`TP_PREWARM=64`. Result: `p99=0.99ms`,
  `final_score=6000`, zero FP/FN/HTTP errors. `submission` commit:
  `6ddaa74`.
- Reference-gated submission attempt, issue
  `https://github.com/zanfranceschi/rinha-de-backend-2026/issues/7169`,
  image
  `ghcr.io/daniloitagyba/rinha-2026-dotnet-tcp:361f6196c117f7203ea1c8e0494ae3e5056b84be`,
  `submission` commit `742b11b`, added
  `PROFILE_FASTPATH_REFERENCE_SHA256` and `EXPECTED_REFERENCES_GZIP_SHA256`
  for the current references hash
  `43d10de80609e77ce25740f375607afce7561ec44da50c27c142493db8fcab67`.
  Result: `p99=0.99ms` (`raw p99_ms=0.99400351`), `final_score=6000`,
  zero FP/FN/HTTP errors. No rollback was needed.
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
TP_MIN_IO_THREADS=4 \
sh scripts/k6-local.sh
```

Result with persisted `lb.sysctls.net.core.somaxconn=65535`:
`p99=0.53ms`, `final_score=6000`, zero errors.

Variant: adding `PIN_FIRST_CPU=1` produced one complete k6 run at
`p99=0.53ms`, `final_score=6000`, zero errors. Treat it as a local-only
experimental signal because it did not reach `0.52ms`, depends on runner CPU
layout, and a later complete recheck regressed to `p99=0.55ms`.
After waiting for Docker/BuildKit startup CPU to settle, the clean baseline
still only reached `p99=0.53ms` in short k6; the same stable window with
`PIN_FIRST_CPU=1` worsened to `0.56ms`.

Latest clean WSL rechecks, after resyncing a stale temporary `MaxRequestBytes =
2 KiB` copy back to the repo default `8 KiB`, stayed in the same range:
complete k6 produced `p99=0.55ms`, then a final source-clean full recheck
produced `p99=0.54ms`, `final_score=6000`, zero errors.
After the later timeout/write/header/prebuffer/KD/topology/CPU experiments were
removed, another source-clean full k6 recheck again produced `p99=0.54ms`,
`final_score=6000`, zero errors.
A later clean full recheck after the ASM-inspired experiments were removed
returned `p99=0.56ms`, `final_score=6000`, zero errors, reinforcing that the
current complete-k6 floor is a `0.54ms-0.56ms` band rather than a sustained
`0.50ms` candidate.
After stopping two stale project-local `rinha-fraud eval` containers that had
been running for hours, the same best bundle still produced complete k6
`p99=0.54ms`, `final_score=6000`, zero errors. The stale containers were not
the source of the remaining p99 floor.
After the later LB socket-inheritance, control-nonblocking, and cpuset
experiments were removed, a fresh complete k6 recheck of the best bundle
returned `p99=0.53ms`, `final_score=6000`, zero errors. The `0.50ms` target
remains unproven.
On 2026-05-28, another clean WSL full recheck of the same best bundle returned
`p99=0.54ms`, `final_score=6000`, zero errors. Treat this as confirmation that
the current local floor remains around `0.53ms-0.54ms`, not as evidence that
`0.50ms` has been reached.
The pre-publication WSL gate for the same publishable bundle returned
`p99=0.55ms`, `final_score=6000`, zero errors; this keeps the same practical
floor band and validates correctness for publication, but does not improve the
best observed local p99.

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
- LB `net.core.somaxconn=65535` reduced the persisted full-k6 result to
  `p99=0.53ms` with zero errors.
- `TP_MIN_IO_THREADS=4` is the best current IO-thread floor for the fdpass
  path: it sustained complete k6 at `p99=0.54ms`, zero errors. Lower values
  only matched short tests, while `8` caused complete-k6 timeouts.
- `[SuppressGCTransition]` on native classifier P/Invokes improved eval
  latency and remained correct in k6, but the full-k6 gain is dominated by
  network/transport.
- `PROFILE_FASTPATH=0` as the safe default after reference changes.
- Automated reference refresh workflow that rebuilds the index and validates
  before publishing a candidate image.
- Embedded reference fingerprints in `references.idx`, plus runtime guards
  (`EXPECTED_REFERENCES_*` and `PROFILE_FASTPATH_REFERENCE_SHA256`) so
  dataset-sensitive fast paths only run against an explicitly validated
  reference set.

## External Repo Findings

- `dalvorsn/cpp-rinha-backend-2026` at `06df468` (checked 2026-05-28):
  submission branch has only `docker-compose.yml`, `info.json`, and `LICENSE`,
  with 1 LB + 2 APIs, total CPU `1.00` and total memory `270MB`. No obvious
  submission-rule violation was found from the tree, but the compose uses the
  mutable image tag `dalvorsn/rinha-backend-2026:main`; keep using immutable
  SHA tags in this project.
- Useful ideas from that repo: IVF index built at image-build time, int16
  quantization, AVX2 pair-SoA block scan over 8 vectors, SIMD centroid scan,
  bbox lower-bound repair pass for ambiguous top-5 results, pre-rendered HTTP
  responses, `mlockall(MCL_CURRENT)`, epoll busy-poll knobs, tmpfs Unix control
  sockets, and a minimal APM mode with internal latency histograms.
- What applies here: the IVF/repair design is the only materially new
  algorithmic direction worth testing against the native KD-tree. Implement it,
  if tested, inside `librinha_native.so` and keep `api1/api2` entrypoint as
  `["rinha-fraud", "serve"]`. Do not replace the `.NET` APIs with the C++
  server. Transport ideas from the repo are mostly already present or already
  rejected locally: fd handoff, pre-rendered responses, cpuset/split variants,
  busy poll, `TCP_QUICKACK`, control preconnect, socket buffers, and
  preinitialized `sendmsg` state.
- Caution: their IVF is approximate and relies on a repair condition
  (`cnt in [1,4]`) to recover recall. Before any adoption, gate with native
  eval on the current references and changed-payload k6; do not combine it with
  profile fast paths until it has zero FP/FN by itself.
- Applied/tested the closest existing analogue here, `NATIVE_ANN=1` bucket-IVF
  with exact repair/fallback for ambiguous `1..4` fraud-neighbor counts. It is
  not viable: default candidate counts still produced `56 FP` and `6 FN`; even
  raising candidates to `72000` still had `6 FP` and `1 FN`. Adding a temporary
  strong-decision distance guard only reached zero errors by forcing exact
  fallback on almost every request (`ANN_STRONG_MAX_DISTANCE=0`), with eval
  classifier `p99=9281404ns`. The temporary guard code was removed. Do not run
  k6 or remote for this path.

- `fksegundo/rinha-dotnet` at `c1f5342` (checked 2026-05-28) is a useful
  `.NET`-API comparison because the preview result showed score `6000` with
  p99 about `0.43ms`. Architecture: Native AOT `.NET` APIs, Rust fd-passing LB,
  epoll/event-loop API runtime, mmap/pretouch/mlock index, and an `RNSPCST2`
  KD-style index with AVX2 leaf/block scans.
- No payload-lookup pattern was found in the inspected source. Do not copy the
  submission posture directly: the submission compose uses mutable `latest` for
  the API image, `info.json` points at a different source repo, and the
  submitted API socket paths are not a clean topology template. Keep this repo
  on immutable SHA tags and the explicit `.NET` API entrypoints.
- Applicable lessons from that repo are mostly already covered here: fd passing,
  preconnected control sockets, event-loop fd handling, TCP quickack/nodelay,
  tmpfs sockets, no Docker logging, cpusets, Native AOT, pre-rendered HTTP
  responses, mmap/pretouch, and AVX2 distance search. Local rechecks did not
  produce a better candidate.
- The only materially new direction is algorithmic, not a small config change:
  a KD leaf layout that scans 8 vectors as a SIMD block, similar to their
  `RNSPCST2` layout. Applying it here would require a new KD index section or
  format version plus native search changes; it should be treated as a separate
  post-final experiment unless the current final candidate is abandoned.

- `vinicius-piassa/rinha-backend-2026-asm` at `34ea36b` (rechecked
  2026-05-28): runtime compose uses
  a public `linux/amd64` image, default bridge networking, 1 LB + 2 APIs,
  `1.00` CPU and `350MB` total. The LB path appears to only pass accepted fds
  to APIs; fraud logic lives in `asm_server`.
- Rule status / blacklist: do not use this repo as a rule-clean source.
  Official docs say
  `submission` must contain only the files needed to run the test and no source
  code; this repo has `origin/submission == origin/main` and the submission tree
  includes the full `asm/` source. The published source is also not
  reproducible as-is because `Dockerfile` copies `index_p0.bin`..`index_p3.bin`
  while the repo only contains `index.bin`.
- The ASM server has heuristic "obvious legit/fraud" short-circuit rules before
  k-NN. This is useful as a caution, not as a safe default here: local/profile
  shortcuts already caused remote FP/FN after reference/test data changed.
- The ASM LB also contains synthetic self-warm traffic. Do not copy this into
  the `.NET` line: external warmup was already tested here and did not improve
  complete k6, and adding extra request injection increases rule-review risk.
- Useful ASM ideas already present here: fd handoff, pre-rendered responses,
  preprocessed binary index, SIMD/AVX2 native search, mmap/prefault, and
  partitioned KD-tree search. Their 4-way tag partition is weaker than the
  current 256-partition KD-tree key.
- Ideas already tested locally from that repo did not beat the current best:
  `MCC_U32_LOOKUP=1`, ASM-style CPU split `lb=0.05/api=0.475`,
  `THREAD_PRIORITY_HIGH=1`, and `KD_HOIST_QUERY=1` all stayed at or worsened
  the `0.54ms-0.55ms` band. The `lb=0.05 CPU/8MB` memory layout worsened to
  `0.57ms`.

## What Not To Repeat Without New Evidence

Transport and LB:

- `FD_EPOLL=1`: correct, but complete k6 stayed around `0.57ms-0.59ms`.
  A later clean recheck with the current bundle reached only `p99=0.56ms`.
- `FD_CONTROL_SEQPACKET=1` and `FD_CONTROL_PREBUFFER=1`: correct after the
  `send_fd` fix, but did not beat the baseline.
- `FD_CONTROL_SEQPACKET=1` without prebuffer also worsened current short k6 to
  `p99=0.57ms`; keep the default stream control socket.
- `FD_CONTROL_CONNECTIONS=2`: multiple fdpass control channels were correct,
  but complete k6 stayed at `p99=0.54ms`; implementation was removed.
- `FD_CONTROL_DRAIN=1`: draining additional received fds from the API control
  socket with nonblocking `recvmsg` after each blocking receive worsened short
  k6 to `p99=0.57ms`; experiment was removed before trying larger drain
  counts.
- `LB_PRECONNECT=1`: eagerly connecting all fdpass control sockets before the
  LB event loop was correct, but worsened short k6 to `p99=0.57ms`; keep lazy
  control connection setup.
- duplicating LB `UPSTREAMS` to open 2/4 control connections per API with
  matching `FD_RECEIVERS` worsened short k6 to `p99=0.59ms/0.56ms`.
- Weighted `UPSTREAMS` did not produce a complete-k6 candidate. A 1:2 split
  favoring `api2` reached `p99=0.52ms` in short k6, but complete k6 regressed
  to `p99=0.55ms`. Favoring `api1` stayed at `0.55ms`; `api2` 1:2 with
  `PIN_FIRST_CPU=1` stayed at `0.53ms`; `api2` 1:3 stayed at `0.55ms`.
  Combining 1:2 `api2` weighting with asymmetric CPU (`api1/api2=0.36/0.52`
  or `0.32/0.56`) introduced one HTTP error and stayed at `0.54ms`; with
  `lb=0.10/api=0.45` it worsened to `0.58ms` with one HTTP error. Harness
  support for these temporary knobs was removed.
- forcing `UPSTREAMS` to only `/sockets/api1.sock.ctrl` overloaded one API and
  produced `255` HTTP timeouts with `p99=2001.12ms`. Keep both APIs active.
- Retesting one active API with more CPU (`lb=0.12`, `api1=0.84`, `api2=0.04`,
  `TP_MIN_THREADS/TP_PREWARM=128`) avoided errors and reached `p99=0.52ms` in
  short k6, but complete k6 regressed to `0.54ms`. It also has higher remote
  rule/interpretation risk, so it was not promoted.
- `FD_PRE_READ=1`: sometimes matched the floor, did not improve complete k6.
  In the latest clean smoke matrix it worsened to `p99=0.55ms`.
- `LB_ACCEPT_CLOEXEC=0`, removing `SOCK_CLOEXEC` from LB `accept4` in fdpass
  mode, stayed at `p99=0.54ms` in short k6 with zero errors; adding
  `PIN_FIRST_CPU=1` also stayed at `0.54ms`. Experiment was removed.
- `FD_SOCKET_ASYNC`: smoke worsened to about `0.71ms`; removed.
- `FD_DEDICATED_THREADS=1`: correct, no sustained gain.
  A later recheck with the current bundle also lost to ThreadPool:
  `FD_THREAD_STACK_KB=128/256/512` produced short k6
  `0.55ms/0.60ms/0.58ms`; `FD_RECEIVERS=1` plus dedicated threads was
  `0.60ms`.
- `LB_FAST2=1`: no gain and can create errors in combinations.
- `EPOLL_ET=1`: smoke looked good, complete k6 only tied around `0.56ms`.
- `TCP_QUICKACK=1`: no stable gain.
- `LB_TCP_QUICKACK=1`: short k6 reached `p99=0.51ms`, but complete k6
  worsened to `p99=0.55ms`; combining with API `TCP_QUICKACK=1` or
  `PIN_FIRST_CPU=1` removed the short-run signal. Experiment was removed.
  Re-adding it temporarily with `KD_NODE_QUEUE_SIZE=640` only reached
  `p99=0.53ms` in short k6, so it was removed again.
- `SO_INCOMING_CPU` on the LB listener, tested as `LB_INCOMING_CPU=0` and
  `LB_INCOMING_CPU=2` to match the current LB cpuset, stayed at `p99=0.55ms`
  in short k6 with zero errors. Experiment was removed.
- `FD_RECV_SPIN=4`: short k6 reached `p99=0.51ms`, but complete k6 stayed at
  `p99=0.54ms`; `FD_RECV_SPIN=8/16` worsened short k6 to `0.54ms-0.55ms`.
  Experiment was removed.
- A later clean implementation of smaller `FD_RECV_SPIN=1/2/3/4` did not
  produce a candidate: short k6 stayed at `0.55ms/0.56ms/0.57ms/0.54ms`.
  Experiment was removed again.
- `FD_BUSY_POLL_US=50`: short k6 reached `p99=0.51ms`, but complete k6
  worsened to `p99=0.55ms`. Experiment was removed.
- Reimplementing busy poll on both the accepted fd in the LB and the received fd
  in the `.NET` API showed it is not useful here: `FD_BUSY_POLL_US=5/10/20/50`
  worsened short k6 to `p99=0.63ms-0.64ms`. Experiment was removed again.
- refactoring the LB fdpass accept path to bypass prebuffer logic when disabled
  reached `p99=0.52ms` in short k6, but complete k6 worsened to `p99=0.55ms`;
  experiment was removed.
- `TCP_DEFER_ACCEPT=1`: smoke worsened to about `0.60ms`.
- `LB_LISTEN_BACKLOG=1024` improved one short k6 to `p99=0.53ms`, while
  `512` stayed at `0.54ms` and `2048` stayed at `0.55ms`, but the complete k6
  recheck with `1024` regressed to `p99=0.55ms`, zero errors. Experiment was
  removed.
- socket buffer sweeps and `LB_SOCKET_BUFFERS=0`: no stable gain.
- later socket buffer recheck after bridge options also did not produce a
  candidate: `SOCKET_BUFFER_SIZE=4096/8192/32768` all stayed at `p99=0.54ms`
  in short k6.
- Splitting LB accepted-socket buffer setup into receive-only/send-only toggles
  did not help: disabling only `SO_RCVBUF` produced short k6 `p99=0.56ms`, and
  disabling only `SO_SNDBUF` worsened to `p99=0.63ms`; experiment was removed.
- Lower socket buffers also did not sustain: `SOCKET_BUFFER_SIZE=512` matched
  `p99=0.52ms` in short k6, but complete k6 regressed to `p99=0.55ms`;
  `1024/2048` were worse in short k6. Combining `512` with `PIN_FIRST_CPU=1`,
  `LB_CPU=0.16/API_CPU=0.42`, both together, or `FD_RECEIVERS=4` stayed at
  `p99=0.54ms-0.55ms` in short k6.
- raw fd `SO_RCVTIMEO/SO_SNDTIMEO` with `FD_SOCKET_TIMEOUT_MS=50/100/250/500/1000`
  did not help: short k6 ranged from `p99=0.55ms` to `0.60ms`, and
  `250/500ms` introduced HTTP errors. Experiment was removed.
- lowering `LB_TCP_NODELAY`: worsened.
- `LB_REUSEPORT=1` on the single LB listener stayed correct but only reached
  `p99=0.53ms` in short k6; not enough for a complete-k6 candidate.
- `TCP_NOTSENT_LOWAT=1/16/128/1024` on accepted client fds did not help short
  k6: best values only matched `p99=0.54ms`, while `16/1024` worsened to
  `0.56ms/0.55ms`. Experiment was removed.
- LB accepted-socket priority/TOS did not produce a complete-k6 candidate:
  `LB_SOCKET_PRIORITY=6 + LB_IP_TOS=16` stayed at `p99=0.54ms` in short k6,
  and `LB_SOCKET_PRIORITY=6` alone reached `0.53ms` in short k6 but regressed
  to `0.56ms` in complete k6. Experiment was removed.
- LB accepted-socket `SO_LINGER` with zero timeout (`LB_SO_LINGER_ZERO=1`) was
  correct but worsened short k6 to `p99=0.55ms`; experiment was removed.
- removing per-request `memset`/manual initialization in the LB `sendmsg`
  fdpass path worsened short k6 to `p99=0.55ms`. Experiment was removed.
- Pre-initializing the LB fdpass `msghdr`/`cmsghdr` and only updating the fd
  (`LB_FDPASS_STATIC_MSG=1`) also stayed at `p99=0.55ms` in short k6 with zero
  errors. Experiment was removed.
- Dedicated socket buffers on the Unix fdpass control channel did not produce a
  complete-k6 candidate: short k6 for `CONTROL_SOCKET_BUFFER_SIZE`
  `1024/4096/16384/65536/262144` ranged from `p99=0.53ms` to `0.55ms`, with
  `16384` only reaching `0.53ms` in short k6. Experiment was removed.
- `[SuppressGCTransition]` on libc `send(2)`: short k6 reached `0.53ms`,
  but full k6 regressed to `0.56ms`; keep it off.
- `[SuppressGCTransition]` on both libc `recv(2)` and `send(2)` also did not
  improve current short k6 (`p99=0.54ms`) and is risky because `recv` can
  block; keep it off.
- Docker network `internal: true`: backend did not become ready through the
  published port; keep it off for the official local/remote path.
- Docker bridge MTU overrides did not help: MTU `9000` worsened short k6 to
  `p99=2.27ms`; MTU `65535` worsened to `0.58ms`.
- `INDEX_HUGEPAGES=0`: smoke stayed at `p99=0.54ms`; combined with
  `PIN_FIRST_CPU=1` reached `0.53ms`, not enough to justify changing the
  default.
- `PREFETCH_INDEX=0`: short k6 worsened to `p99=0.56ms`; keep prefaulting the
  index before readiness.
- Skipping prefault only for mapped risky fallback sections when
  `KDTREE_NATIVE=1` was correct but not useful: short k6 reached `0.53ms`, then
  complete k6 regressed to `p99=0.55ms`; experiment was removed.
- `LOGGING_NONE=1`: disabling Docker logging worsened short k6 to
  `p99=0.55ms`; keep the default logging path.
- `FD_RECEIVERS=3`: complete k6 worsened to `p99=0.55ms`.
- `FD_RECEIVERS=1`: short k6 stayed at `p99=0.54ms`.
  A later clean smoke recheck had `FD_RECEIVERS=1` at `0.55ms` and
  `FD_RECEIVERS=4` at `0.53ms`, not enough for a complete-k6 candidate.
- bind-mounting `/sockets` from `/tmp` or `/dev/shm`: no complete-k6 gain;
  `/dev/shm` reached `0.53ms` in short k6 but `0.55ms` in complete k6.
- tmpfs socket volume option changes did not produce a candidate:
  `noatime,nosuid,nodev` stayed at `p99=0.54ms`, and `size=1m` reached only
  `0.53ms` in short k6.
- `FD_CONTROL_SEQPACKET=1` with `FD_CONTROL_PREBUFFER=1`: short k6 matched
  the baseline, but complete k6 produced `209` HTTP errors and `p99=0.60ms`.
- `FD_CONTROL_DGRAM=1`: Unix datagram SCM_RIGHTS control channel was made
  functional and kept zero errors, but short k6 worsened to `p99=0.55ms`.
  Experiment was removed.
- stream control prebuffer without `SEQPACKET`: correct and reached
  `p99=0.51ms` in short k6, but complete k6 regressed to `p99=0.55ms`;
  experiment was removed.
- Rechecking stream control prebuffer on the current bundle with prebuffer
  capacities `8192/2048/1024/512` did not produce a candidate; short k6 stayed
  at `p99=0.55ms-0.56ms`, zero errors.
- Reducing `FDPASS_PREBUFFER_SIZE` at build time while prebuffer stayed disabled
  did not help: `1/64` worsened short k6 to `0.59ms/0.56ms`, and `512` only
  matched `0.54ms`.
- `LB_FDPASS_BLOCKING_ACCEPT=1`: blocking accept loop in the LB worsened short
  k6 to `p99=0.55ms`; experiment was removed.
- `LB_FDPASS_THREADS=2/4`: multiple fdpass accept/epoll loops in the LB were
  correct and `2` reached `p99=0.51ms` in short k6, but complete k6 regressed
  hard to `p99=0.68ms`; experiment was removed.
- fdpass `SCM_RIGHTS` batching was correct but not a candidate:
  `LB_FDPASS_BATCH=2 + FD_BATCH_RECEIVE=1` reached only `p99=0.53ms` in short
  k6, `batch=4` worsened to `0.58ms`, and `batch=2 + PIN_FIRST_CPU=1` worsened
  to `0.57ms`. Experiment was removed.
- `LB_FDPASS_ACCEPT_SPIN=1/4`: doing immediate extra `accept4` drain attempts
  after a listener event did not produce a candidate. `1` tied the short
  baseline at `p99=0.53ms`; `4` worsened to `0.54ms`. Experiment was removed.
- LB `MAX_EVENTS=1` had a promising first signal (`p99=0.52ms` in short k6
  and one complete run at `0.53ms`), but did not sustain after being persisted:
  the source-clean complete recheck regressed to `0.55ms`, and the following
  short recheck was `0.54ms`. `MAX_EVENTS=2/4` were worse in short k6
  (`0.56ms/0.58ms`). A 2026-05-28 recheck on the current WSL bundle reached
  only `p99=0.53ms` in 30s smoke; adding `PIN_FIRST_CPU=1` worsened to
  `0.54ms`. Experiment remains rejected.
- `LB_FDPASS_ACCEPT_LIMIT=1/16`, limiting how many accepted fds the LB drains
  per epoll event, worsened short k6 to `p99=0.55ms` with zero errors.
  Experiment was removed; draining until `EAGAIN` remains the best fdpass
  behavior.
- ASM-inspired `LB_FDPASS_URING=1` was tested as an accept/sendmsg
  `io_uring` LB path while keeping APIs in `.NET`. It stayed correct, but did
  not beat the current full-k6 floor: `p99=0.55ms`, score `6000`, zero errors;
  the same bundle without the experiment also produced `p99=0.55ms`. The code
  was removed instead of adding LB complexity without measurable gain.
- `FD_PRE_READ=1`: short k6 stayed at `p99=0.54ms`.
- `FD_DEDICATED_THREADS=1` with `FD_THREAD_STACK_KB=64`: short k6 worsened to
  `p99=0.58ms`.
- Unix datagram fdpass (`FD_CONTROL_DGRAM=1`) was correct but did not improve:
  short k6 stayed at `p99=0.54ms`; with prebuffer it worsened to `0.55ms`.
- `LB_FDPASS_NONBLOCK=1` was rejected: accepted sockets reached the API as
  nonblocking fds and short k6 produced `5633` HTTP errors.
- `LB_FDPASS_ACCEPT_NONBLOCK=1`, a refined variant that used nonblocking
  `accept4` only to drain the LB queue and restored blocking mode before
  fdpass, stayed correct but did not sustain: short k6 reached `p99=0.53ms`,
  while complete k6 regressed to `p99=0.55ms`. Combining it with restored
  `LB_TCP_QUICKACK=1`, `PIN_FIRST_CPU=1`, or `lb=0.16/api=0.42` stayed at
  `p99=0.53ms-0.55ms` in short k6. Experiment was removed.
- `SO_REUSEPORT=1` with `api1/api2` listening directly in the `lb` network
  namespace preserved the `.NET` API entrypoints and produced zero errors, but
  complete k6 worsened to `p99=0.66ms`; experiment was removed.
- A temporary 3-API `.NET` topology inside the same resource budget
  (`lb=0.12 CPU/32MB`, each API `0.293 CPU/106MB`) stayed correct but worsened
  short k6 to `p99=0.58ms`; do not pursue more API instances without a new
  scheduling signal.
- C LB proxy mode from TCP to Unix sockets, with APIs still using
  `entrypoint: ["rinha-fraud", "serve"]`, is not competitive with fdpass:
  short k6 worsened to `p99=5.62ms`, zero errors.
- `PIN_FIRST_CPU=1`: one complete k6 run improved to `p99=0.53ms`, but it did
  not reach `0.52ms`; combining with `LB_CPU=0.14/API_CPU=0.43` looked good in
  short k6 (`0.51ms`) and regressed to `0.55ms` in complete k6. Treat as a
  local-only signal, not a submission default.
  A later full recheck with the clean WSL copy reached `p99=0.54ms`.
- Implementing `PIN_FIRST_CPU=1` inside the `.NET` API itself, before
  ThreadPool startup, is not viable: short k6 produced `238` HTTP timeouts and
  `p99=2001.05ms`. The code was removed.
- `LB_CPUSET=1/3` with `PIN_FIRST_CPU=1`: after removing CRLF from the WSL
  command, cpuset validation was fine, but there was no complete-k6 gain;
  `LB_CPUSET=3` reached `p99=0.52ms` in short k6 and regressed to `0.55ms` in
  complete k6.
- clean CPU-isolation sweeps with `PIN_FIRST_CPU=1` did not produce a
  candidate: `LB_CPUSET=3/API1_CPUSET=1/API2_CPUSET=2` reached only
  `p99=0.53ms` in short k6, while broader variants worsened to
  `0.57ms-0.61ms`.
  A later explicit pin/cpuset matrix (`api1/api2` isolated on `1/3`, `1/2`,
  `0/2`, `0/3` with LB isolated on another CPU) stayed at `p99=0.54ms-0.55ms`.
- Rechecking the earlier broad LB/alternate API cpuset signal after Docker
  startup stabilized did not reproduce the old `0.51ms` smoke result:
  `LB_CPUSET=0,1,2,3`, `API1_CPUSET=0,2`, `API2_CPUSET=1,3` worsened to
  `p99=0.56ms`, zero errors.
- removing service `cpuset` through a local compose override worsened short k6
  to `p99=0.59ms`; keep the current cpuset layout.

Runtime:

- `TP_MIN_THREADS=32`: caused major cauda and errors.
- `TP_MIN_THREADS=96/128/192+`: no stable gain.
- `TP_PREFER_LOCAL=1`: complete k6 worsened to `p99=0.55ms`.
- `DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=64`: smoke looked better, but
  complete k6 worsened to `p99=0.55ms`.
- `DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=0`: short k6 reached `p99=0.53ms`,
  matching the historical floor, but complete k6 worsened to `p99=0.55ms`.
- `DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=256`: short k6 stayed at
  `p99=0.54ms`.
- `DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=6/8/10/12/16`: latest smoke
  recheck found one `8` run at `p99=0.52ms`, but complete k6 with `8`
  returned to `p99=0.55ms`; combinations with `PIN_FIRST_CPU`,
  `FD_RECEIVERS=4` and cpuset variants stayed at `0.53ms-0.54ms` in smoke.
- `TP_PREFER_LOCAL=1`: no gain. Smoke was `p99=0.55ms` alone and `0.53ms`
  only when combined with already-rejected pin/receiver variants.
- `TP_MIN_THREADS=48` with matching `TP_PREWARM=48` worsened short k6 to
  `p99=0.55ms`.
- Lowering `TP_MIN_THREADS` below the current floor is not viable:
  `16/24/32` caused many HTTP timeouts, `40` still produced `33` HTTP errors,
  and `64` without prewarm worsened to `p99=0.58ms`.
- `TP_PREWARM=0/32/128` on the current bundle did not improve short k6:
  `0` and `32` stayed at `p99=0.55ms`; `128` worsened to `0.57ms`.
- `TP_MIN_THREADS=72/80/88` with matching `TP_PREWARM` did not produce a
  candidate: short k6 stayed between `p99=0.53ms` and `0.55ms`.
- `TP_MIN_THREADS=96/112/128` with matching prewarm did not produce a
  candidate: short k6 stayed at `0.54ms/0.55ms/0.56ms`; `96` combined with
  `TP_MIN_IO_THREADS=8` stayed at `0.54ms`.
- Matching thread prewarm closer to the 100 k6 VUs also worsened:
  `TP_MIN_THREADS/TP_PREWARM=80/88/100` with `TP_MIN_IO_THREADS=4` produced
  short k6 `p99=0.60ms/0.59ms/0.59ms`, zero errors.
- Fine checks around the current `64` worker minimum also did not help:
  `TP_MIN_THREADS/TP_PREWARM=60/60` worsened short k6 to `p99=0.58ms`, and
  `68/68` worsened to `0.55ms`.
- `TP_MIN_THREADS=64`, `TP_MAX_THREADS=128`, `TP_PREWARM=64` reached only
  `p99=0.53ms` in short k6.
- `TP_MIN_IO_THREADS=1/2/4/8`: `4` is the only value promoted to default after
  sustaining complete k6 at `p99=0.54ms`, zero errors. `8` looked best in short
  k6 (`0.51ms`) but failed complete k6 with `p99=0.61ms` and `213` HTTP
  errors/timeouts; `1/2` did not beat the floor.
- Additional explicit IO-thread points also failed to improve the floor:
  `TP_MIN_IO_THREADS=5/6` produced short k6 `p99=0.54ms/0.56ms`, and
  `TP_MIN_IO_THREADS=7` regressed to `p99=0.55ms` in complete k6. A
  2026-05-28 smoke recheck filled the remaining gap: `TP_MIN_IO_THREADS=3`
  only matched `p99=0.53ms`, while `0` worsened to `0.54ms`. Keep `4`.
- `TP_MIN_IO_THREADS=4` plus
  `DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=1/16/32/128` did not produce a
  complete-k6 candidate; `32` reached `0.53ms` in short k6 and regressed to
  `0.55ms` complete.
- `TP_MAX_THREADS=64`/`TP_MAX_IO_THREADS=64`: short k6 worsened to
  `p99=0.56ms`.
- `TP_MAX_THREADS=96`/`TP_MAX_IO_THREADS=96` with the current `64/4` floor
  stayed at `p99=0.54ms` in 30s smoke; no reason to cap the ThreadPool there.
- Raising managed thread priority with `THREAD_PRIORITY_HIGH=1` stayed correct
  but did not improve the official local smoke path: `p99=0.54ms`,
  `final_score=6000`, zero errors. Experiment was removed.
- `MALLOC_ARENA_MAX=1`: short k6 stayed at `p99=0.53ms`, not enough to promote.
- CFLAGS recheck did not produce a complete-k6 candidate:
  `-fno-plt -fno-semantic-interposition` tied short k6 at `p99=0.53ms`;
  `-flto` reached `0.52ms` in short k6 but complete k6 regressed to
  `p99=0.55ms`; `-flto + PIN_FIRST_CPU=1` worsened short k6 to `0.54ms`.
- ASM-inspired `TIMER_SLACK_NS=1`, `INDEX_MLOCK=1`, and the combined
  `TIMER_SLACK_NS=1 + INDEX_MLOCK=1` smoke tests all stayed at `p99=0.54ms`,
  zero errors. The default-off code experiment was removed because it did not
  produce a complete-k6 candidate and `mlock` would add submission-rule risk.
- Rechecking `TIMER_SLACK_NS=1` through `PR_SET_TIMERSLACK` on both LB and APIs
  did not improve the current bundle: short k6 was `p99=0.57ms` with the
  toggle versus `0.56ms` on the same code without it, both zero errors. The
  code was removed.
- Moving accepted-socket `TCP_NODELAY`/buffer configuration to the LB listener
  and skipping per-accept `setsockopt` did not produce a candidate:
  `LB_INHERIT_SOCKET_OPTIONS=1` reached only `p99=0.53ms` in short k6, while
  nodelay-only inheritance and inherited `SOCKET_BUFFER_SIZE=512` both stayed
  at `0.54ms`. The experiment was removed.
- Making the LB fdpass Unix control sockets nonblocking after `connect()`
  (`FD_CONTROL_NONBLOCK=1`) stayed correct but only reached `p99=0.54ms` in
  short k6. The experiment was removed.
- `WORKERS=1` with `FD_RECEIVERS=1`: short k6 worsened to `p99=0.58ms`.
- `DOTNET_PROCESSOR_COUNT=1/2/4`: no gain.
- Later `DOTNET_PROCESSOR_COUNT=3/4` smoke rechecks stayed at `0.53ms-0.54ms`.
- Real pass-through tests for `.NET` GC knobs did not produce a candidate:
  `DOTNET_gcServer=0/1` stayed at `0.55ms/0.54ms`, `DOTNET_GCRetainVM=1`
  worsened to `0.58ms`, `DOTNET_GCDynamicAdaptationMode=0` stayed at
  `0.55ms`, and `GCRetainVM=1 + GCDynamicAdaptationMode=0` reached only
  `0.53ms` in smoke.
- An explicit `GC.TryStartNoGCRegion` startup toggle was tested with
  `8/16/32MB`; best short k6 was only `p99=0.53ms` at `32MB`, so the
  experiment was removed.
- `GC_LATENCY_MODE=sustained-low-latency`, `DOTNET_GCHeapCount`,
  `DOTNET_GCConserveMemory`: no gain.
- cpuset changes and CPU splits around `0.08/0.46`, `0.10/0.45`,
  `0.14/0.43`: no stable complete-k6 gain.
- isolating app cpusets to leave a core for k6 (`lb=0`, `api1=1`,
  `api2=2`) did not improve smoke.
- Rechecking the same app cpuset isolation in complete k6 worsened to
  `p99=0.59ms`.
- moving `api1/api2` to `network_mode: none` was correct but complete k6
  stayed at `p99=0.54ms`.
- CPU split recheck after bridge opts did not produce a candidate:
  `lb=0.10/api=0.45` only matched short k6 at `0.53ms`, while
  `lb=0.08/api=0.46` and `lb=0.14/api=0.43` stayed around `0.54ms`.
- ASM-style split `lb=0.05/api=0.475` on the current bundle stayed at
  `p99=0.54ms` in short k6, score `6000`, zero errors; not a candidate.
- Asymmetric API CPU splits with `lb=0.12` did not help short k6:
  `api1/api2=0.50/0.38`, `0.48/0.40`, `0.46/0.42`, and `0.42/0.46`
  stayed at `p99=0.55ms-0.59ms`.
- aggressive CPU split `lb=0.04/api=0.48` stayed at `p99=0.54ms` in short k6.
- replacing NanoCpus with `cpu_period=10000`/matching `cpu_quota` worsened short
  k6 to `p99=0.55ms`.
- Testing longer CFS quota periods through compose override was blocked by
  Docker's conflict between `NanoCpus` from `deploy.resources.limits.cpus` and
  explicit `cpu_period/cpu_quota`. Do not pursue this without replacing the
  resource declaration in a full temporary compose file.
- A full temporary compose replacing `deploy.resources.limits.cpus` with
  `cpu_period=1000000` and equivalent `cpu_quota` built and started containers,
  but the stack never became ready through `/ready`; no k6 result was produced.
  Treat this resource model as incompatible with the current local setup.
- Trying to update running containers to `cpu_period=1000000`/matching quotas
  also hit Docker's `NanoCPUs` conflict. The manual run remained at
  `p99=0.54ms`, so there was no evidence to replace the resource model.
- Docker `cpu_shares` did not produce a local candidate without changing CPU
  limits: all services at `2048` reached only `p99=0.53ms` in short k6,
  `lb=4096/api=1024` worsened to `0.56ms`, and `lb=1024/api=4096` stayed at
  `0.54ms`. Temporary overrides were removed.
- LB-only `net.core.somaxconn=65535` is the best current transport-side
  signal: after WSL recovered from a full-disk failure, baseline short k6 was
  `p99=0.55ms`; adding only `somaxconn=65535` reached `p99=0.52ms` in short
  k6 and `p99=0.53ms` in complete k6, score `6000`, zero errors. It was
  promoted to `docker-compose.yml` and revalidated from the persisted worktree
  with complete k6 at `p99=0.53ms`, score `6000`, zero errors. A broader TCP
  sysctl pack
  (`tcp_syncookies=0`, `tcp_timestamps=0`, `tcp_sack=0`, `tcp_fin_timeout=5`)
  worsened short k6 to `p99=0.56ms` and was not kept.
  Additional isolated sysctls on top of `somaxconn` did not help:
  `tcp_max_syn_backlog=65535` worsened short k6 to `0.56ms`,
  `tcp_tw_reuse=1` worsened to `0.59ms`, `tcp_syncookies=0` worsened to
  `0.55ms`, `tcp_timestamps=0` only matched `0.53ms`, and
  `tcp_sack=0` / `tcp_fin_timeout=5` stayed at `0.54ms`.
- Increasing the LB `listen()` backlog after adding `somaxconn=65535` did not
  help: temporary `LB_LISTEN_BACKLOG=8192` and `65535` both stayed at
  `p99=0.54ms` in short k6. The temporary env support was removed.
- Adding the same `net.core.somaxconn=65535` sysctl to `api1/api2` gave one
  short-k6 signal at `p99=0.51ms`, score `6000`, zero errors, but complete k6
  regressed to `p99=0.54ms`. Combining it with `FD_RECEIVERS=4` produced only
  `p99=0.53ms` in short k6, and `TP_MIN_IO_THREADS=8` worsened short k6 to
  `p99=0.56ms`. Keep `somaxconn` only on the LB.
- Combining API `net.core.somaxconn=65535` with LB `MAX_EVENTS=1` on the current
  best bundle also did not produce a target candidate: short k6 reached
  `p99=0.54ms`, score `6000`, zero errors.
- later CPU split recheck with more LB budget also did not produce a target
  candidate: `lb=0.16/api=0.42` and `lb=0.18/api=0.41` reached `0.53ms` in
  short k6, but `lb=0.16/api=0.42` complete k6 stayed at `p99=0.54ms`;
  `lb=0.20/api=0.40` worsened short k6 to `0.54ms`.
- `lb=0.16/api=0.42` combined with `LB_FAST2=1` worsened short k6 to
  `p99=0.55ms`.
- broad cpusets (`LB_CPUSET=0,1,2,3` and `API_CPUSET=0,1,2,3`) worsened short
  k6 to `p99=0.55ms`.
- rechecking broad/alternate cpusets with `TP_MIN_IO_THREADS=4` did not
  produce a complete-k6 candidate. `API1_CPUSET=0,2`,
  `API2_CPUSET=1,3`, `LB_CPUSET=0,1,2,3` reached `0.51ms` in short k6 but
  regressed to `0.55ms` complete.
- Additional LB cpuset checks without `PIN_FIRST_CPU` did not produce a
  candidate: `LB_CPUSET=1,3` only matched `p99=0.53ms` in short k6,
  `0,3` worsened to `0.55ms`, `1,2` worsened to `0.56ms`,
  `LB_CPUSET=1,3 + LB_TCP_QUICKACK=1` stayed at `0.53ms`,
  `LB_CPUSET=1,3 + FD_RECEIVERS=4` worsened to `0.55ms`, and isolated
  `LB_CPUSET=1,3/API1_CPUSET=0/API2_CPUSET=2` worsened to `0.55ms`.
- increasing API memory to `159MB` each worsened short k6 to `p99=0.55ms`.
- memory-limit sweeps did not produce a candidate: `API_MEMORY=150MB` and
  `LB_MEMORY=24MB/40MB` reached only `p99=0.53ms` in short k6;
  `API_MEMORY=152MB` worsened to `0.56ms`, and `164MB` stayed at `0.54ms`.
- ASM-style resource layout with `lb=0.05 CPU/8MB` and
  `api=0.475 CPU/171MB` worsened short k6 to `p99=0.57ms`, zero errors.
- limiting keep-alive requests did not help in direct tests:
  `KEEP_ALIVE_REQUESTS=32` worsened short k6 to `0.55ms`; `128` stayed at
  `0.54ms`.
- Additional keep-alive rebalancing values also failed to sustain:
  `KEEP_ALIVE_REQUESTS=16` reached `p99=0.52ms` in short k6 but regressed to
  `0.56ms` in complete k6; `64/96` stayed at `0.53ms-0.54ms` in short k6.
- aggressive keep-alive limits made reconnection overhead dominate:
  `KEEP_ALIVE_REQUESTS=1/2/4/8` worsened short k6 to `0.82ms/0.66ms/0.62ms/0.58ms`.
- `FD_RCVLOWAT=128/256/512` is not viable: the matrix hung before producing a
  valid result and had to be stopped; experiment was removed.
- LB-side `SO_RCVLOWAT` values low enough to keep `/ready` alive did not help:
  `LB_RCVLOWAT=64` stayed at `p99=0.54ms` in short k6, and `32` worsened to
  `0.55ms`. Experiment was removed.

Classifier/index:

- `KDTREE_LEAF_SIZE=64/80/112/128`: did not beat leaf `96` on complete k6.
- Additional fine-grained leaf-size checks also did not produce a candidate:
  `KDTREE_LEAF_SIZE=88` stayed at `p99=0.54ms` in short k6; `104` improved one
  short k6 to `p99=0.52ms`, but complete k6 regressed to `p99=0.54ms`.
  Keep leaf `96`.
- `KDTREE_KEY_PROFILE=1+`: may improve eval in spots, did not beat k6.
- `KDTREE_MAX_PARTITIONS` reduction: no useful gain and can risk accuracy.
- ASM-inspired lower-cardinality KD partition profiles were rejected. A
  temporary `KDTREE_KEY_PROFILE=11` using only the base flags compiled, but was
  far slower than the current 256-partition key: even `EVAL_LIMIT=5000`
  exceeded 120s. The experiment was removed before k6; profile `12` was not
  pursued because it would create even larger partitions.
- Reducing native KD partitions below `KDTREE_MAX_PARTITIONS=6` is not safe on
  the current data: `5` produced `8` FP and `16` FN in `eval`; `4/3/2`
  produced progressively more errors.
- A two-pass KD experiment (`KDTREE_FAST_PARTITIONS=5` with fallback to `6` for
  borderline `2/3` fraud-neighbor counts) preserved zero errors but was slower
  offline than the baseline: native JSON eval p99 worsened from about `35.5us`
  to `38.6us`. `KDTREE_FAST_PARTITIONS=4` still produced 1 FP. Experiment was
  removed before k6.
- reducing candidates to `EARLY/MIN/MAX=9000/9000/10000` passed eval and reached
  `p99=0.52ms` in short k6, but complete k6 regressed to `p99=0.55ms`.
- `KD_NODE_QUEUE_SIZE=512` looked promising in short k6 (`p99=0.51ms`) but
  complete k6 regressed to `p99=0.56ms`; `256` was already worse in short k6
  at `p99=0.55ms`.
- Intermediate `KD_NODE_QUEUE_SIZE` values did not produce a new candidate:
  `576/704/768` stayed at `p99=0.54ms` in short k6; `640` reached
  `p99=0.53ms` in short k6 but full k6 regressed to `p99=0.54ms`.
  Combining `640` with `SOCKET_BUFFER_SIZE=512`, `KEEP_ALIVE_REQUESTS=16`,
  `lb=0.16/api=0.42`, or broad cpusets stayed at `0.54ms-0.55ms`.
- `KD_SCALAR_EARLY=1` worsened short k6 to `p99=0.57ms`; keep AVX2 leaf
  distance.
- `KD_HOIST_QUERY=1`: hoisting the query vector once per KD leaf stayed
  correct, but worsened short k6 to `p99=0.55ms`; experiment removed.
- Early return from the KD primary partition when its top-5 was unanimous was
  unsafe even with the current profile bundle: native JSON eval produced
  `39` FP and `8` FN. Do not use primary-partition unanimity as an accuracy
  shortcut.
- Limiting KD partition candidate insertion to the number of partitions that
  can actually be visited preserved zero-error eval, but did not move k6:
  short k6 stayed at `p99=0.54ms`. Experiment was removed.
- Replacing the AVX2 distance lane sum with a 32-to-64-bit vector reduction
  (`KD_SUM64=1`) stayed correct but did not improve the path: native JSON eval
  was effectively flat and short k6 worsened to `p99=0.55ms`. Experiment was
  removed.
- disabling `KD_BEST_FIRST=1` stayed at `p99=0.54ms` in short k6; keep
  best-first enabled.
- `NATIVE_ANN=1` / `NATIVE_ANN_DIRECT=1` is not a replacement for native KD on
  the current data. With `KDTREE_NATIVE=0`, offline eval produced `314` FP,
  `17` FN and classifier `p99=130738ns`; do not run k6 on that path without a
  new accuracy strategy.
- `PROFILE_DOMINANT_FASTPATH=1`: small coverage increase, but generated false
  positives.
- `BUCKET_FASTPATH=1`: JSON path produced false positives or worse k6.
- More aggressive `PROFILE_FRAUD_AMOUNT_MIN` below about `3910`: false
  positives on current public data.
- `PROFILE_FRAUD_AMOUNT_MIN=3950` passed native JSON eval with zero errors but
  short k6 stayed at `p99=0.54ms`; keep `4000` as the less aggressive
  reference.
- `PROFILE_LEGIT_MIN_COUNT=12`, `PROFILE_FRAUD_MIN_COUNT=7`,
  `PROFILE_FRAUD_AMOUNT_MIN=4000` passed native JSON eval and full k6 with zero
  errors, but complete k6 worsened to `p99=0.55ms`.
- Rechecking whether profile fast path still matters showed it does:
  `PROFILE_FASTPATH=0` worsened smoke to `p99=0.58ms`, and disabling only
  `PROFILE_FRAUD_LOW_AMOUNT_FASTPATH` worsened smoke to `0.57ms`. The current
  profile bundle can still hit `0.52ms` in smoke, but complete k6 rechecks
  return to about `0.55ms`.
- Conservative dominant profile fast path with `PROFILE_DOMINANT_FASTPATH=1`,
  `PROFILE_DOMINANT_MIN_COUNT=15`, `PROFILE_DOMINANT_MAX_OPPOSITE=0` and both
  legit/fraud enabled passed native JSON eval with zero errors and improved
  offline classifier p99 to about `36us`, but short k6 worsened to `p99=0.55ms`.
  `MAX_OPPOSITE=1` is not safe: eval produced one false positive.
- `PROFILE_LEGIT_MIN_COUNT=8..11` with `PROFILE_FRAUD_MIN_COUNT=7` stayed
  zero-error in native JSON eval and reduced offline classifier p99 to about
  `35us`, but `PROFILE_LEGIT_MIN_COUNT=10` worsened short k6 to `p99=0.55ms`.
- specialized native profile function for the current `15/8/4000` thresholds
  reduced P/Invoke/config overhead and reached `p99=0.51ms` in short k6, but
  complete k6 regressed to `p99=0.55ms`; combining with `PIN_FIRST_CPU=1`
  worsened short k6 to `0.56ms`. Experiment was removed.
- dominant profile fast path for legitimate-only decisions passed eval with
  zero errors, but short k6 worsened to `p99=0.56ms`.
- Additional profile fast paths were rechecked after the `TP_MIN_IO_THREADS=4`
  work. `PROFILE_FRAUD_MID_AMOUNT_NO_LAST_FASTPATH` and conservative dominant
  profile variants stayed zero-error in `eval`, but their eval p99 was worse
  than the current bundle and they were not promoted to k6. `BUCKET_FASTPATH`
  with conservative `64/64` thresholds still produced false positives.
- A later expanded native JSON eval matrix over
  `PROFILE_LEGIT_MIN_COUNT=7..16`, `PROFILE_FRAUD_MIN_COUNT=6..9`,
  `PROFILE_FRAUD_AMOUNT_MIN=3900/3950/4000/4100`, conservative dominant
  profile variants, and mid-amount no-last variants found many zero-error
  combinations. The best eval p99 was around `34.7us`, but the top k6 candidate
  (`legit=10`, `fraud=6`, `amount=3950`) worsened short k6 to `p99=0.55ms`.
  No profile-threshold change was promoted.
- `PROFILE_LEGIT_MIN_COUNT=6`: false negative.
- `RISKY_SOA=1` with `BUILD_NATIVE_ONLY_INDEX=0` stayed correct but only tied
  short k6 at `p99=0.54ms`; disabling `RISKY_NATIVE_FINE` to force managed SOA
  worsened short k6 to `p99=0.61ms`. Keep the native fine fallback and the
  native-only index.
- Disabling mapped/compact risky fallback toggles on the current native KD JSON
  path (`RISKY_COMPACT=0`, `RISKY_FINE_BUCKETS=0`, `RISKY_SIMD=0`,
  `RISKY_NATIVE_FINE=0`) stayed at `p99=0.54ms` in short k6; no gain.
- ASM-style obvious legit/fraud fast path compiled as
  `-DOBVIOUS_FASTPATH=1` was correct on full native JSON eval and had one
  complete-k6 run at `p99=0.53ms`, but the source-clean recheck returned to
  `p99=0.54ms`; combinations with `PIN_FIRST_CPU=1`, unfair semaphore,
  `FD_RECEIVERS=4`, `KEEP_ALIVE_REQUESTS=16`, `SOCKET_BUFFER_SIZE=512`,
  `KDTREE_MAX_PARTITIONS=6`, `-flto`, and profile `12/7` did not improve.
  Moving the decision earlier into the JSON parser worsened smoke to
  `p99=0.57ms`. Experiment was removed.
- ASM-inspired MCC lookup by 4-byte integer switch (`MCC_U32_LOOKUP=1`) stayed
  correct but did not improve the official local smoke path: `p99=0.54ms`,
  `final_score=6000`, zero errors. The experiment was removed.
- reducing candidate counts: no complete-k6 win.

Build/codegen:

- `-flto`: worsened smoke.
- `-Ofast`: did not improve complete results.
- `-fno-plt -fno-semantic-interposition`: no gain.
- `-march=znver2`: not portable and no durable gain.
- `-falign-functions=32 -falign-loops=32`: worsened short k6 to `p99=0.55ms`.
- `.NET` `DisableRuntimeMarshalling=true`: build completed but the backend did
  not become ready, so it is not compatible with the current interop surface.
- Native AOT `IlcInstructionSet` extensions beyond the current `avx2` were
  rejected by the .NET 10 ILCompiler image before k6: `bmi` and `sse3` were
  reported as unrecognized instruction sets. Keep `IlcInstructionSet=avx2`.
- Native AOT support trimming (`EventSourceSupport=false`,
  `StackTraceSupport=false`, `DebuggerSupport=false`,
  `MetadataUpdaterSupport=false`, `UseSystemResourceKeys=true`) compiled and
  reached `p99=0.52ms` in short k6, but complete k6 worsened to `p99=0.56ms`;
  experiment was removed.
  Retesting the same trimming after persisting LB `somaxconn=65535` did not
  produce a candidate either: short k6 stayed at `p99=0.54ms`, score `6000`,
  zero errors. Keep the default AOT support settings.
- native `-fno-unwind-tables -fno-asynchronous-unwind-tables` worsened short k6
  to `p99=0.55ms`.
- native/LB `-Ofast -fomit-frame-pointer -fno-math-errno` worsened short k6 to
  `p99=0.57ms`.
- native/LB `-fno-plt` stayed at `p99=0.54ms` in short k6.
- native/LB `-flto=thin` built cleanly in a one-line shell but worsened short
  k6 to `p99=0.55ms`.
- native/LB `-fno-semantic-interposition` and
  `-fno-semantic-interposition -fno-exceptions` both worsened short k6 to
  `p99=0.56ms`; `-funroll-loops` worsened to `0.59ms`.
- native/LB `-march=native -mtune=native` worsened short k6 to `p99=0.55ms`;
  `-march=znver3 -mtune=znver3` worsened to `0.60ms`. Keep the current
  Haswell-targeted build for this machine and remote portability.
- Native/LB size-oriented optimization levels did not produce a candidate:
  appending `-O2` after the default `-O3` stayed at `p99=0.54ms` in short k6,
  `-Os` worsened to `0.55ms`, `-Oz` reached only `0.53ms`, and
  `-Oz + PIN_FIRST_CPU=1` worsened to `0.56ms`. Keep default `-O3`.
- `FD_SENDMSG_FAST=1`: removing per-call zeroing in the LB `sendmsg` path
  reached `p99=0.51ms` once in short k6, but did not sustain; complete k6
  worsened to `p99=0.56ms`, and combining with `lb=0.04/api=0.48` returned to
  `p99=0.54ms` in short k6. Experiment was removed.
- direct native JSON function-pointer call via `NativeLibrary` was correct but
  worsened short k6 to `p99=0.55ms`; experiment was removed.

Response/header micro-optimizations:

- Explicit `[MethodImpl(AggressiveInlining)]` on raw-fd hot methods
  (`RequestComplete`, `SelectResponse`, `Receive`, `Send`) worsened short k6
  to `p99=0.59ms`; experiment was removed.
- `[SkipLocalsInit]` on raw-fd hot methods (`HandleConnection`, `RequestComplete`,
  `Receive`, `ReceiveDontWait`, `Send`, `ReceiveSocketFdRaw`) was correct but
  did not improve the complete local k6 floor: full k6 stayed at `p99=0.54ms`,
  score `6000`, zero errors. Experiment was removed.
- removing reason phrase `OK`: latest explicit short k6 stayed at `p99=0.55ms`,
  including with `PIN_FIRST_CPU=1`; no gain.
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
- `FD_NEW_BUFFER=1`: replacing `ArrayPool` with
  `GC.AllocateUninitializedArray` per raw-fd connection reached `p99=0.52ms`
  in short k6, but complete k6 regressed to `p99=0.56ms`; experiment was
  removed.
- `FD_THREAD_BUFFER=1` on the current fdpass/raw path reached `p99=0.50ms` in
  short k6 but complete k6 regressed to `p99=0.55ms`; experiment was removed.
  A later recheck after the latest `TP_MIN_IO_THREADS=4`/cpuset state did not
  recover the old short-run signal: isolated `FD_THREAD_BUFFER=1` stayed at
  `p99=0.55ms`, `FD_THREAD_BUFFER=1 + PIN_FIRST_CPU=1` stayed at `0.55ms`, and
  `FD_THREAD_BUFFER=1 + FD_RECEIVERS=4` worsened to `0.57ms`; experiment was
  removed again.
- `FD_WORKITEM_POOL=1`: replacing the generic
  `ThreadPool.UnsafeQueueUserWorkItem` state path with a reusable
  `IThreadPoolWorkItem` pool improved one short k6 comparison
  (`0.53ms` vs `0.55ms` baseline) but complete k6 regressed to `p99=0.56ms`,
  zero errors. Experiment was removed.
- `FD_ASSUME_SCM_RIGHTS=1`: skipping repeated cmsg validation in the API
  `recvmsg(SCM_RIGHTS)` path reached `p99=0.52ms` in short k6, but complete k6
  regressed to `p99=0.58ms`, zero errors. Experiment was removed.
- Moving only the API `recvmsg(SCM_RIGHTS)` receive into a tiny native helper
  (`FD_NATIVE_RECVFD=1`) stayed correct but reached only `p99=0.53ms` in short
  k6. Combining it with `cpu_shares=2048` worsened to `0.55ms`; experiment and
  temporary compose overrides were removed.
- `FD_INLINE=1` is not viable with k6 keep-alive connections: it serialized
  long-lived client sockets and produced about `10%` HTTP timeouts. Duplicating
  LB `UPSTREAMS` to open multiple control connections did not fix it.
- `FD_FIRST_INLINE=1`: processing only the first request on the fd receiver
  thread before handing the keep-alive connection to the ThreadPool was correct,
  but short k6 reached only `p99=0.53ms`; increasing `FD_RECEIVERS` to `3/4`
  worsened to `0.54ms`. Experiment was removed.
- `ASSUME_NO_EXCEPTIONS=1`: removing the hot-path `try/catch` via toggle stayed
  at `p99=0.54ms`; experiment was removed.
- `FAST_LIBC_IO=1`: using `send/recv` P/Invokes without `SetLastError` stayed
  at `p99=0.54ms`; experiment was removed.
- `FD_SINGLE_READ_FASTPATH=1`: one-read raw-fd request path reached `0.53ms`
  in short k6 but complete k6 stayed at `p99=0.54ms`; experiment was removed.
- `FD_UNLIMITED_FAST=1`: splitting the raw-fd loop for
  `KEEP_ALIVE_REQUESTS=0` to remove the per-request keep-alive counter/branch
  worsened short k6 to `p99=0.59ms`; experiment was removed.
- native full fd handler (`NATIVE_FD_HANDLER=1`) was correct but did not beat
  the floor: complete k6 stayed at `p99=0.54ms` with zero errors, including the
  JSON body-start fast path and `KD_BEST_FIRST`.
- fixed fd worker queue instead of `ThreadPool.UnsafeQueueUserWorkItem` was
  rejected: short k6 stayed at `0.55ms` with 64 workers, `0.54ms` with 128
  workers and `0.56ms` with 256 workers; experiment was removed.
- reverse search for JSON body start was rejected: short k6 produced `5925`
  false negatives because the request JSON has nested `{` characters; experiment
  was removed.
- `ASSUME_FRAUD_ONLY_REQUESTS=1`: skipping the full path-prefix check inside
  `RequestComplete` initially broke `/ready`; after limiting it to `POST`
  traffic, short k6 stayed at `p99=0.54ms`; experiment was removed.
- skipping the `POST /fraud-score` prefix check inside `RequestComplete` when
  `ASSUME_FRAUD_SCORE_PATH=1` stayed at `p99=0.54ms` in short k6; experiment
  was removed.
- fixed k6 body offset fast path (`ASSUME_FIXED_BODY_OFFSET=128`) was correct
  for `BASE_URL=http://lb:9999` and kept zero errors, but worsened short k6 to
  `p99=0.56ms`; experiment was removed.
- Rechecking parse completion without `ASSUME_JSON_BODY_START` was not a
  candidate. `ASSUME_BODY_COMPLETE=1` without the JSON body-start shortcut
  reached `p99=0.53ms` in short k6, but complete k6 regressed to `0.56ms`,
  zero errors. Keep the current JSON body-start fast path.
- assuming single `send()` writes the whole tiny response was correct but
  worsened short k6 to `p99=0.56ms`; experiment was removed.
- assuming both single tiny `send()` completion and no `recv()` `EINTR` reached
  `p99=0.51ms` in short k6, but complete k6 stayed at `p99=0.54ms`; combining
  with `FD_THREAD_BUFFER=1` worsened short k6 to `p99=0.54ms`. Experiment was
  removed.
- replacing fd response `send(..., MSG_NOSIGNAL)` with `write(2)` was correct
  but did not improve short k6: baseline was `p99=0.53ms`, `FD_USE_WRITE=1`
  was `0.54ms`, and adding `PIN_FIRST_CPU=1` was `0.55ms`. Experiment was
  removed.
- using `read(2)` instead of `recv(2)` for blocking raw-fd request reads was
  correct but stayed at `p99=0.54ms`; experiment was removed.
- `FD_FAST_SYSCALLS=1`, using no-`SetLastError` DllImports for raw `recv` and
  `send`, was correct but did not move the complete floor: short k6 reached
  `p99=0.53ms`, complete k6 stayed at `p99=0.54ms`, and
  `+PIN_FIRST_CPU=1` worsened short k6 to `0.57ms`. Experiment was removed.
- External warmup with 1000 synthetic `POST /fraud-score` requests before k6
  did not help (`p99=0.55ms`, zero errors). Do not add API startup classifier
  warmup unless a future profile shows cold classification in the measured p99.
- Internal startup classifier prewarm, calling the native JSON classifier 1000
  times per API before opening the fd control socket, also did not help:
  short k6 stayed at `p99=0.54ms`. Experiment was removed.
- In `ASSUME_JSON_BODY_START`, starting the `{` search after the fixed
  `POST /fraud-score` prefix instead of byte zero was correct but worsened
  short k6 to `p99=0.55ms`; experiment was removed.
- Returning `byte[]` directly from `SelectResponse` instead of
  `ReadOnlyMemory<byte>` improved one short k6 to `p99=0.53ms`, but complete
  k6 regressed to `p99=0.55ms`, zero errors; experiment was removed.
- `FD_UNMANAGED_RESPONSES=1`, copying the static HTTP responses to unmanaged
  memory to avoid per-send pinning in the raw-fd path, worsened short k6 to
  `p99=0.57ms`; experiment was removed.

## Profiling Notes

- A short `perf record` during local k6 showed most sampled CPU in the k6
  runner and kernel bridge/netfilter paths; `.NET` worker samples were much
  smaller and led by Docker/network syscalls plus native KD-tree search.
- A later `perf stat` around a 30s k6 run showed about `1.35M` context
  switches and `36k` CPU migrations while the app still had zero errors; the
  sampled profile again skewed toward k6/kernel/scheduler, not managed
  classification.
- Reprofile after `TP_MIN_IO_THREADS=4`: a 30s k6 run under
  `perf stat -a` still showed scheduler/kernel noise dominating the local
  floor (`529738` context switches, `5136` CPU migrations, `210828` page
  faults over `38.8s`). No new app-side CPU hotspot was exposed.
- Reprofile after persisting LB `somaxconn=65535`: a traffic-only 30s k6 pass
  against already-running services produced `p99=0.57ms`, zero detection errors,
  with `503520` context switches, `6104` CPU migrations and `212665` page
  faults over `38.5s`. Running under `perf stat` itself adds noise, but the
  signal still points at scheduler/network/test-runner overhead rather than a
  new classifier hotspot.
- A later service-PID-only `perf stat` recheck still changed the measurement
  enough to be unsuitable as a gate: k6 smoke rose to `p99=0.59ms` while the
  three service PIDs used only about `2.09s` task-clock over `45s`. A clean
  smoke immediately after returned to `p99=0.54ms`.
- Explicit `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` on both APIs did not help
  the already invariant Native AOT binary; short k6 worsened to `p99=0.55ms`.
- LB namespace `net.ipv4.tcp_autocorking=0` stayed correct but only matched
  `p99=0.54ms` in short k6; do not promote it.
- Native JSON-only offline eval for the best local bundle has no parser
  fallback on the current dataset: `total=54100`, `parse_errors=0`,
  `fp=0`, `fn=0`, classifier `p99=55215ns`. The remaining local k6 p99 is
  therefore not caused by managed parser fallback.
- A guarded native timestamp shortcut for 2026 dates was tested only as a
  build flag. It did not beat the controlled baseline in native JSON eval
  (`36028ns` vs `35548ns` p99), so the code was removed before k6.
- A complete k6 run kept containers alive for cgroup inspection:
  `p99=0.55ms`, zero errors. `cpu.stat` showed minimal quota pressure
  (`api1/api2` about `3.1s` CPU over the run, `nr_throttled=3`,
  `throttled_usec≈0.11s`; LB about `0.057s` CPU, `nr_throttled=1`).
  This reinforces that the remaining local p99 floor is dominated by
  Docker/k6/network/scheduler variance, not classifier CPU.
- Pinning the app to CPUs `0/1/2` and the k6 runner to CPU `3` was only
  diagnostic and worsened short k6 to `p99=3.65ms`; do not use runner/app
  cpuset isolation as a local shortcut.
- Alternative k6 images did not help local p99: `grafana/k6:1.3.0` worsened
  short k6 to `0.57ms`, and `grafana/k6:1.2.3` worsened to `0.55ms`.
- Changing k6 VU allocation is not an application optimization and did not
  help local diagnosis: `PRE_ALLOCATED_VUS/MAX_VUS=50/100`, `75/150`,
  `100/250`, `150/300` produced short k6 `0.62ms`, `0.62ms`, `0.63ms`,
  and `0.68ms` with 40 HTTP errors.
- Apparent `dockerd`/`containerd` 100% CPU with no containers was startup
  noise while Docker was still `activating` and initializing BuildKit. After
  waiting for both services to become `active`, a 3s `/proc/<pid>/stat` delta
  showed about `0%` real CPU for both. Do not benchmark during that activation
  window.
- Temporarily disabling WSL bridge netfilter was only diagnostic and was
  restored; it did not provide a publishable application optimization.
- The current official local k6 compose uses `network_mode: host` for the k6
  container and posts to `localhost:9999`. `scripts/k6-local.sh` now supports
  `K6_NETWORK_MODE=host` to measure that path without changing the default
  bridge runner.
- Host-network k6 against the best local bundle is much slower in this WSL
  Docker setup: complete k6 returned `p99=0.78ms`, score `6000`, zero errors.
  Short 30s checks stayed in the same band: `localhost` `0.82ms`, `127.0.0.1`
  `0.83ms`.
- Host-path diagnostics did not expose an application-side candidate:
  publishing only `127.0.0.1:9999:9999` stayed at `0.82ms`, 2000 external
  warmup POSTs worsened to `0.88ms`, `lb=0.20/api=0.40` stayed at `0.82ms`,
  and `lb=0.08/api=0.46` stayed at `0.81ms`. The signal points at Docker
  published-port/proxy overhead rather than the `.NET` classifier path.
- During host/bridge rechecks, C: reached `0 bytes` free and WSL started
  failing new processes with `getpwuid(0) failed 5` / `I/O error`; the bridge
  result from that failed run is invalid. Recovery was: remove temporary
  `%TEMP%\rinha-*` clones, run `docker builder prune -af` and
  `docker image prune -af` inside WSL, then `Optimize-VHD -Mode Full` on the
  Ubuntu `ext4.vhdx`. This shrank the VHD from about `316.8GB` to `15.39GB`
  and restored about `324GB` free on C:. Check disk space before long matrix
  runs.

## Reference Fingerprint Gate

Implemented on 2026-05-28 to protect score when `resources/references.json.gz`
changes:

- `references.idx` now stores build metadata: reference count, decompressed
  JSON SHA-256, gzip SHA-256 when available, KD-tree leaf size and key profile.
- `rinha-fraud index-info <references.idx>` prints the embedded metadata.
- `EXPECTED_REFERENCES_GZIP_SHA256` and `EXPECTED_REFERENCES_JSON_SHA256` fail
  startup/eval if the image carries the wrong index.
- `PROFILE_FASTPATH_REFERENCE_SHA256` is now required for profile/bucket fast
  paths to run. If it is absent or does not match the embedded reference hash,
  those fast paths are disabled.
- `scripts/k6-local.sh` and `scripts/k6-local.ps1` propagate the reference
  guard variables into generated compose overrides.
- `scripts/reference-refresh.sh` now runs `validate-local` without k6 first,
  then `validate-reference-candidate.sh` for safe eval, gated fast-path eval
  and k6/mixed k6 when enabled.

Validation:

- Host `dotnet build -c Release` and `self-test`: OK.
- WSL `validate-reference-candidate.sh` without k6: index metadata matched
  `references_gzip_sha256=43d10de80609e77ce25740f375607afce7561ec44da50c27c142493db8fcab67`;
  safe eval and gated candidate eval both had zero FP/FN/parse errors.
- WSL official local k6 with the gated candidate:
  `p99=0.56ms`, `final_score=6000`, zero FP/FN/HTTP errors.
- WSL `PAYLOAD_VARIANT=mixed` k6 with the same gate:
  `p99=0.57ms`, `final_score=6000`, zero FP/FN/HTTP errors.
- WSL `validate-local.sh RUN_K6=0`: OK; the deliberately unsafe profile check
  still records divergence and keeps the safe default.
- Wrong `EXPECTED_REFERENCES_GZIP_SHA256` fails with an index hash mismatch
  before evaluation.
- `PROFILE_FASTPATH=1` without `PROFILE_FASTPATH_REFERENCE_SHA256` falls back
  to the safe classifier path: zero errors, safe fraud-count bucket
  distribution, and eval p99 around `90us` instead of the gated fast-path
  `36us`.

This does not prove a future unknown reference file will keep remote p99 below
1ms. It does make the failure mode safer: changed references require rebuilding
the index, and experimental fast paths are not used unless the new reference
hash has been explicitly validated.

## Reference Change Rule

When references change:

1. Rebuild `references.idx`.
2. Verify `index-info` includes the new reference SHA.
3. Run `validate-local`.
4. Run `validate-reference-candidate.sh` with safe eval and gated fast-path
   eval.
5. Keep `PROFILE_FASTPATH=0` for the safe path, or set
   `PROFILE_FASTPATH_REFERENCE_SHA256=<new refs sha>` only after the gated
   candidate is clean.
6. Use `EVAL_NATIVE_JSON=1` when validating native JSON fast paths.
7. Publish a candidate image only after local `eval` and k6 are clean.
8. Do not move the `submission` branch or open remote automatically.

## Next Plausible Work

- Profile the clean `.NET` fdpass path after `TP_PREWARM=64` to identify the
  remaining p99 source.
- Focus on reducing runtime/socket scheduling overhead; classifier-only gains
  have not moved official k6 enough.
- Treat any new profile shortcut as unsafe until it passes `EVAL_NATIVE_JSON=1`
  and full k6 with zero errors.
