#!/bin/sh
# SPDX-FileCopyrightText: 2026 jfk1987 and GameHub contributors
# SPDX-License-Identifier: AGPL-3.0-only
set -eu
destination=${1:?Pass the package directory}
version=${2:?Pass the build version}
cli_dir=$(CDPATH='' cd -- "$(dirname -- "$0")/.." && pwd)
legal_dir=${GAMEHUB_LICENSE_DIR:-$cli_dir/../../licensing}
repository=${GAMEHUB_SOURCE_REPOSITORY:-${GITHUB_REPOSITORY:-happticde/gamehub}}
revision=${GAMEHUB_SOURCE_REVISION:-${GITHUB_SHA:-unknown}}
if [ "$version" != dev ] && [ "$version" != 0.0.0 ] && [ "$revision" = unknown ]; then
    echo 'Versioned packages require their exact source revision.' >&2
    exit 2
fi
printf '%s' "$repository" | LC_ALL=C grep -Eq '^[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+$' || exit 2
if [ "$revision" != unknown ]; then
    printf '%s' "$revision" | LC_ALL=C grep -Eq '^[0-9a-f]{40}$' || exit 2
fi
mkdir -p "$destination"
cp "$legal_dir/LICENSE" "$destination/LICENSE.txt"
notices_dir=$(mktemp -d)
trap 'rm -rf "$notices_dir"' EXIT HUP INT TERM
go run "$cli_dir/tools/license-notices/main.go" -kind go -input "$cli_dir" \
    -legal "$legal_dir" -output "$notices_dir"
cp "$notices_dir/THIRD_PARTY_NOTICES.txt" "$destination/THIRD_PARTY_NOTICES.txt"
{
    printf 'GameHub %s — AGPL-3.0-only\n' "$version"
    printf 'Source repository: https://github.com/%s\n' "$repository"
    printf 'Build revision: %s\n' "$revision"
    if [ "$revision" != unknown ]; then
        printf 'Source for this build: https://github.com/%s/tree/%s\n' "$repository" "$revision"
    fi
    printf '\nRelease source archives are provided alongside official release downloads.\n'
    printf 'Operators of modified builds must provide the corresponding modified source.\n'
    printf 'Upstream links do not cover unpublished local modifications.\n'
} > "$destination/SOURCE.txt"
