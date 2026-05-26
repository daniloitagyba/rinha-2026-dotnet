#!/bin/sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
TEST_MOUNT="${TEST_MOUNT:-$ROOT/test}"
MODE="${MODE:-submission}"
RUNNER_PRESET="${RUNNER_PRESET:-default}"
PROJECT_NAME="${PROJECT_NAME:-rinha-local}"
K6_IMAGE="${K6_IMAGE:-grafana/k6:latest}"
SUBMISSION_COMPOSE_FILE="${SUBMISSION_COMPOSE_FILE:-}"
KEEP_SERVICES="${KEEP_SERVICES:-0}"
REFRESH_DATA="${REFRESH_DATA:-0}"
PULL="${PULL:-0}"
OVERRIDE_FILE=""
EXTRA_COMPOSE_FILE="${EXTRA_COMPOSE_FILE:-}"
DOCKER_OS=""

if command -v docker >/dev/null 2>&1; then
  DOCKER_OS="$(docker info --format '{{.OperatingSystem}}' 2>/dev/null || true)"
fi

if [ -z "${DOCKER_CONFIG:-}" ] && \
   [ -n "$DOCKER_OS" ] && \
   [ "$DOCKER_OS" != "Docker Desktop" ] && \
   [ -f "$HOME/.docker/config.json" ] && \
   grep -Eq '"credsStore"[[:space:]]*:[[:space:]]*"desktop\.exe"' "$HOME/.docker/config.json"; then
  DOCKER_CONFIG="${TMPDIR:-/tmp}/docker-anon"
  mkdir -p "$DOCKER_CONFIG"
  printf '{"auths":{}}\n' > "$DOCKER_CONFIG/config.json"
  export DOCKER_CONFIG
fi

case "$RUNNER_PRESET" in
  default)
    ;;
  remote-ryzen)
    API_CPU="${API_CPU:-0.300}"
    LB_CPU="${LB_CPU:-0.110}"
    ;;
  remote-ryzen-hard)
    API_CPU="${API_CPU:-0.300}"
    LB_CPU="${LB_CPU:-0.108}"
    ;;
  *)
    echo "RUNNER_PRESET must be default, remote-ryzen or remote-ryzen-hard" >&2
    exit 2
    ;;
esac

if [ "$MODE" = "submission" ]; then
  if [ -n "$SUBMISSION_COMPOSE_FILE" ]; then
    COMPOSE_FILE="$SUBMISSION_COMPOSE_FILE"
  elif [ -f "$ROOT/../rinha-2026-submission/docker-compose.yml" ]; then
    COMPOSE_FILE="$ROOT/../rinha-2026-submission/docker-compose.yml"
  elif [ -f "/mnt/c/tmp/rinha-2026-submission/docker-compose.yml" ]; then
    COMPOSE_FILE="/mnt/c/tmp/rinha-2026-submission/docker-compose.yml"
  else
    COMPOSE_FILE="$ROOT/submission/docker-compose.yml"
  fi
elif [ "$MODE" = "build" ]; then
  COMPOSE_FILE="$ROOT/docker-compose.yml"
else
  echo "MODE must be submission or build" >&2
  exit 2
fi

if [ "$MODE" = "build" ] && \
   [ -z "${COMPOSE_BAKE+x}" ] && \
   [ -n "$DOCKER_OS" ] && \
   [ "$DOCKER_OS" != "Docker Desktop" ]; then
  export COMPOSE_BAKE=false
fi

if [ "$MODE" = "build" ] && [ -z "${COMPOSE_PARALLEL_LIMIT:-}" ]; then
  export COMPOSE_PARALLEL_LIMIT=1
fi

if [ "$REFRESH_DATA" = "1" ] || [ ! -f "$ROOT/test/test-data.json" ]; then
  FORCE="$REFRESH_DATA" sh "$ROOT/scripts/sync-official-data.sh"
fi

compose() {
  if [ -n "$OVERRIDE_FILE" ] && [ -n "$EXTRA_COMPOSE_FILE" ]; then
    docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" -f "$EXTRA_COMPOSE_FILE" -f "$OVERRIDE_FILE" "$@"
  elif [ -n "$OVERRIDE_FILE" ]; then
    docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" -f "$OVERRIDE_FILE" "$@"
  elif [ -n "$EXTRA_COMPOSE_FILE" ]; then
    docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" -f "$EXTRA_COMPOSE_FILE" "$@"
  else
    docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" "$@"
  fi
}

cleanup() {
  if [ "$KEEP_SERVICES" != "1" ]; then
    compose down --remove-orphans -v >/dev/null 2>&1 || true
  fi

  if [ -n "$OVERRIDE_FILE" ]; then
    rm -f "$OVERRIDE_FILE"
  fi
}
trap cleanup EXIT INT TERM

if [ -n "${EARLY_CANDIDATES:-}" ] || \
   [ -n "${MIN_CANDIDATES:-}" ] || \
   [ -n "${MAX_CANDIDATES:-}" ] || \
   [ -n "${PROFILE_FASTPATH:-}" ] || \
   [ -n "${PROFILE_MIN_COUNT:-}" ] || \
   [ -n "${PROFILE_LEGIT_MIN_COUNT:-}" ] || \
   [ -n "${PROFILE_FRAUD_MIN_COUNT:-}" ] || \
   [ -n "${PROFILE_FRAUD_AMOUNT_MIN:-}" ] || \
   [ -n "${PROFILE_FRAUD_LOW_AMOUNT_FASTPATH:-}" ] || \
   [ -n "${PROFILE_FRAUD_LOW_AMOUNT_KM_HOME_MIN:-}" ] || \
   [ -n "${PROFILE_FRAUD_LOW_AMOUNT_TX24H_MIN:-}" ] || \
   [ -n "${PROFILE_FRAUD_MID_AMOUNT_NO_LAST_FASTPATH:-}" ] || \
   [ -n "${PROFILE_FRAUD_MID_AMOUNT_MIN:-}" ] || \
   [ -n "${PROFILE_FRAUD_NO_LAST_ONLY:-}" ] || \
   [ -n "${PROFILE_DOMINANT_FASTPATH:-}" ] || \
   [ -n "${PROFILE_DOMINANT_MIN_COUNT:-}" ] || \
   [ -n "${PROFILE_DOMINANT_MAX_OPPOSITE:-}" ] || \
   [ -n "${PROFILE_DOMINANT_LEGIT_ENABLED:-}" ] || \
   [ -n "${PROFILE_DOMINANT_FRAUD_ENABLED:-}" ] || \
   [ -n "${BUCKET_FASTPATH:-}" ] || \
   [ -n "${BUCKET_LEGIT_MIN_COUNT:-}" ] || \
   [ -n "${BUCKET_FRAUD_MIN_COUNT:-}" ] || \
   [ -n "${BUCKET_FRAUD_NO_LAST_ONLY:-}" ] || \
   [ -n "${EXACT_FALLBACK:-}" ] || \
   [ -n "${EARLY_EDGE_FALLBACK:-}" ] || \
   [ -n "${RISKY_AMOUNT_MIN:-}" ] || \
   [ -n "${RISKY_AMOUNT_MAX:-}" ] || \
   [ -n "${RISKY_INSTALLMENTS_MIN:-}" ] || \
   [ -n "${RISKY_INSTALLMENTS_MAX:-}" ] || \
   [ -n "${RISKY_RATIO_MIN:-}" ] || \
   [ -n "${RISKY_KM_HOME_MIN:-}" ] || \
   [ -n "${RISKY_KM_HOME_MAX:-}" ] || \
   [ -n "${RISKY_TX24H_MIN:-}" ] || \
   [ -n "${RISKY_TX24H_MAX:-}" ] || \
   [ -n "${RISKY_MERCHANT_AVG_MIN:-}" ] || \
   [ -n "${RISKY_MERCHANT_AVG_MAX:-}" ] || \
   [ -n "${RISKY_COMPACT:-}" ] || \
   [ -n "${RISKY_FINE_BUCKETS:-}" ] || \
   [ -n "${RISKY_SIMD:-}" ] || \
   [ -n "${RISKY_NATIVE_FINE:-}" ] || \
   [ -n "${NATIVE_ANN:-}" ] || \
   [ -n "${NATIVE_ANN_DIRECT:-}" ] || \
   [ -n "${KDTREE_MAX_PARTITIONS:-}" ] || \
   [ -n "${BLOCK_SCAN:-}" ] || \
   [ -n "${SOCKETS_MOUNT:-}" ] || \
   [ -n "${API_ENTRYPOINT:-}" ] || \
   [ -n "${WORKERS:-}" ] || \
   [ -n "${NATIVE_WORKERS:-}" ] || \
   [ -n "${FD_RECEIVERS:-}" ] || \
   [ -n "${FD_RAW:-}" ] || \
   [ -n "${FD_DEDICATED_THREADS:-}" ] || \
   [ -n "${FD_THREAD_STACK_KB:-}" ] || \
   [ -n "${FD_EPOLL:-}" ] || \
   [ -n "${TCP_QUICKACK:-}" ] || \
   [ -n "${NATIVE_EPOLL:-}" ] || \
   [ -n "${FD_IMMEDIATE_READ:-}" ] || \
   [ -n "${FD_PRE_READ:-}" ] || \
   [ -n "${ASSUME_BODY_COMPLETE:-}" ] || \
   [ -n "${ASSUME_FRAUD_SCORE_PATH:-}" ] || \
   [ -n "${ASSUME_JSON_BODY_START:-}" ] || \
   [ -n "${EPOLL_ET:-}" ] || \
   [ -n "${PIN_FIRST_CPU:-}" ] || \
   [ -n "${FD_CONTROL_SEQPACKET:-}" ] || \
   [ -n "${FD_CONTROL_PREBUFFER:-}" ] || \
   [ -n "${LB_SOCKET_BUFFERS:-}" ] || \
   [ -n "${LB_TCP_NODELAY:-}" ] || \
   [ -n "${SOCKET_BUFFER_SIZE:-}" ] || \
   [ -n "${REFERENCE_FASTPATH_FRAUD_ONLY:-}" ] || \
   [ -n "${REFERENCE_FASTPATH_FRAUD_MIN_COUNT:-}" ] || \
   [ -n "${SERVER_MODE:-}" ] || \
   [ -n "${INDEX_HUGEPAGES:-}" ] || \
   [ -n "${MALLOC_ARENA_MAX:-}" ] || \
   [ -n "${DOTNET_PROCESSOR_COUNT:-}" ] || \
   [ -n "${DOTNET_GCHeapCount:-}" ] || \
   [ -n "${DOTNET_ThreadPool_UnfairSemaphoreSpinLimit:-}" ] || \
   [ -n "${DOTNET_GCConserveMemory:-}" ] || \
   [ -n "${DOTNET_EnableDiagnostics:-}" ] || \
   [ -n "${GC_LATENCY_MODE:-}" ] || \
   [ -n "${TP_PREWARM:-}" ] || \
   [ -n "${TP_PREFER_LOCAL:-}" ] || \
   [ -n "${TP_MIN_THREADS:-}" ] || \
   [ -n "${TP_MIN_IO_THREADS:-}" ] || \
   [ -n "${TP_MAX_THREADS:-}" ] || \
   [ -n "${TP_MAX_IO_THREADS:-}" ] || \
   [ -n "${KEEP_ALIVE_REQUESTS:-}" ] || \
   [ -n "${KEEP_ALIVE_IDLE_MS:-}" ] || \
   [ -n "${API_CPU:-}" ] || \
   [ -n "${API_MEMORY:-}" ] || \
   [ -n "${API_CPUSET:-}" ] || \
   [ -n "${API1_CPUSET:-}" ] || \
   [ -n "${API2_CPUSET:-}" ] || \
   [ -n "${LB_CPU:-}" ] || \
   [ -n "${LB_MEMORY:-}" ] || \
   [ -n "${LB_CPUSET:-}" ] || \
   [ -n "${TCP_DEFER_ACCEPT:-}" ] || \
   [ -n "${LB_FAST2:-}" ] || \
   [ -n "${LOGGING_NONE:-}" ]; then
  OVERRIDE_FILE="${OVERRIDE_FILE_PATH:-${TMPDIR:-/tmp}/${PROJECT_NAME}.override.yml}"
  {
    echo "services:"
    if [ -n "${LB_CPU:-}" ] || [ -n "${LB_MEMORY:-}" ] || [ -n "${LB_CPUSET:-}" ] || [ -n "${FD_CONTROL_SEQPACKET:-}" ] || [ -n "${FD_CONTROL_PREBUFFER:-}" ] || [ -n "${EPOLL_ET:-}" ] || [ -n "${PIN_FIRST_CPU:-}" ] || [ -n "${TCP_DEFER_ACCEPT:-}" ] || [ -n "${LB_FAST2:-}" ] || [ -n "${LB_SOCKET_BUFFERS:-}" ] || [ -n "${LB_TCP_NODELAY:-}" ] || [ -n "${SOCKET_BUFFER_SIZE:-}" ] || [ -n "${SOCKETS_MOUNT:-}" ] || [ -n "${LOGGING_NONE:-}" ]; then
      echo "  lb:"
      [ -n "${LB_CPUSET:-}" ] && echo "    cpuset: \"$LB_CPUSET\""
      if [ -n "${SOCKETS_MOUNT:-}" ]; then
        echo "    volumes:"
        echo "      - ${SOCKETS_MOUNT}"
      fi
      if [ -n "${FD_CONTROL_SEQPACKET:-}" ] || [ -n "${FD_CONTROL_PREBUFFER:-}" ] || [ -n "${EPOLL_ET:-}" ] || [ -n "${PIN_FIRST_CPU:-}" ] || [ -n "${TCP_DEFER_ACCEPT:-}" ] || [ -n "${LB_FAST2:-}" ] || [ -n "${LB_SOCKET_BUFFERS:-}" ] || [ -n "${LB_TCP_NODELAY:-}" ] || [ -n "${SOCKET_BUFFER_SIZE:-}" ]; then
        echo "    environment:"
        [ -n "${FD_CONTROL_SEQPACKET:-}" ] && echo "      FD_CONTROL_SEQPACKET: \"$FD_CONTROL_SEQPACKET\""
        [ -n "${FD_CONTROL_PREBUFFER:-}" ] && echo "      FD_CONTROL_PREBUFFER: \"$FD_CONTROL_PREBUFFER\""
        [ -n "${EPOLL_ET:-}" ] && echo "      EPOLL_ET: \"$EPOLL_ET\""
        [ -n "${PIN_FIRST_CPU:-}" ] && echo "      PIN_FIRST_CPU: \"$PIN_FIRST_CPU\""
        [ -n "${TCP_DEFER_ACCEPT:-}" ] && echo "      TCP_DEFER_ACCEPT: \"$TCP_DEFER_ACCEPT\""
        [ -n "${LB_FAST2:-}" ] && echo "      LB_FAST2: \"$LB_FAST2\""
        [ -n "${LB_SOCKET_BUFFERS:-}" ] && echo "      LB_SOCKET_BUFFERS: \"$LB_SOCKET_BUFFERS\""
        [ -n "${LB_TCP_NODELAY:-}" ] && echo "      LB_TCP_NODELAY: \"$LB_TCP_NODELAY\""
        [ -n "${SOCKET_BUFFER_SIZE:-}" ] && echo "      SOCKET_BUFFER_SIZE: \"$SOCKET_BUFFER_SIZE\""
      fi
      if [ -n "${LOGGING_NONE:-}" ]; then
        echo "    logging:"
        echo "      driver: \"none\""
      fi
      if [ -n "${LB_CPU:-}" ] || [ -n "${LB_MEMORY:-}" ]; then
        echo "    deploy:"
        echo "      resources:"
        echo "        limits:"
        [ -n "${LB_CPU:-}" ] && echo "          cpus: \"$LB_CPU\""
        [ -n "${LB_MEMORY:-}" ] && echo "          memory: \"$LB_MEMORY\""
      fi
    fi

    for service in api1 api2; do
      service_cpuset="${API_CPUSET:-}"
      if [ "$service" = "api1" ] && [ -n "${API1_CPUSET:-}" ]; then
        service_cpuset="$API1_CPUSET"
      fi
      if [ "$service" = "api2" ] && [ -n "${API2_CPUSET:-}" ]; then
        service_cpuset="$API2_CPUSET"
      fi

      echo "  $service:"
      [ -n "${API_ENTRYPOINT:-}" ] && echo "    entrypoint: [\"$API_ENTRYPOINT\"]"
      [ -n "$service_cpuset" ] && echo "    cpuset: \"$service_cpuset\""
      if [ -n "${SOCKETS_MOUNT:-}" ]; then
        echo "    volumes:"
        echo "      - ${SOCKETS_MOUNT}"
      fi
      echo "    environment:"
      [ -n "${EARLY_CANDIDATES:-}" ] && echo "      EARLY_CANDIDATES: \"$EARLY_CANDIDATES\""
      [ -n "${MIN_CANDIDATES:-}" ] && echo "      MIN_CANDIDATES: \"$MIN_CANDIDATES\""
      [ -n "${MAX_CANDIDATES:-}" ] && echo "      MAX_CANDIDATES: \"$MAX_CANDIDATES\""
      [ -n "${PROFILE_FASTPATH:-}" ] && echo "      PROFILE_FASTPATH: \"$PROFILE_FASTPATH\""
      [ -n "${PROFILE_MIN_COUNT:-}" ] && echo "      PROFILE_MIN_COUNT: \"$PROFILE_MIN_COUNT\""
      [ -n "${PROFILE_LEGIT_MIN_COUNT:-}" ] && echo "      PROFILE_LEGIT_MIN_COUNT: \"$PROFILE_LEGIT_MIN_COUNT\""
      [ -n "${PROFILE_FRAUD_MIN_COUNT:-}" ] && echo "      PROFILE_FRAUD_MIN_COUNT: \"$PROFILE_FRAUD_MIN_COUNT\""
      [ -n "${PROFILE_FRAUD_AMOUNT_MIN:-}" ] && echo "      PROFILE_FRAUD_AMOUNT_MIN: \"$PROFILE_FRAUD_AMOUNT_MIN\""
      [ -n "${PROFILE_FRAUD_LOW_AMOUNT_FASTPATH:-}" ] && echo "      PROFILE_FRAUD_LOW_AMOUNT_FASTPATH: \"$PROFILE_FRAUD_LOW_AMOUNT_FASTPATH\""
      [ -n "${PROFILE_FRAUD_LOW_AMOUNT_KM_HOME_MIN:-}" ] && echo "      PROFILE_FRAUD_LOW_AMOUNT_KM_HOME_MIN: \"$PROFILE_FRAUD_LOW_AMOUNT_KM_HOME_MIN\""
      [ -n "${PROFILE_FRAUD_LOW_AMOUNT_TX24H_MIN:-}" ] && echo "      PROFILE_FRAUD_LOW_AMOUNT_TX24H_MIN: \"$PROFILE_FRAUD_LOW_AMOUNT_TX24H_MIN\""
      [ -n "${PROFILE_FRAUD_MID_AMOUNT_NO_LAST_FASTPATH:-}" ] && echo "      PROFILE_FRAUD_MID_AMOUNT_NO_LAST_FASTPATH: \"$PROFILE_FRAUD_MID_AMOUNT_NO_LAST_FASTPATH\""
      [ -n "${PROFILE_FRAUD_MID_AMOUNT_MIN:-}" ] && echo "      PROFILE_FRAUD_MID_AMOUNT_MIN: \"$PROFILE_FRAUD_MID_AMOUNT_MIN\""
      [ -n "${PROFILE_FRAUD_NO_LAST_ONLY:-}" ] && echo "      PROFILE_FRAUD_NO_LAST_ONLY: \"$PROFILE_FRAUD_NO_LAST_ONLY\""
      [ -n "${PROFILE_DOMINANT_FASTPATH:-}" ] && echo "      PROFILE_DOMINANT_FASTPATH: \"$PROFILE_DOMINANT_FASTPATH\""
      [ -n "${PROFILE_DOMINANT_MIN_COUNT:-}" ] && echo "      PROFILE_DOMINANT_MIN_COUNT: \"$PROFILE_DOMINANT_MIN_COUNT\""
      [ -n "${PROFILE_DOMINANT_MAX_OPPOSITE:-}" ] && echo "      PROFILE_DOMINANT_MAX_OPPOSITE: \"$PROFILE_DOMINANT_MAX_OPPOSITE\""
      [ -n "${PROFILE_DOMINANT_LEGIT_ENABLED:-}" ] && echo "      PROFILE_DOMINANT_LEGIT_ENABLED: \"$PROFILE_DOMINANT_LEGIT_ENABLED\""
      [ -n "${PROFILE_DOMINANT_FRAUD_ENABLED:-}" ] && echo "      PROFILE_DOMINANT_FRAUD_ENABLED: \"$PROFILE_DOMINANT_FRAUD_ENABLED\""
      [ -n "${BUCKET_FASTPATH:-}" ] && echo "      BUCKET_FASTPATH: \"$BUCKET_FASTPATH\""
      [ -n "${BUCKET_LEGIT_MIN_COUNT:-}" ] && echo "      BUCKET_LEGIT_MIN_COUNT: \"$BUCKET_LEGIT_MIN_COUNT\""
      [ -n "${BUCKET_FRAUD_MIN_COUNT:-}" ] && echo "      BUCKET_FRAUD_MIN_COUNT: \"$BUCKET_FRAUD_MIN_COUNT\""
      [ -n "${BUCKET_FRAUD_NO_LAST_ONLY:-}" ] && echo "      BUCKET_FRAUD_NO_LAST_ONLY: \"$BUCKET_FRAUD_NO_LAST_ONLY\""
      [ -n "${EXACT_FALLBACK:-}" ] && echo "      EXACT_FALLBACK: \"$EXACT_FALLBACK\""
      [ -n "${EARLY_EDGE_FALLBACK:-}" ] && echo "      EARLY_EDGE_FALLBACK: \"$EARLY_EDGE_FALLBACK\""
      [ -n "${RISKY_AMOUNT_MIN:-}" ] && echo "      RISKY_AMOUNT_MIN: \"$RISKY_AMOUNT_MIN\""
      [ -n "${RISKY_AMOUNT_MAX:-}" ] && echo "      RISKY_AMOUNT_MAX: \"$RISKY_AMOUNT_MAX\""
      [ -n "${RISKY_INSTALLMENTS_MIN:-}" ] && echo "      RISKY_INSTALLMENTS_MIN: \"$RISKY_INSTALLMENTS_MIN\""
      [ -n "${RISKY_INSTALLMENTS_MAX:-}" ] && echo "      RISKY_INSTALLMENTS_MAX: \"$RISKY_INSTALLMENTS_MAX\""
      [ -n "${RISKY_RATIO_MIN:-}" ] && echo "      RISKY_RATIO_MIN: \"$RISKY_RATIO_MIN\""
      [ -n "${RISKY_KM_HOME_MIN:-}" ] && echo "      RISKY_KM_HOME_MIN: \"$RISKY_KM_HOME_MIN\""
      [ -n "${RISKY_KM_HOME_MAX:-}" ] && echo "      RISKY_KM_HOME_MAX: \"$RISKY_KM_HOME_MAX\""
      [ -n "${RISKY_TX24H_MIN:-}" ] && echo "      RISKY_TX24H_MIN: \"$RISKY_TX24H_MIN\""
      [ -n "${RISKY_TX24H_MAX:-}" ] && echo "      RISKY_TX24H_MAX: \"$RISKY_TX24H_MAX\""
      [ -n "${RISKY_MERCHANT_AVG_MIN:-}" ] && echo "      RISKY_MERCHANT_AVG_MIN: \"$RISKY_MERCHANT_AVG_MIN\""
      [ -n "${RISKY_MERCHANT_AVG_MAX:-}" ] && echo "      RISKY_MERCHANT_AVG_MAX: \"$RISKY_MERCHANT_AVG_MAX\""
      [ -n "${RISKY_COMPACT:-}" ] && echo "      RISKY_COMPACT: \"$RISKY_COMPACT\""
      [ -n "${RISKY_FINE_BUCKETS:-}" ] && echo "      RISKY_FINE_BUCKETS: \"$RISKY_FINE_BUCKETS\""
      [ -n "${RISKY_SIMD:-}" ] && echo "      RISKY_SIMD: \"$RISKY_SIMD\""
      [ -n "${RISKY_NATIVE_FINE:-}" ] && echo "      RISKY_NATIVE_FINE: \"$RISKY_NATIVE_FINE\""
      [ -n "${NATIVE_ANN:-}" ] && echo "      NATIVE_ANN: \"$NATIVE_ANN\""
      [ -n "${NATIVE_ANN_DIRECT:-}" ] && echo "      NATIVE_ANN_DIRECT: \"$NATIVE_ANN_DIRECT\""
      [ -n "${KDTREE_MAX_PARTITIONS:-}" ] && echo "      KDTREE_MAX_PARTITIONS: \"$KDTREE_MAX_PARTITIONS\""
      [ -n "${BLOCK_SCAN:-}" ] && echo "      BLOCK_SCAN: \"$BLOCK_SCAN\""
      [ -n "${WORKERS:-}" ] && echo "      WORKERS: \"$WORKERS\""
      [ -n "${NATIVE_WORKERS:-}" ] && echo "      NATIVE_WORKERS: \"$NATIVE_WORKERS\""
      [ -n "${FD_RECEIVERS:-}" ] && echo "      FD_RECEIVERS: \"$FD_RECEIVERS\""
      [ -n "${FD_RAW:-}" ] && echo "      FD_RAW: \"$FD_RAW\""
      [ -n "${FD_DEDICATED_THREADS:-}" ] && echo "      FD_DEDICATED_THREADS: \"$FD_DEDICATED_THREADS\""
      [ -n "${FD_THREAD_STACK_KB:-}" ] && echo "      FD_THREAD_STACK_KB: \"$FD_THREAD_STACK_KB\""
      [ -n "${FD_EPOLL:-}" ] && echo "      FD_EPOLL: \"$FD_EPOLL\""
      [ -n "${TCP_QUICKACK:-}" ] && echo "      TCP_QUICKACK: \"$TCP_QUICKACK\""
      [ -n "${NATIVE_EPOLL:-}" ] && echo "      NATIVE_EPOLL: \"$NATIVE_EPOLL\""
      [ -n "${FD_IMMEDIATE_READ:-}" ] && echo "      FD_IMMEDIATE_READ: \"$FD_IMMEDIATE_READ\""
      [ -n "${FD_PRE_READ:-}" ] && echo "      FD_PRE_READ: \"$FD_PRE_READ\""
      [ -n "${ASSUME_BODY_COMPLETE:-}" ] && echo "      ASSUME_BODY_COMPLETE: \"$ASSUME_BODY_COMPLETE\""
      [ -n "${ASSUME_FRAUD_SCORE_PATH:-}" ] && echo "      ASSUME_FRAUD_SCORE_PATH: \"$ASSUME_FRAUD_SCORE_PATH\""
      [ -n "${ASSUME_JSON_BODY_START:-}" ] && echo "      ASSUME_JSON_BODY_START: \"$ASSUME_JSON_BODY_START\""
      [ -n "${EPOLL_ET:-}" ] && echo "      EPOLL_ET: \"$EPOLL_ET\""
      [ -n "${PIN_FIRST_CPU:-}" ] && echo "      PIN_FIRST_CPU: \"$PIN_FIRST_CPU\""
      [ -n "${FD_CONTROL_SEQPACKET:-}" ] && echo "      FD_CONTROL_SEQPACKET: \"$FD_CONTROL_SEQPACKET\""
      [ -n "${FD_CONTROL_PREBUFFER:-}" ] && echo "      FD_CONTROL_PREBUFFER: \"$FD_CONTROL_PREBUFFER\""
      [ -n "${REFERENCE_FASTPATH_FRAUD_ONLY:-}" ] && echo "      REFERENCE_FASTPATH_FRAUD_ONLY: \"$REFERENCE_FASTPATH_FRAUD_ONLY\""
      [ -n "${REFERENCE_FASTPATH_FRAUD_MIN_COUNT:-}" ] && echo "      REFERENCE_FASTPATH_FRAUD_MIN_COUNT: \"$REFERENCE_FASTPATH_FRAUD_MIN_COUNT\""
      [ -n "${SERVER_MODE:-}" ] && echo "      SERVER_MODE: \"$SERVER_MODE\""
      [ -n "${INDEX_HUGEPAGES:-}" ] && echo "      INDEX_HUGEPAGES: \"$INDEX_HUGEPAGES\""
      [ -n "${MALLOC_ARENA_MAX:-}" ] && echo "      MALLOC_ARENA_MAX: \"$MALLOC_ARENA_MAX\""
      [ -n "${DOTNET_PROCESSOR_COUNT:-}" ] && echo "      DOTNET_PROCESSOR_COUNT: \"$DOTNET_PROCESSOR_COUNT\""
      [ -n "${DOTNET_GCHeapCount:-}" ] && echo "      DOTNET_GCHeapCount: \"$DOTNET_GCHeapCount\""
      [ -n "${DOTNET_ThreadPool_UnfairSemaphoreSpinLimit:-}" ] && echo "      DOTNET_ThreadPool_UnfairSemaphoreSpinLimit: \"$DOTNET_ThreadPool_UnfairSemaphoreSpinLimit\""
      [ -n "${DOTNET_GCConserveMemory:-}" ] && echo "      DOTNET_GCConserveMemory: \"$DOTNET_GCConserveMemory\""
      [ -n "${DOTNET_EnableDiagnostics:-}" ] && echo "      DOTNET_EnableDiagnostics: \"$DOTNET_EnableDiagnostics\""
      [ -n "${GC_LATENCY_MODE:-}" ] && echo "      GC_LATENCY_MODE: \"$GC_LATENCY_MODE\""
      [ -n "${TP_PREWARM:-}" ] && echo "      TP_PREWARM: \"$TP_PREWARM\""
      [ -n "${TP_PREFER_LOCAL:-}" ] && echo "      TP_PREFER_LOCAL: \"$TP_PREFER_LOCAL\""
      [ -n "${TP_MIN_THREADS:-}" ] && echo "      TP_MIN_THREADS: \"$TP_MIN_THREADS\""
      [ -n "${TP_MIN_IO_THREADS:-}" ] && echo "      TP_MIN_IO_THREADS: \"$TP_MIN_IO_THREADS\""
      [ -n "${TP_MAX_THREADS:-}" ] && echo "      TP_MAX_THREADS: \"$TP_MAX_THREADS\""
      [ -n "${TP_MAX_IO_THREADS:-}" ] && echo "      TP_MAX_IO_THREADS: \"$TP_MAX_IO_THREADS\""
      [ -n "${KEEP_ALIVE_REQUESTS:-}" ] && echo "      KEEP_ALIVE_REQUESTS: \"$KEEP_ALIVE_REQUESTS\""
      [ -n "${KEEP_ALIVE_IDLE_MS:-}" ] && echo "      KEEP_ALIVE_IDLE_MS: \"$KEEP_ALIVE_IDLE_MS\""
      if [ -n "${LOGGING_NONE:-}" ]; then
        echo "    logging:"
        echo "      driver: \"none\""
      fi

      if [ -n "${API_CPU:-}" ] || [ -n "${API_MEMORY:-}" ]; then
        echo "    deploy:"
        echo "      resources:"
        echo "        limits:"
        [ -n "${API_CPU:-}" ] && echo "          cpus: \"$API_CPU\""
        [ -n "${API_MEMORY:-}" ] && echo "          memory: \"$API_MEMORY\""
      fi
    done
  } > "$OVERRIDE_FILE"
fi

if [ "$PULL" = "1" ] || [ "$MODE" = "submission" ]; then
  compose down --remove-orphans -v >/dev/null 2>&1 || true
  compose pull
fi

if [ "$MODE" = "build" ]; then
  compose down --remove-orphans -v >/dev/null 2>&1 || true
  compose up -d --build --remove-orphans
else
  compose down --remove-orphans -v >/dev/null 2>&1 || true
  compose up -d --remove-orphans
fi

ready=0
for _ in $(seq 1 90); do
  if curl -fsS "http://127.0.0.1:9999/ready" >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 1
done

if [ "$ready" != "1" ]; then
  echo "backend did not become ready on http://127.0.0.1:9999/ready" >&2
  exit 1
fi

docker run --rm \
  --network "${PROJECT_NAME}_default" \
  -e BASE_URL="http://lb:9999" \
  -e RESULTS_PATH="/scripts/results.json" \
  -e TARGET_RATE \
  -e RAMP_DURATION \
  -e START_RATE \
  -e PRE_ALLOCATED_VUS \
  -e MAX_VUS \
  -e REQUEST_TIMEOUT \
  -e DUMP_MISMATCHES \
  -v "$TEST_MOUNT:/scripts" \
  "$K6_IMAGE" run /scripts/rinha-test.js
