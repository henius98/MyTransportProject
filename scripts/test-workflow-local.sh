#!/usr/bin/env bash
# ==============================================================================
# Run and validate GitHub Actions workflows locally using wrkflw
# https://github.com/bahdotsh/wrkflw
# Single source of truth for local workflow testing
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

# Ensure wrkflw is in PATH
if ! command -v wrkflw >/dev/null 2>&1; then
  for candidate in \
    "/var/home/linuxbrew/.linuxbrew/bin" \
    "${HOME}/.linuxbrew/bin" \
    "${HOME}/.cargo/bin" \
    "${HOME}/.local/bin"; do
    if [ -x "${candidate}/wrkflw" ]; then
      export PATH="${candidate}:${PATH}"
      break
    fi
  done
  if ! command -v wrkflw >/dev/null 2>&1; then
    echo "Error: wrkflw CLI not found." >&2
    echo "Please install via: brew install wrkflw  OR  cargo install wrkflw" >&2
    exit 1
  fi
fi

load_env_file() {
  local file="$1"
  [ -f "${file}" ] || return 0
  # Export non-comment lines
  while IFS= read -r line || [ -n "${line}" ]; do
    line="${line#"${line%%[![:space:]]*}"}"
    if [ -n "${line}" ] && [[ ! "${line}" =~ ^# ]] && [[ "${line}" == *"="* ]]; then
      local key="${line%%=*}"
      local val="${line#*=}"
      # Strip outer matching quotes if present
      if [[ "${val}" =~ ^\"(.*)\"$ ]] || [[ "${val}" =~ ^\'(.*)\'$ ]]; then
        val="${BASH_REMATCH[1]}"
      fi
      export "${key}=${val}"
    fi
  done < "${file}"
}

# Load local secrets if present
if [ -f "${REPO_ROOT}/.secrets" ]; then
  load_env_file "${REPO_ROOT}/.secrets"
elif [ -f "${REPO_ROOT}/.secrets.example" ]; then
  echo "Notice: No .secrets file found. You can copy .secrets.example to .secrets to supply local secrets."
fi

# Load local workflow vars if present
if [ -f "${REPO_ROOT}/.vars" ]; then
  load_env_file "${REPO_ROOT}/.vars"
fi

# Default fallback values for required workflow variables/secrets
export GOOGLE_MAPS_API_KEY="${GOOGLE_MAPS_API_KEY:-dummy_key}"
export FIREBASE_CONFIG_JSON="${FIREBASE_CONFIG_JSON:-{\"apiKey\":\"dummyApiKey\",\"authDomain\":\"mytransportapp.firebaseapp.com\",\"projectId\":\"mytransportapp\",\"storageBucket\":\"mytransportapp.appspot.com\",\"messagingSenderId\":\"123456789\",\"appId\":\"1:123456789:web:abcdef\"}}"

cd "${REPO_ROOT}"

# If no arguments are passed, validate workflows and list them
if [ $# -eq 0 ]; then
  echo "==> Validating workflows..."
  wrkflw validate
  echo ""
  echo "==> Workflows found:"
  wrkflw list
  echo ""
  echo "Tip: Run a workflow with:"
  echo "  $0 run .github/workflows/deploy.yml --runtime emulation"
  echo "  $0 run .github/workflows/deploy.yml --runtime podman"
  echo "  $0 tui"
  exit 0
fi

# Pass through all arguments to wrkflw
exec wrkflw "$@"
