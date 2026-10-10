#!/bin/sh
set -eu
output_dir=${1:?Pass an absolute release output directory}
version=${2:?Pass the shared release version}
case "$version" in *[!0-9.]*|'') echo 'Invalid release version' >&2; exit 2 ;; esac
cli_dir=$(CDPATH='' cd -- "$(dirname -- "$0")/.." && pwd)
mkdir -p "$output_dir"
output_dir=$(CDPATH='' cd -- "$output_dir" && pwd)
package_dir=$(mktemp -d)
trap 'rm -rf "$package_dir"' EXIT HUP INT TERM
cd "$cli_dir"
for architecture in amd64 arm64; do
    CGO_ENABLED=0 GOOS=linux GOARCH="$architecture" go build -trimpath \
        -ldflags "-s -w -X happtic/gamehub-cli/cmd.Version=$version" -o "$package_dir/gamehub-cli" .
    tar -czf "$output_dir/gamehub-cli-$version-linux-$architecture.tar.gz" -C "$package_dir" gamehub-cli
done
sh scripts/package-macos-helper.sh "$output_dir" "$version"
for architecture in amd64 arm64; do
    mv "$output_dir/gamehub-helper-macos-$architecture.tar.gz" "$output_dir/gamehub-helper-$version-macos-$architecture.tar.gz"
done
cd "$output_dir"
sha256sum ./*.tar.gz > SHA256SUMS
