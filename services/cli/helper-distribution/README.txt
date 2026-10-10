GameHub Helper for macOS

1. Extract this archive on the Mac where you use your GameHub browser.
2. Open "Install GameHub Helper.command" and complete the installation.
   If you prefer Terminal, run: bash "Install GameHub Helper.command"
3. Return to GameHub, choose "Continue after installation", and connect your account.

The installer copies the CLI to ~/Library/Application Support/GameHub/Helper
and registers ~/Applications/GameHub Helper.app for gamehub-helper:// links.
You can remove the extracted archive after installation. No Go installation is needed.
Install once for PlayStation, GOG and Epic Games. Chrome must be installed on this Mac.
After updating GameHub's helper protocol, install the matching new helper download.
Current launches use a separate loopback status port per attempt to avoid retry conflicts.
Your provider password is entered only on the provider's own website.

These packages are unsigned and not notarized. macOS may require approval to open them.
Use only packages downloaded from your trusted GameHub deployment.
If macOS blocks the installer or gamehub-cli, open System Settings > Privacy & Security
and choose Open Anyway for the blocked file, then confirm Open.
Run the installer again after approving gamehub-cli. It keeps the installed binary at
~/Library/Application Support/GameHub/Helper/gamehub-cli and does not replace an
identical version on retry. An updated helper version may require a new approval.
The installer signs the locally generated protocol-handler app ad hoc after setting
its URL scheme, then verifies the finished bundle before registration. This requires
no paid certificate and does not provide Developer ID trust or notarization.
Windows, Linux, iOS and iPadOS installation is not supported in this release.

To unregister the helper:
"$HOME/Library/Application Support/GameHub/Helper/gamehub-cli" helper uninstall-protocol-handler
Then remove ~/Library/Application Support/GameHub/Helper if it is no longer needed.
