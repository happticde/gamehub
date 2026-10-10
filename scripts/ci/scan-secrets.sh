#!/usr/bin/env bash
set -euo pipefail
report=${1:?Pass the redacted report path}
error_log=$(mktemp)
trap 'rm -f "$error_log"' EXIT
git rev-parse --verify HEAD >/dev/null
status=0
gitleaks git . --config .gitleaks.toml --redact=100 --no-banner --log-level error \
    --log-opts="--all -- . :(glob,exclude)**/.env* :(exclude).env :(glob,exclude).env.*" \
    --report-format json --report-path "$report" 2> "$error_log" || status=$?
# Gitleaks can exit zero after a Git subprocess error. Such a scan is not a pass.
if [[ -s "$error_log" ]]; then
    cat "$error_log" >&2
    exit 1
fi
exit "$status"
