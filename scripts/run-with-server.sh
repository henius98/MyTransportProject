#!/usr/bin/env bash
# ==============================================================================
# Helper to execute any command with an automatically managed Blazor DevServer.
# Ensures the server starts, waits for readiness, runs the command, and always
# cleanly tears down the server and browser on exit.
# Single source of truth for ad-hoc browser & integration task lifecycle.
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
ARTIFACTS_DIR="${REPO_ROOT}/artifacts"
PORT="${PORT:-5139}"
HOST="${HOST:-127.0.0.1}"
BASE_URL="http://${HOST}:${PORT}"
CONFIGURATION="${CONFIGURATION:-Debug}"
SERVER_PID=""
SERVER_LOG="${SERVER_LOG:-${ARTIFACTS_DIR}/devserver.log}"
export PORT HOST BASE_URL ARTIFACTS_DIR
export RUN_WITH_SERVER_ACTIVE=1

mkdir -p "${ARTIFACTS_DIR}"

kill_port() {
  local port="$1"
  if command -v fuser >/dev/null 2>&1; then
    fuser -k "${port}/tcp" >/dev/null 2>&1 || true
  elif command -v lsof >/dev/null 2>&1; then
    lsof -ti ":${port}" | xargs -r kill -9 >/dev/null 2>&1 || true
  else
    pkill -f "urls.*:${port}" >/dev/null 2>&1 || true
  fi
}

is_server_ready() {
  local url="$1"
  if command -v curl >/dev/null 2>&1; then
    curl -sI "${url}" >/dev/null 2>&1
  elif command -v wget >/dev/null 2>&1; then
    wget -q --spider "${url}" >/dev/null 2>&1
  elif command -v python3 >/dev/null 2>&1; then
    python3 -c "import urllib.request, sys; sys.exit(0 if urllib.request.urlopen('${url}', timeout=2) else 1)" >/dev/null 2>&1
  else
    return 1
  fi
}

# Resolve agent-browser executable portably across local environments and CI containers
AGENT_BROWSER=()
if command -v agent-browser >/dev/null 2>&1; then
  AGENT_BROWSER=("agent-browser")
elif [ -n "${HOME:-}" ] && [ -x "${HOME}/.local/share/pnpm/bin/agent-browser" ]; then
  AGENT_BROWSER=("${HOME}/.local/share/pnpm/bin/agent-browser")
elif command -v pnpm >/dev/null 2>&1 && pnpm exec agent-browser --version >/dev/null 2>&1; then
  AGENT_BROWSER=("pnpm" "exec" "agent-browser")
elif command -v npx >/dev/null 2>&1; then
  AGENT_BROWSER=("npx" "agent-browser")
fi

cleanup() {
  local exit_code=$?
  echo "==> Cleaning up test environment..."
  if [ "${#AGENT_BROWSER[@]}" -gt 0 ]; then
    "${AGENT_BROWSER[@]}" close >/dev/null 2>&1 || true
  fi
  if [ -n "${SERVER_PID}" ]; then
    echo "==> Stopping test server (PID: ${SERVER_PID})..."
    pkill -P "${SERVER_PID}" >/dev/null 2>&1 || true
    kill "${SERVER_PID}" >/dev/null 2>&1 || true
    wait "${SERVER_PID}" >/dev/null 2>&1 || true
    kill_port "${PORT}"
  fi
  exit "${exit_code}"
}
trap cleanup EXIT INT TERM

# Optional clean restart
if [ "${1:-}" = "--clean" ]; then
  shift
  CLEAN_SERVER=1
fi
if [ "${CLEAN_SERVER:-0}" = "1" ]; then
  echo "==> Cleaning stale server on port ${PORT}..."
  kill_port "${PORT}"
  sleep 0.5
fi

if [ $# -eq 0 ]; then
  echo "Usage: $0 [--clean] <command> [args...]" >&2
  echo "Example: $0 agent-browser open ${BASE_URL}" >&2
  exit 1
fi

if is_server_ready "${BASE_URL}"; then
  echo "==> Server already running on ${BASE_URL}."
else
  echo "==> Starting Blazor DevServer (${CONFIGURATION}) on ${BASE_URL}..."
  dotnet run --project "${REPO_ROOT}/MyTransportAppWASM" -c "${CONFIGURATION}" --no-launch-profile --urls "${BASE_URL}" > "${SERVER_LOG}" 2>&1 &
  SERVER_PID=$!

  echo "==> Waiting for server readiness..."
  READY=0
  for i in $(seq 1 40); do
    if is_server_ready "${BASE_URL}"; then
      READY=1
      echo "==> Server is ready."
      break
    fi
    sleep 0.5
  done

  if [ "${READY}" -ne 1 ]; then
    echo "Error: DevServer failed to become ready within 20s." >&2
    if [ -f "${SERVER_LOG}" ]; then
      cat "${SERVER_LOG}" >&2
    fi
    exit 1
  fi
fi

echo "==> Executing: $*"
"$@"
