#!/usr/bin/env bash
# ==============================================================================
# Automated Browser Smoke Verification using agent-browser
# Verifies live WASM rendering, splash screen dismissal, navigation across all routes,
# absence of unhandled UI errors, and zero uncaught JavaScript errors.
# Single source of truth for browser smoke verification
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
ARTIFACTS_DIR="${REPO_ROOT}/artifacts"
PORT="${PORT:-5139}"
HOST="127.0.0.1"
BASE_URL="http://${HOST}:${PORT}"
CONFIGURATION="${CONFIGURATION:-Release}"
SERVER_PID=""
SERVER_LOG="${ARTIFACTS_DIR}/devserver.log"

mkdir -p "${ARTIFACTS_DIR}"

# Optional clean restart
if [ "${CLEAN_SERVER:-0}" = "1" ] || [[ "$*" == *"--clean"* ]]; then
  echo "==> Cleaning stale server on port ${PORT}..."
  if command -v fuser >/dev/null 2>&1; then
    fuser -k "${PORT}/tcp" >/dev/null 2>&1 || true
  fi
  sleep 0.5
fi

# Resolve agent-browser executable portably across local environments and CI containers
if command -v agent-browser >/dev/null 2>&1; then
  AGENT_BROWSER="agent-browser"
elif command -v pnpm >/dev/null 2>&1 && pnpm exec agent-browser --version >/dev/null 2>&1; then
  AGENT_BROWSER="pnpm exec agent-browser"
elif [ -n "${HOME:-}" ] && [ -x "${HOME}/.local/share/pnpm/bin/agent-browser" ]; then
  AGENT_BROWSER="${HOME}/.local/share/pnpm/bin/agent-browser"
elif command -v npx >/dev/null 2>&1; then
  AGENT_BROWSER="npx agent-browser"
else
  echo "Error: agent-browser CLI not found. Please install via: npm i -g agent-browser or pnpm add -g agent-browser" >&2
  exit 1
fi

cleanup() {
  local exit_code=$?
  echo "==> Cleaning up smoke test..."
  "${AGENT_BROWSER}" close >/dev/null 2>&1 || true
  if [ -n "${SERVER_PID}" ]; then
    echo "Stopping test server (PID: ${SERVER_PID})..."
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

# Check if server is already running on the target port
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

check_page_errors() {
  local route_name="$1"
  local error_display
  error_display="$("${AGENT_BROWSER}" eval 'getComputedStyle(document.getElementById("blazor-error-ui")).display' 2>/dev/null || echo "none")"
  if [ "${error_display}" = "block" ]; then
    echo "Error: #blazor-error-ui is displayed on ${route_name}." >&2
    exit 1
  fi

  local js_errors
  js_errors="$("${AGENT_BROWSER}" errors 2>/dev/null || true)"
  if [ -n "${js_errors}" ]; then
    echo "Error: Uncaught JavaScript errors on ${route_name}: ${js_errors}" >&2
    exit 1
  fi
}

echo "==> Launching agent-browser smoke test on ${BASE_URL}..."
"${AGENT_BROWSER}" open "${BASE_URL}"

# Waiting for Blazor WASM initialization and splash dismissal...
READY_WASM=0
SNAPSHOT=""
for i in $(seq 1 15); do
  "${AGENT_BROWSER}" wait 1000
  SNAPSHOT="$("${AGENT_BROWSER}" snapshot -i 2>/dev/null || true)"
  if [[ "${SNAPSHOT}" == *"Weather"* ]] && [[ "${SNAPSHOT}" == *"Map"* ]]; then
    READY_WASM=1
    break
  fi
done

PAGE_TITLE="$("${AGENT_BROWSER}" get title)"
echo "Page title: ${PAGE_TITLE}"
if [[ "${PAGE_TITLE}" != *"MyTransport"* ]]; then
  echo "Error: Unexpected page title '${PAGE_TITLE}' (expected to contain 'MyTransport')" >&2
  exit 1
fi

if [ "${READY_WASM}" -ne 1 ]; then
  echo "Error: Expected navigation elements (Weather, Map) not found in interactive snapshot within 15s." >&2
  echo "${SNAPSHOT}"
  exit 1
fi

check_page_errors "Home"

# Primary verification screenshot for CI artifact collection
SCREENSHOT_PATH="${ARTIFACTS_DIR}/smoke-verification.png"
"${AGENT_BROWSER}" screenshot "${SCREENSHOT_PATH}"
echo "==> Verification screenshot saved to: ${SCREENSHOT_PATH}"

echo "==> Navigating to /weather..."
"${AGENT_BROWSER}" open "${BASE_URL}/weather"
WEATHER_READY=0
WEATHER_SNAPSHOT=""
for i in $(seq 1 10); do
  "${AGENT_BROWSER}" wait 1000
  WEATHER_SNAPSHOT="$("${AGENT_BROWSER}" snapshot -i 2>/dev/null || true)"
  if [[ "${WEATHER_SNAPSHOT}" == *"Weather"* ]]; then
    WEATHER_READY=1
    break
  fi
done

if [ "${WEATHER_READY}" -ne 1 ]; then
  echo "Error: Weather page did not render expected content within 10s." >&2
  echo "${WEATHER_SNAPSHOT}"
  exit 1
fi
check_page_errors "/weather"
"${AGENT_BROWSER}" screenshot "${ARTIFACTS_DIR}/smoke-weather.png"

echo "==> Navigating to /map..."
"${AGENT_BROWSER}" open "${BASE_URL}/map"
MAP_READY=0
MAP_SNAPSHOT=""
for i in $(seq 1 10); do
  "${AGENT_BROWSER}" wait 1000
  MAP_SNAPSHOT="$("${AGENT_BROWSER}" snapshot -i 2>/dev/null || true)"
  if [[ "${MAP_SNAPSHOT}" == *"Transit Map"* ]] || [[ "${MAP_SNAPSHOT}" == *"Provider"* ]]; then
    MAP_READY=1
    break
  fi
done

if [ "${MAP_READY}" -ne 1 ]; then
  echo "Error: Map page did not render expected content within 10s." >&2
  echo "${MAP_SNAPSHOT}"
  exit 1
fi
check_page_errors "/map"
"${AGENT_BROWSER}" screenshot "${ARTIFACTS_DIR}/smoke-map.png"

echo "==> Navigating to /fortune..."
"${AGENT_BROWSER}" open "${BASE_URL}/fortune"
FORTUNE_READY=0
FORTUNE_SNAPSHOT=""
for i in $(seq 1 10); do
  "${AGENT_BROWSER}" wait 1000
  FORTUNE_SNAPSHOT="$("${AGENT_BROWSER}" snapshot -i 2>/dev/null || true)"
  if [[ "${FORTUNE_SNAPSHOT}" == *"Fortune"* ]]; then
    FORTUNE_READY=1
    break
  fi
done

if [ "${FORTUNE_READY}" -ne 1 ]; then
  echo "Error: Fortune page did not render expected content within 10s." >&2
  echo "${FORTUNE_SNAPSHOT}"
  exit 1
fi
check_page_errors "/fortune"
"${AGENT_BROWSER}" screenshot "${ARTIFACTS_DIR}/smoke-fortune.png"

echo "==> Navigating to /privacy..."
"${AGENT_BROWSER}" open "${BASE_URL}/privacy"
PRIVACY_READY=0
PRIVACY_SNAPSHOT=""
for i in $(seq 1 10); do
  "${AGENT_BROWSER}" wait 1000
  PRIVACY_SNAPSHOT="$("${AGENT_BROWSER}" snapshot 2>/dev/null || true)"
  if [[ "${PRIVACY_SNAPSHOT}" == *"Privacy Policy"* ]]; then
    PRIVACY_READY=1
    break
  fi
done

if [ "${PRIVACY_READY}" -ne 1 ]; then
  echo "Error: Privacy page did not render expected content within 10s." >&2
  echo "${PRIVACY_SNAPSHOT}"
  exit 1
fi
check_page_errors "/privacy"
"${AGENT_BROWSER}" screenshot "${ARTIFACTS_DIR}/smoke-privacy.png"

echo "✓ Full Automated Browser Smoke Verification PASSED successfully!"
