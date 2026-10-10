#!/bin/sh
set -eu

# Package only build outputs and explicit installer inputs, never local CLI configuration.
cli_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
output_dir=${1:?Pass the destination directory for the helper downloads}
mkdir -p "$output_dir"
output_dir=$(CDPATH= cd -- "$output_dir" && pwd)
package_dir=$(mktemp -d)
trap 'rm -rf "$package_dir"' EXIT HUP INT TERM

cd "$cli_dir"
for architecture in arm64 amd64; do
    bundle="$package_dir/GameHub Helper"
    mkdir -p "$bundle"
    CGO_ENABLED=0 GOOS=darwin GOARCH="$architecture" go build -trimpath -o "$bundle/gamehub-cli" .
    cp helper-distribution/install.command "$bundle/Install GameHub Helper.command"
    cp helper-distribution/README.txt "$bundle/README.txt"
    chmod 755 "$bundle/gamehub-cli" "$bundle/Install GameHub Helper.command"
    tar -czf "$output_dir/gamehub-helper-macos-$architecture.tar.gz" -C "$package_dir" 'GameHub Helper'
    rm -rf "$bundle"
done
