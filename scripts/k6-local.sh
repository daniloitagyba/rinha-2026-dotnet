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
  if [ -n "$OVERRIDE_FILE" ]; then
    docker compose -p "$PROJECT_NAME" -f "$COMPOSE_FILE" -f "$OVERRIDE_FILE" "$@"
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
   [ -n "${EXACT_FALLBACK:-}" ] || \
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
   [ -n "${SOCKETS_MOUNT:-}" ] || \
   [ -n "${WORKERS:-}" ] || \
   [ -n "${SERVER_MODE:-}" ] || \
   [ -n "${TP_MIN_THREADS:-}" ] || \
   [ -n "${KEEP_ALIVE_REQUESTS:-}" ] || \
   [ -n "${KEEP_ALIVE_IDLE_MS:-}" ] || \
   [ -n "${API_CPU:-}" ] || \
   [ -n "${API_MEMORY:-}" ] || \
   [ -n "${LB_CPU:-}" ] || \
   [ -n "${LB_MEMORY:-}" ]; then
  OVERRIDE_FILE="${OVERRIDE_FILE_PATH:-${TMPDIR:-/tmp}/${PROJECT_NAME}.override.yml}"
  {
    echo "services:"
    if [ -n "${LB_CPU:-}" ] || [ -n "${LB_MEMORY:-}" ]; then
      echo "  lb:"
      echo "    deploy:"
      echo "      resources:"
      echo "        limits:"
      [ -n "${LB_CPU:-}" ] && echo "          cpus: \"$LB_CPU\""
      [ -n "${LB_MEMORY:-}" ] && echo "          memory: \"$LB_MEMORY\""
    fi

    if [ -n "${SOCKETS_MOUNT:-}" ]; then
      for service in lb api1 api2; do
        echo "  $service:"
        echo "    volumes:"
        echo "      - ${SOCKETS_MOUNT}"
      done
    fi

    for service in api1 api2; do
      echo "  $service:"
      echo "    environment:"
      [ -n "${EARLY_CANDIDATES:-}" ] && echo "      EARLY_CANDIDATES: \"$EARLY_CANDIDATES\""
      [ -n "${MIN_CANDIDATES:-}" ] && echo "      MIN_CANDIDATES: \"$MIN_CANDIDATES\""
      [ -n "${MAX_CANDIDATES:-}" ] && echo "      MAX_CANDIDATES: \"$MAX_CANDIDATES\""
      [ -n "${PROFILE_FASTPATH:-}" ] && echo "      PROFILE_FASTPATH: \"$PROFILE_FASTPATH\""
      [ -n "${PROFILE_MIN_COUNT:-}" ] && echo "      PROFILE_MIN_COUNT: \"$PROFILE_MIN_COUNT\""
      [ -n "${PROFILE_LEGIT_MIN_COUNT:-}" ] && echo "      PROFILE_LEGIT_MIN_COUNT: \"$PROFILE_LEGIT_MIN_COUNT\""
      [ -n "${PROFILE_FRAUD_MIN_COUNT:-}" ] && echo "      PROFILE_FRAUD_MIN_COUNT: \"$PROFILE_FRAUD_MIN_COUNT\""
      [ -n "${EXACT_FALLBACK:-}" ] && echo "      EXACT_FALLBACK: \"$EXACT_FALLBACK\""
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
      [ -n "${WORKERS:-}" ] && echo "      WORKERS: \"$WORKERS\""
      [ -n "${SERVER_MODE:-}" ] && echo "      SERVER_MODE: \"$SERVER_MODE\""
      [ -n "${TP_MIN_THREADS:-}" ] && echo "      TP_MIN_THREADS: \"$TP_MIN_THREADS\""
      [ -n "${KEEP_ALIVE_REQUESTS:-}" ] && echo "      KEEP_ALIVE_REQUESTS: \"$KEEP_ALIVE_REQUESTS\""
      [ -n "${KEEP_ALIVE_IDLE_MS:-}" ] && echo "      KEEP_ALIVE_IDLE_MS: \"$KEEP_ALIVE_IDLE_MS\""

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
  -v "$TEST_MOUNT:/scripts" \
  "$K6_IMAGE" run /scripts/rinha-test.js
