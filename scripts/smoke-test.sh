#!/usr/bin/env bash
# ==============================================================================
# Automated Browser Smoke Verification using agent-browser
# Verifies live WASM rendering, splash screen dismissal, navigation, and absence of errors.
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
  echo "==> Cleaning up smoke test..."
  "${AGENT_BROWSER}" close >/dev/null 2>&1 || true
  if [ -n "${SERVER_PID}" ]; then
    echo "Stopping test server (PID: ${SERVER_PID})..."
    pkill -P "${SERVER_PID}" >/dev/null 2>&1 || true
    kill "${SERVER_PID}" >/dev/null 2>&1 || true
    wait "${SERVER_PID}" >/dev/null 2>&1 || true
  fi
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

# Verify page title
PAGE_TITLE="$("${AGENT_BROWSER}" get title)"
echo "Page title: ${PAGE_TITLE}"
if [[ "${PAGE_TITLE}" != *"MyTransport"* ]]; then
  echo "Error: Unexpected page title '${PAGE_TITLE}' (expected to contain 'MyTransport')" >&2
  exit 1
fi

# Verify interactive navigation elements
if [ "${READY_WASM}" -ne 1 ]; then
  echo "Error: Expected navigation elements (Weather, Map) not found in interactive snapshot within 15s." >&2
  echo "${SNAPSHOT}"
  exit 1
fi

# Verify error UI is not shown
ERROR_DISPLAY="$("${AGENT_BROWSER}" eval 'getComputedStyle(document.getElementById("blazor-error-ui")).display')"
if [ "${ERROR_DISPLAY}" = "block" ]; then
  echo "Error: #blazor-error-ui is displayed on the page." >&2
  exit 1
fi

# Take verification screenshot
SCREENSHOT_PATH="${ARTIFACTS_DIR}/smoke-verification.png"
"${AGENT_BROWSER}" screenshot "${SCREENSHOT_PATH}"
echo "==> Verification screenshot saved to: ${SCREENSHOT_PATH}"

echo "==> Navigating to /weather to verify client-side routing..."
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

"${AGENT_BROWSER}" screenshot "${ARTIFACTS_DIR}/smoke-weather.png"

echo "✓ Automated Browser Smoke Verification PASSED successfully!"
