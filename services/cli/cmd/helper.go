package cmd

import (
	"errors"
	"fmt"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"time"

	"github.com/spf13/cobra"
)

const (
	gameHubHelperScheme         = "gamehub-helper"
	gameHubHelperAppName        = "GameHub Helper.app"
	defaultGogHandoffOriginList = "http://localhost:3005,http://127.0.0.1:3005"
)

var (
	helperInstallAppPath string
	helperInstallCliPath string
	helperLaunchTimeout  time.Duration
	helperLaunchUser     string
	helperLaunchTTL      time.Duration
)

type helperLaunchRequest struct {
	Platform string
	Origin   string
}

var helperCmd = &cobra.Command{
	Use:   "helper",
	Short: "Install and launch local GameHub helper integrations",
}

var helperInstallCmd = &cobra.Command{
	Use:     "install-protocol-handler",
	Aliases: []string{"install"},
	Short:   "Register gamehub-helper:// URLs on this host",
	Long: strings.TrimSpace(`Register the local gamehub-helper:// protocol handler for this host.

On macOS this creates a small AppleScript application in ~/Applications. The app opens Terminal and
starts the existing visible-browser GOG helper flow when the Admin UI launches
gamehub-helper://connect/gog. The custom URI carries only non-secret launch metadata.`),
	RunE: runHelperInstallProtocolHandler,
}

var helperUninstallCmd = &cobra.Command{
	Use:   "uninstall-protocol-handler",
	Short: "Remove the local gamehub-helper:// macOS handler app",
	RunE:  runHelperUninstallProtocolHandler,
}

var helperLaunchCmd = &cobra.Command{
	Use:   "launch gamehub-helper://connect/gog",
	Short: "Launch a helper flow from a gamehub-helper:// URL",
	Args:  cobra.ExactArgs(1),
	RunE:  runHelperLaunch,
}

func runHelperInstallProtocolHandler(cmd *cobra.Command, args []string) error {
	if runtime.GOOS != "darwin" {
		return fmt.Errorf("protocol handler installation is currently implemented for macOS only; use `gamehub-cli connect gog --handoff-server` on this OS")
	}

	appPath, err := resolveHelperAppPath(helperInstallAppPath)
	if err != nil {
		return err
	}

	cliPath, err := resolveHelperCliPath(helperInstallCliPath)
	if err != nil {
		return err
	}
	if isLikelyGoRunBinary(cliPath) && strings.TrimSpace(helperInstallCliPath) == "" {
		return fmt.Errorf("refusing to register a temporary go run binary; build gamehub-cli first or pass --cli-path")
	}

	if err := installMacOSProtocolHandler(appPath, cliPath); err != nil {
		return err
	}

	fmt.Fprintf(cmd.OutOrStdout(), "Installed gamehub-helper:// protocol handler at %s\n", appPath)
	fmt.Fprintf(cmd.OutOrStdout(), "It launches %s helper launch <url> in Terminal.\n", cliPath)
	return nil
}

func runHelperUninstallProtocolHandler(cmd *cobra.Command, args []string) error {
	if runtime.GOOS != "darwin" {
		return fmt.Errorf("protocol handler uninstall is currently implemented for macOS only")
	}

	appPath, err := resolveHelperAppPath(helperInstallAppPath)
	if err != nil {
		return err
	}

	_ = unregisterMacOSProtocolHandler(appPath)
	if err := os.RemoveAll(appPath); err != nil {
		return fmt.Errorf("failed to remove helper app: %w", err)
	}

	fmt.Fprintf(cmd.OutOrStdout(), "Removed gamehub-helper:// protocol handler app at %s\n", appPath)
	return nil
}

func runHelperLaunch(cmd *cobra.Command, args []string) error {
	if helperLaunchTimeout <= 0 {
		return fmt.Errorf("timeout must be greater than zero")
	}
	if helperLaunchTTL <= 0 {
		return fmt.Errorf("handoff result ttl must be greater than zero")
	}

	request, err := parseHelperLaunchURL(args[0])
	if err != nil {
		return err
	}
	if request.Platform != "gog" {
		return fmt.Errorf("unsupported helper platform %q", request.Platform)
	}

	gogConnectTimeout = helperLaunchTimeout
	gogConnectUser = helperLaunchUser
	gogHandoffServer = true
	gogHandoffOrigin = defaultGogHandoffOriginList
	if request.Origin != "" {
		gogHandoffOrigin = request.Origin
	}
	gogHandoffTTL = helperLaunchTTL

	return runConnectGog(cmd, nil)
}

func parseHelperLaunchURL(value string) (helperLaunchRequest, error) {
	parsed, err := url.Parse(strings.TrimSpace(value))
	if err != nil {
		return helperLaunchRequest{}, fmt.Errorf("invalid helper URL: %w", err)
	}

	if parsed.Scheme != gameHubHelperScheme {
		return helperLaunchRequest{}, fmt.Errorf("helper URL must use the %s:// scheme", gameHubHelperScheme)
	}
	if parsed.User != nil {
		return helperLaunchRequest{}, fmt.Errorf("helper URL must not include user info")
	}
	if parsed.Host != "connect" || parsed.Path != "/gog" {
		return helperLaunchRequest{}, fmt.Errorf("unsupported helper URL target")
	}

	values := parsed.Query()
	for name := range values {
		switch strings.ToLower(name) {
		case "origin":
		case "cookie", "gog-al", "gogal", "gogalcookie", "password", "token":
			return helperLaunchRequest{}, fmt.Errorf("helper URL must not contain secret parameter %q", name)
		default:
			return helperLaunchRequest{}, fmt.Errorf("unsupported helper URL parameter %q", name)
		}
	}

	origin := strings.TrimSpace(values.Get("origin"))
	if origin != "" {
		normalized, _, err := normalizeGogHandoffOrigin(origin)
		if err != nil {
			return helperLaunchRequest{}, err
		}
		origin = normalized
	}

	return helperLaunchRequest{Platform: "gog", Origin: origin}, nil
}

func resolveHelperAppPath(value string) (string, error) {
	if strings.TrimSpace(value) != "" {
		return filepath.Abs(value)
	}

	home, err := os.UserHomeDir()
	if err != nil {
		return "", fmt.Errorf("failed to find home directory: %w", err)
	}
	return filepath.Join(home, "Applications", gameHubHelperAppName), nil
}

func resolveHelperCliPath(value string) (string, error) {
	candidate := strings.TrimSpace(value)
	if candidate == "" {
		executable, err := os.Executable()
		if err != nil {
			return "", fmt.Errorf("failed to find current executable: %w", err)
		}
		candidate = executable
	}

	absolute, err := filepath.Abs(candidate)
	if err != nil {
		return "", fmt.Errorf("failed to resolve CLI path: %w", err)
	}
	evaluated, err := filepath.EvalSymlinks(absolute)
	if err == nil {
		absolute = evaluated
	}

	info, err := os.Stat(absolute)
	if err != nil {
		return "", fmt.Errorf("failed to access CLI binary %q: %w", absolute, err)
	}
	if info.IsDir() {
		return "", fmt.Errorf("CLI path %q is a directory", absolute)
	}
	if info.Mode()&0111 == 0 {
		return "", fmt.Errorf("CLI path %q is not executable", absolute)
	}

	return absolute, nil
}

func isLikelyGoRunBinary(path string) bool {
	return strings.Contains(path, string(filepath.Separator)+"go-build")
}

func installMacOSProtocolHandler(appPath string, cliPath string) error {
	if _, err := exec.LookPath("osacompile"); err != nil {
		return fmt.Errorf("macOS osacompile is required to create the helper app: %w", err)
	}

	if err := os.MkdirAll(filepath.Dir(appPath), 0755); err != nil {
		return fmt.Errorf("failed to create helper app parent directory: %w", err)
	}
	if err := os.RemoveAll(appPath); err != nil {
		return fmt.Errorf("failed to replace existing helper app: %w", err)
	}

	tempDir, err := os.MkdirTemp("", "gamehub-helper-install-*")
	if err != nil {
		return fmt.Errorf("failed to create helper install workspace: %w", err)
	}
	defer os.RemoveAll(tempDir)

	scriptPath := filepath.Join(tempDir, "GameHubHelper.applescript")
	if err := os.WriteFile(scriptPath, []byte(macOSHelperAppleScript(cliPath)), 0600); err != nil {
		return fmt.Errorf("failed to write helper AppleScript: %w", err)
	}

	output, err := exec.Command("osacompile", "-o", appPath, scriptPath).CombinedOutput()
	if err != nil {
		return fmt.Errorf("failed to compile helper app: %w: %s", err, strings.TrimSpace(string(output)))
	}

	plistPath := filepath.Join(appPath, "Contents", "Info.plist")
	if err := os.WriteFile(plistPath, []byte(macOSHelperInfoPlist()), 0644); err != nil {
		return fmt.Errorf("failed to write helper app Info.plist: %w", err)
	}

	if err := registerMacOSProtocolHandler(appPath); err != nil {
		return err
	}

	return nil
}

func registerMacOSProtocolHandler(appPath string) error {
	lsregister := "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"
	if _, err := os.Stat(lsregister); err == nil {
		output, runErr := exec.Command(lsregister, "-f", appPath).CombinedOutput()
		if runErr != nil {
			return fmt.Errorf("failed to register helper app with Launch Services: %w: %s", runErr, strings.TrimSpace(string(output)))
		}
		return nil
	}

	output, err := exec.Command("open", "-Ra", appPath).CombinedOutput()
	if err != nil {
		return fmt.Errorf("failed to register helper app with Launch Services: %w: %s", err, strings.TrimSpace(string(output)))
	}
	return nil
}

func unregisterMacOSProtocolHandler(appPath string) error {
	lsregister := "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"
	if _, err := os.Stat(lsregister); err != nil {
		return err
	}

	output, err := exec.Command(lsregister, "-u", appPath).CombinedOutput()
	if err != nil {
		return errors.New(strings.TrimSpace(string(output)))
	}
	return nil
}

func macOSHelperAppleScript(cliPath string) string {
	return fmt.Sprintf(`property gamehubCliPath : %s

on open location helperUrl
	set shellCommand to quoted form of gamehubCliPath & " helper launch " & quoted form of helperUrl
	tell application "Terminal"
		activate
		do script shellCommand
	end tell
end open location

on run
	display dialog "GameHub Helper is installed. Use Connect GOG in the GameHub Admin UI to launch the local helper." buttons {"OK"} default button "OK"
end run
`, appleScriptStringLiteral(cliPath))
}

func appleScriptStringLiteral(value string) string {
	return "\"" + strings.ReplaceAll(value, "\"", "\" & quote & \"") + "\""
}

func macOSHelperInfoPlist() string {
	return strings.TrimSpace(`<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleDevelopmentRegion</key>
	<string>en</string>
	<key>CFBundleDisplayName</key>
	<string>GameHub Helper</string>
	<key>CFBundleExecutable</key>
	<string>applet</string>
	<key>CFBundleIdentifier</key>
	<string>de.happtic.gamehub.helper</string>
	<key>CFBundleName</key>
	<string>GameHub Helper</string>
	<key>CFBundlePackageType</key>
	<string>APPL</string>
	<key>CFBundleSignature</key>
	<string>aplt</string>
	<key>CFBundleURLTypes</key>
	<array>
		<dict>
			<key>CFBundleURLName</key>
			<string>GameHub Helper Protocol</string>
			<key>CFBundleURLSchemes</key>
			<array>
				<string>gamehub-helper</string>
			</array>
		</dict>
	</array>
	<key>LSMinimumSystemVersion</key>
	<string>10.13</string>
	<key>NSAppleEventsUsageDescription</key>
	<string>GameHub Helper opens Terminal to start the visible local account-connect helper.</string>
	<key>NSHighResolutionCapable</key>
	<true/>
</dict>
</plist>
`) + "\n"
}

func init() {
	rootCmd.AddCommand(helperCmd)
	helperCmd.AddCommand(helperInstallCmd)
	helperCmd.AddCommand(helperUninstallCmd)
	helperCmd.AddCommand(helperLaunchCmd)

	helperInstallCmd.Flags().StringVar(&helperInstallAppPath, "app-path", "", "macOS helper app path (default ~/Applications/GameHub Helper.app)")
	helperInstallCmd.Flags().StringVar(&helperInstallCliPath, "cli-path", "", "gamehub-cli binary to launch (default current executable)")
	helperUninstallCmd.Flags().StringVar(&helperInstallAppPath, "app-path", "", "macOS helper app path (default ~/Applications/GameHub Helper.app)")
	helperLaunchCmd.Flags().DurationVar(&helperLaunchTimeout, "timeout", 10*time.Minute, "maximum time to wait for the visible GOG login")
	helperLaunchCmd.Flags().StringVar(&helperLaunchUser, "user", "", "optional GOG username override when userData.json cannot report it")
	helperLaunchCmd.Flags().DurationVar(&helperLaunchTTL, "handoff-result-ttl", 10*time.Minute, "maximum time to keep a captured local handoff result available")
}
