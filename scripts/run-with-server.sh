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
HOST="127.0.0.1"
BASE_URL="http://${HOST}:${PORT}"
CONFIGURATION="${CONFIGURATION:-Debug}"
SERVER_PID=""
SERVER_LOG="${ARTIFACTS_DIR}/runserver.log"

mkdir -p "${ARTIFACTS_DIR}"

cleanup() {
  local exit_code=$?
  echo "==> Cleaning up test environment..."
  if command -v agent-browser >/dev/null 2>&1; then
    agent-browser close >/dev/null 2>&1 || true
  fi
  if [ -n "${SERVER_PID}" ]; then
    echo "==> Stopping test server (PID: ${SERVER_PID})..."
    pkill -P "${SERVER_PID}" >/dev/null 2>&1 || true
    kill "${SERVER_PID}" >/dev/null 2>&1 || true
    wait "${SERVER_PID}" >/dev/null 2>&1 || true
    if command -v fuser >/dev/null 2>&1; then
      fuser -k "${PORT}/tcp" >/dev/null 2>&1 || true
    fi
  fi
  exit "${exit_code}"
}
trap cleanup EXIT INT TERM

if [ $# -eq 0 ]; then
  echo "Usage: $0 <command> [args...]" >&2
  echo "Example: $0 agent-browser open ${BASE_URL}" >&2
  exit 1
fi

if curl -sI "${BASE_URL}" >/dev/null 2>&1; then
  echo "==> Server already running on ${BASE_URL}."
else
  echo "==> Starting Blazor DevServer (${CONFIGURATION}) on ${BASE_URL}..."
  dotnet run --project "${REPO_ROOT}/MyTransportAppWASM" -c "${CONFIGURATION}" --no-launch-profile --urls "${BASE_URL}" > "${SERVER_LOG}" 2>&1 &
  SERVER_PID=$!

  echo "==> Waiting for server readiness..."
  READY=0
  for i in $(seq 1 40); do
    if curl -sI "${BASE_URL}" >/dev/null 2>&1; then
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
