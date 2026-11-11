#!/usr/bin/env bash
# ==============================================================================
# Automated Browser Smoke Verification using agent-browser
# Verifies live WASM rendering, splash screen dismissal, navigation across all routes,
# absence of unhandled UI errors, and zero uncaught JavaScript errors.
# Single source of truth for browser smoke verification
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Optional clean restart
if [[ "$*" == *"--clean"* ]]; then
  export CLEAN_SERVER=1
fi

# Delegate DevServer startup, readiness check, and teardown to run-with-server.sh
if [ "${RUN_WITH_SERVER_ACTIVE:-0}" != "1" ]; then
  export CONFIGURATION="${CONFIGURATION:-Release}"
  exec "${SCRIPT_DIR}/run-with-server.sh" "$0" "$@"
fi

REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
ARTIFACTS_DIR="${ARTIFACTS_DIR:-${REPO_ROOT}/artifacts}"
PORT="${PORT:-5139}"
HOST="${HOST:-127.0.0.1}"
BASE_URL="${BASE_URL:-http://${HOST}:${PORT}}"

mkdir -p "${ARTIFACTS_DIR}"

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
else
  echo "Error: agent-browser CLI not found. Please install via: npm i -g agent-browser or pnpm add -g agent-browser" >&2
  exit 1
fi

check_page_errors() {
  local route_name="$1"
  local error_display
  error_display="$("${AGENT_BROWSER[@]}" eval 'const el = document.getElementById("blazor-error-ui"); el ? getComputedStyle(el).display : "none"' 2>/dev/null || echo "none")"
  if [ "${error_display}" = "block" ]; then
    echo "Error: #blazor-error-ui is displayed on ${route_name}." >&2
    exit 1
  fi

  local js_errors
  js_errors="$("${AGENT_BROWSER[@]}" errors 2>/dev/null || true)"
  if [ -n "${js_errors}" ]; then
    echo "Error: Uncaught JavaScript errors on ${route_name}: ${js_errors}" >&2
    exit 1
  fi
}

verify_route() {
  local path="$1"
  local screenshot_name="$2"
  local snapshot_flag="$3"
  shift 3

  echo "==> Navigating to ${path}..."
  "${AGENT_BROWSER[@]}" open "${BASE_URL}${path}"

  local ready=0
  local snap=""
  for i in $(seq 1 10); do
    "${AGENT_BROWSER[@]}" wait 1000
    if [ -n "${snapshot_flag}" ]; then
      snap="$("${AGENT_BROWSER[@]}" snapshot "${snapshot_flag}" 2>/dev/null || true)"
    else
      snap="$("${AGENT_BROWSER[@]}" snapshot 2>/dev/null || true)"
    fi
    for expected in "$@"; do
      if [[ "${snap}" == *"${expected}"* ]]; then
        ready=1
        break 2
      fi
    done
  done

  if [ "${ready}" -ne 1 ]; then
    echo "Error: ${path} page did not render expected content within 10s." >&2
    echo "${snap}"
    exit 1
  fi

  check_page_errors "${path}"
  "${AGENT_BROWSER[@]}" screenshot "${ARTIFACTS_DIR}/${screenshot_name}"
}

echo "==> Launching agent-browser smoke test on ${BASE_URL}..."
"${AGENT_BROWSER[@]}" open "${BASE_URL}"

# Waiting for Blazor WASM initialization and splash dismissal...
READY_WASM=0
SNAPSHOT=""
for i in $(seq 1 15); do
  "${AGENT_BROWSER[@]}" wait 1000
  SNAPSHOT="$("${AGENT_BROWSER[@]}" snapshot -i 2>/dev/null || true)"
  if [[ "${SNAPSHOT}" == *"Weather"* ]] && [[ "${SNAPSHOT}" == *"Map"* ]]; then
    READY_WASM=1
    break
  fi
done

PAGE_TITLE="$("${AGENT_BROWSER[@]}" get title)"
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
"${AGENT_BROWSER[@]}" screenshot "${SCREENSHOT_PATH}"
echo "==> Verification screenshot saved to: ${SCREENSHOT_PATH}"

verify_route "/weather"         "smoke-weather.png"  "-i" "Weather"
verify_route "/map"             "smoke-map.png"      "-i" "Transit Map" "Provider"
verify_route "/fortune"         "smoke-fortune.png"  "-i" "Fortune"
verify_route "/privacy"         "smoke-privacy.png"  ""   "Privacy Policy"
verify_route "/google/calendar" "smoke-calendar.png" ""   "Calendar" "Sign in"
verify_route "/google/tasks"    "smoke-tasks.png"    ""   "Tasks" "Sign in"

echo "✓ Full Automated Browser Smoke Verification PASSED successfully!"
