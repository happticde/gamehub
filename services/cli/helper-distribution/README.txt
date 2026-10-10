GameHub Helper for macOS

1. Extract this archive on the Mac where you use your GameHub browser.
2. Open "Install GameHub Helper.command" and complete the installation.
   If you prefer Terminal, run: bash "Install GameHub Helper.command"
3. Return to GameHub, choose "Continue after installation", and connect your account.

The installer copies the CLI to ~/Library/Application Support/GameHub/Helper
and registers ~/Applications/GameHub Helper.app for gamehub-helper:// links.
You can remove the extracted archive after installation. No Go installation is needed.
Install once for PlayStation, GOG and Epic Games. Chrome must be installed on this Mac.
Your provider password is entered only on the provider's own website.

These packages are unsigned and not notarized. macOS may require approval to open them.
Use only packages downloaded from your trusted GameHub deployment.
Windows, Linux, iOS and iPadOS installation is not supported in this release.

To unregister the helper:
"$HOME/Library/Application Support/GameHub/Helper/gamehub-cli" helper uninstall-protocol-handler
Then remove ~/Library/Application Support/GameHub/Helper if it is no longer needed.
