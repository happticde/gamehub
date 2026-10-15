#!/usr/bin/env bash
set -euo pipefail

# The workflow checks out the selected main SHA, not a moving branch after verification.
bump=${1:-auto}
output_dir=${2:?Pass a release metadata directory}
case "$bump" in auto|patch|minor|major) ;; *) exit 2 ;; esac
python3 "$(dirname "$0")/select-checks.py" release
mkdir -p "$output_dir"
if [[ "$bump" == auto ]]; then
    tag=$(git-cliff --config cliff.toml --bumped-version)
else
    tag=$(git-cliff --config cliff.toml --bump "$bump" --bumped-version)
fi
[[ "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Invalid release version' >&2; exit 1; }
if git rev-parse --verify --quiet "refs/tags/$tag" >/dev/null; then
    echo 'The calculated release already exists; there are no releasable changes.' >&2
    exit 1
fi
git-cliff --config cliff.toml --unreleased --tag "$tag" --output "$output_dir/RELEASE_NOTES.md"
printf '%s\n' "$tag" > "$output_dir/TAG"
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    printf 'tag=%s\nversion=%s\n' "$tag" "${tag#v}" >> "$GITHUB_OUTPUT"
fi
echo "Prepared $tag without writing a tag or release"
