#!/bin/bash
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "GameHub Helper currently supports installation on macOS only."
    exit 1
fi

package_dir=$(cd -- "$(dirname -- "$0")" && pwd)
install_dir="$HOME/Library/Application Support/GameHub/Helper"
mkdir -p "$install_dir"
# The protocol handler must not depend on the download remaining in Downloads.
temporary_binary=$(mktemp "$install_dir/.gamehub-cli.XXXXXX")
trap 'rm -f "$temporary_binary"' EXIT
cp "$package_dir/gamehub-cli" "$temporary_binary"
chmod 755 "$temporary_binary"
"$temporary_binary" --help > /dev/null
mv -f "$temporary_binary" "$install_dir/gamehub-cli"
"$install_dir/gamehub-cli" helper install-protocol-handler --cli-path "$install_dir/gamehub-cli"

echo
echo "GameHub Helper is installed. Return to GameHub and continue account setup."
if [[ -t 0 ]]; then
    read -r -p "Press Enter to close this window. "
fi
