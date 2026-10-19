#!/bin/bash
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "GameHub Helper currently supports installation on macOS only."
    exit 1
fi

package_dir=$(cd -- "$(dirname -- "$0")" && pwd)
install_dir="$HOME/Library/Application Support/GameHub/Helper"
installed_binary="$install_dir/gamehub-cli"

wait_to_close() {
    if [[ -t 0 ]]; then
        read -r -p "Press Enter to close this window. " || true
    fi
}

# Keep legal notices even after the extracted download is removed.
for notice in LICENSE.txt THIRD_PARTY_NOTICES.txt SOURCE.txt; do
    if [[ ! -f "$package_dir/$notice" ]]; then
        echo "Incomplete helper package: missing $notice. Download a complete package."
        exit 1
    fi
done
mkdir -p "$install_dir"
for notice in LICENSE.txt THIRD_PARTY_NOTICES.txt SOURCE.txt; do
    cp "$package_dir/$notice" "$install_dir/$notice"
done
# The protocol handler must not depend on the download remaining in Downloads.
# Keep the same file on retries so a user's macOS approval is not discarded.
if [[ ! -f "$installed_binary" ]] || ! cmp -s "$package_dir/gamehub-cli" "$installed_binary"; then
    temporary_binary=$(mktemp "$install_dir/.gamehub-cli.XXXXXX")
    trap 'rm -f "$temporary_binary"' EXIT
    cp "$package_dir/gamehub-cli" "$temporary_binary"
    chmod 755 "$temporary_binary"
    mv -f "$temporary_binary" "$installed_binary"
fi
chmod 755 "$installed_binary"

# Only execute the final path; never ask Gatekeeper to approve a temporary name.
if ! "$installed_binary" --help > /dev/null; then
    echo
    echo "GameHub Helper could not be started. Its installed file has been kept at:"
    printf '  %s\n' "$installed_binary"
    echo
    echo "If macOS blocked gamehub-cli, open System Settings > Privacy & Security"
    echo "and choose Open Anyway for gamehub-cli. Confirm Open, then run this installer again."
    echo "The same helper version will keep the same installed file on the next attempt."
    echo "If no macOS security warning appeared, check the error above and your Mac architecture."
    wait_to_close
    exit 1
fi
"$installed_binary" helper install-protocol-handler --cli-path "$installed_binary"

echo
echo "GameHub Helper is installed. Return to GameHub and continue account setup."
wait_to_close
