#!/usr/bin/env bash
# Retry complete installs when a registry connection fails outside npm's fetch retries.
set -euo pipefail
cd -- "${1:?Pass the package directory}"
install_log=$(mktemp)
trap 'rm -f "$install_log"' EXIT

for attempt in 1 2 3; do
    echo "npm ci: attempt $attempt/3"
    if npm ci --no-fund 2>&1 | tee "$install_log"; then
        exit 0
    else
        statuses=("${PIPESTATUS[@]}")
    fi
    # A failed log pipe must never turn into a successful installation.
    if [[ "${statuses[1]}" != 0 ]]; then
        exit "${statuses[1]}"
    fi
    status=${statuses[0]}
    if [[ "$attempt" == 3 ]] || ! grep -Eq \
        '^npm (error|ERR!) code (ECONNRESET|ETIMEDOUT|EAI_AGAIN|ENETUNREACH|EHOSTUNREACH|ECONNREFUSED|ERR_SOCKET_TIMEOUT|EPIPE)([[:space:]]|$)' \
        "$install_log"; then
        exit "$status"
    fi
    delay=$((attempt * 10))
    echo "Transient npm network failure; retrying in ${delay}s." >&2
    sleep "$delay"
done
