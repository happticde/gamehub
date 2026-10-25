#!/bin/sh
set -eu
cd "$(git rev-parse --show-toplevel)"
current=$(git config --get core.hooksPath || :)
if [ -n "$current" ] && [ "$current" != '.githooks' ]; then
    echo 'Hook setup stopped: core.hooksPath already points elsewhere. Integrate the secret scan into your existing hooks.' >&2
    exit 1
fi
if [ -z "$current" ] && [ -x "$(git rev-parse --git-path hooks/pre-commit)" ]; then
    echo 'Hook setup stopped: an existing pre-commit hook must be integrated first.' >&2
    exit 1
fi
python3 scripts/scan-staged-secrets.py --check-tool
git config --local core.hooksPath .githooks
echo 'GameHub pre-commit secret protection is enabled for this checkout.'
