package cmd

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/spf13/cobra"
)

const (
	gameHubHelperScheme         = "gamehub-helper"
	gameHubHelperAppName        = "GameHub Helper.app"
	defaultGogHandoffOriginList = "http://localhost:3005,http://127.0.0.1:3005"
	helperLaunchStatusPort      = 53935
	psnChromeProfileDirName     = "psn-chrome-profile"
)

var (
	helperInstallAppPath string
	helperInstallCliPath string
	helperLaunchTimeout  time.Duration
	helperLaunchUser     string
	helperLaunchPsnDebug string
	helperLaunchTTL      time.Duration
)

var (
	helperRuntimeGOOS           = runtime.GOOS
	psnRemoteDebugEndpointReady = func(ctx context.Context, remoteDebugURL string) bool {
		checkCtx, cancel := context.WithTimeout(ctx, 2*time.Second)
		defer cancel()
		_, err := resolvePsnRemoteDebugURL(checkCtx, remoteDebugURL)
		return err == nil
	}
	startPsnRemoteDebugChrome = defaultStartPsnRemoteDebugChrome
	confirmHelperOrigin       = defaultConfirmHelperOrigin
)

type helperLaunchRequest struct {
	Platform   string
	Origin     string
	LaunchID   string
	StatusPort int
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

On macOS this creates a small AppleScript application in ~/Applications. The app starts the existing
visible-browser platform helper flow in the background when the Admin UI launches a
gamehub-helper://connect/... URL. The custom URI carries only non-secret launch metadata.`),
	RunE: runHelperInstallProtocolHandler,
}

var helperUninstallCmd = &cobra.Command{
	Use:   "uninstall-protocol-handler",
	Short: "Remove the local gamehub-helper:// macOS handler app",
	RunE:  runHelperUninstallProtocolHandler,
}

var helperLaunchCmd = &cobra.Command{
	Use:   "launch gamehub-helper://connect/{gog|psn|epic}",
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
		return fmt.Errorf("refusing to register a temporary go run binary; run `go build -o gamehub-cli .` and then `./gamehub-cli helper install-protocol-handler --cli-path <absolute-path-to-gamehub-cli>`")
	}

	if err := installMacOSProtocolHandler(appPath, cliPath); err != nil {
		return err
	}

	fmt.Fprintf(cmd.OutOrStdout(), "Installed gamehub-helper:// protocol handler at %s\n", appPath)
	fmt.Fprintf(cmd.OutOrStdout(), "It launches %s helper launch <url> in the background.\n", cliPath)
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
	if request.Platform != "gog" && request.Platform != "psn" && request.Platform != "epic" {
		return fmt.Errorf("unsupported helper platform %q", request.Platform)
	}
	// The URI is untrusted input, including its origin, launch ID, and status port. Obtain
	// independent native consent before opening a browser or exposing even the status nonce.
	if request.Origin == "" {
		return fmt.Errorf("helper launch requires an explicit GameHub origin")
	}
	if err := confirmHelperOrigin(cmd.Context(), request.Origin, request.Platform); err != nil {
		return fmt.Errorf("helper connection was not approved: %w", err)
	}

	var launchStatus *helperLaunchStatusState
	var shutdownLaunchStatus func(context.Context) error
	if request.LaunchID != "" {
		allowedOrigins, err := parseGogHandoffOrigins(defaultGogHandoffOriginList)
		if err != nil {
			return err
		}
		if request.Origin != "" {
			allowedOrigins, err = parseGogHandoffOrigins(request.Origin)
			if err != nil {
				return err
			}
		}

		state, shutdown, err := startHelperLaunchStatusServer(request.Platform, request.LaunchID, allowedOrigins, request.StatusPort)
		if err != nil {
			return err
		}
		launchStatus = state
		shutdownLaunchStatus = shutdown
		defer func() {
			shutdownCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
			defer cancel()
			_ = shutdownLaunchStatus(shutdownCtx)
		}()
	}

	handoffOrigin := defaultGogHandoffOriginList
	if request.Origin != "" {
		handoffOrigin = request.Origin
	}

	var runErr error
	switch request.Platform {
	case "gog":
		gogConnectTimeout = helperLaunchTimeout
		gogConnectUser = helperLaunchUser
		gogHandoffServer = true
		gogHandoffOrigin = handoffOrigin
		gogHandoffTTL = helperLaunchTTL

		previousHook := gogHandoffURLHook
		if launchStatus != nil {
			gogHandoffURLHook = launchStatus.ready
		}
		defer func() {
			gogHandoffURLHook = previousHook
		}()
		runErr = runConnectGog(cmd, nil)
	case "psn":
		restorePsnLaunch := configurePsnHelperLaunch(
			helperLaunchTimeout,
			helperLaunchTTL,
			handoffOrigin,
			helperLaunchPsnDebug,
		)
		defer restorePsnLaunch()

		previousHook := psnHandoffURLHook
		if launchStatus != nil {
			psnHandoffURLHook = launchStatus.ready
		}
		defer func() {
			psnHandoffURLHook = previousHook
		}()
		runErr = runConnectPsn(cmd, nil)
	case "epic":
		epicConnectTimeout = helperLaunchTimeout
		epicHandoffServer = true
		epicHandoffOrigin = handoffOrigin
		epicHandoffTTL = helperLaunchTTL

		previousHook := epicHandoffURLHook
		if launchStatus != nil {
			epicHandoffURLHook = launchStatus.ready
		}
		defer func() {
			epicHandoffURLHook = previousHook
		}()
		runErr = runConnectEpic(cmd, nil)
	}

	if runErr != nil {
		if launchStatus != nil {
			launchStatus.fail(runErr)
		}
		return runErr
	}
	return nil
}

func configurePsnHelperLaunch(timeout time.Duration, handoffTTL time.Duration, handoffOrigin string, remoteDebugURL string) func() {
	previousTimeout := psnConnectTimeout
	previousHandoffServer := psnHandoffServer
	previousHandoffOrigin := psnHandoffOrigin
	previousHandoffTTL := psnHandoffTTL
	previousRemoteDebugURL := psnRemoteDebugURL

	psnConnectTimeout = timeout
	psnHandoffServer = true
	psnHandoffOrigin = handoffOrigin
	psnHandoffTTL = handoffTTL
	psnRemoteDebugURL = strings.TrimSpace(remoteDebugURL)
	if psnRemoteDebugURL == "" {
		psnRemoteDebugURL = defaultPsnRemoteDebugURL
	}

	return func() {
		psnConnectTimeout = previousTimeout
		psnHandoffServer = previousHandoffServer
		psnHandoffOrigin = previousHandoffOrigin
		psnHandoffTTL = previousHandoffTTL
		psnRemoteDebugURL = previousRemoteDebugURL
	}
}

func preparePsnRemoteDebugChrome(ctx context.Context, remoteDebugURL string, stderr io.Writer) error {
	if strings.TrimSpace(remoteDebugURL) == "" || psnRemoteDebugEndpointReady(ctx, remoteDebugURL) {
		return nil
	}
	if helperRuntimeGOOS != "darwin" {
		return nil
	}

	fmt.Fprintf(stderr, "Chrome debugging endpoint %s is not ready. Opening Chrome with local remote debugging enabled.\n", remoteDebugURL)
	if err := startPsnRemoteDebugChrome(ctx, remoteDebugURL); err != nil {
		return err
	}

	deadline := time.Now().Add(25 * time.Second)
	for time.Now().Before(deadline) {
		if psnRemoteDebugEndpointReady(ctx, remoteDebugURL) {
			return nil
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(500 * time.Millisecond):
		}
	}

	profileDir, profileErr := defaultPsnRemoteDebugProfileDir()
	if profileErr != nil {
		profileDir = "the persistent GameHub Chrome profile"
	}
	return fmt.Errorf("Chrome did not expose %s after launch. The helper tried to start Chrome with profile %s; if this repeats, close that Chrome window and use the manual fallback command shown in GameHub", remoteDebugURL, profileDir)
}

func defaultStartPsnRemoteDebugChrome(ctx context.Context, remoteDebugURL string) error {
	normalizedURL, err := normalizePsnRemoteDebugURL(remoteDebugURL)
	if err != nil {
		return err
	}
	parsed, err := url.Parse(normalizedURL)
	if err != nil {
		return fmt.Errorf("failed to parse PlayStation Chrome debugging URL: %w", err)
	}
	port := parsed.Port()
	if port == "" {
		return fmt.Errorf("PlayStation Chrome debugging URL must include a port")
	}
	profileDir, err := defaultPsnRemoteDebugProfileDir()
	if err != nil {
		return err
	}
	if err := os.MkdirAll(profileDir, 0700); err != nil {
		return fmt.Errorf("failed to prepare PlayStation Chrome profile directory: %w", err)
	}

	chromePath := "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
	command := exec.CommandContext(
		ctx,
		chromePath,
		"--remote-debugging-address=127.0.0.1",
		"--remote-debugging-port="+port,
		"--user-data-dir="+profileDir,
		psnConnectStartURL,
	)
	if err := command.Start(); err != nil {
		return fmt.Errorf("failed to open Chrome for PlayStation connect: %w", err)
	}
	return nil
}

func defaultPsnRemoteDebugProfileDir() (string, error) {
	cacheDir, err := os.UserCacheDir()
	if err != nil {
		return "", fmt.Errorf("failed to find the user cache directory for the PlayStation Chrome profile: %w", err)
	}
	return filepath.Join(cacheDir, "GameHub", psnChromeProfileDirName), nil
}

const helperOriginConfirmationScript = `on run argv
set targetOrigin to item 1 of argv
set providerName to item 2 of argv
set messageText to "Allow this website to receive your " & providerName & " account connection?" & return & return & targetOrigin & return & return & "Continue only if this is your GameHub website. Cancel unexpected requests."
display dialog messageText with title "GameHub Helper" buttons {"Cancel", "Connect"} default button "Cancel" cancel button "Cancel" with icon caution
end run`

func defaultConfirmHelperOrigin(ctx context.Context, origin string, provider string) error {
	if helperRuntimeGOOS != "darwin" {
		return fmt.Errorf("native approval is unavailable; use the explicit connect command with --origin instead")
	}
	approvalCtx, cancel := context.WithTimeout(ctx, 2*time.Minute)
	defer cancel()
	// Pass display values as argv, never as interpolated AppleScript or shell source.
	if err := exec.CommandContext(approvalCtx, "osascript", "-e", helperOriginConfirmationScript, origin, provider).Run(); err != nil {
		return fmt.Errorf("native origin confirmation was cancelled or unavailable")
	}
	return nil
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
	if parsed.Host != "connect" {
		return helperLaunchRequest{}, fmt.Errorf("unsupported helper URL target")
	}
	platform := strings.TrimPrefix(parsed.Path, "/")
	if platform != "gog" && platform != "psn" && platform != "epic" {
		return helperLaunchRequest{}, fmt.Errorf("unsupported helper URL target")
	}

	values := parsed.Query()
	for name := range values {
		if len(values[name]) != 1 {
			return helperLaunchRequest{}, fmt.Errorf("helper URL parameters must appear exactly once")
		}
		switch strings.ToLower(name) {
		case "origin", "launchid", "statusport":
		case "authorizationcode", "code", "cookie", "cookies", "gog-al", "gogal", "gogalcookie", "npsso", "psnauthbearertoken", "psnauthrefreshtoken", "jsessionid", "pdccws_p", "sb-cdn.session", "psn-csrf-token", "accesstoken", "refreshtoken", "session", "password", "token", "nonce", "handoff", "handoffurl", "result", "resulturl":
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

	launchID := strings.TrimSpace(values.Get("launchId"))
	if launchID != "" && !isValidHelperLaunchID(launchID) {
		return helperLaunchRequest{}, fmt.Errorf("launchId must be a URL-safe non-secret identifier")
	}

	statusPort := helperLaunchStatusPort
	if values.Has("statusPort") {
		ports := values["statusPort"]
		if len(ports) != 1 {
			return helperLaunchRequest{}, fmt.Errorf("statusPort must appear exactly once")
		}
		statusPort, err = strconv.Atoi(ports[0])
		if err != nil || statusPort < 49152 || statusPort > 65535 {
			return helperLaunchRequest{}, fmt.Errorf("statusPort must be a port between 49152 and 65535")
		}
		if launchID == "" {
			return helperLaunchRequest{}, fmt.Errorf("statusPort requires a launchId")
		}
	}

	return helperLaunchRequest{Platform: platform, Origin: origin, LaunchID: launchID, StatusPort: statusPort}, nil
}

func isValidHelperLaunchID(value string) bool {
	if len(value) < 16 || len(value) > 128 {
		return false
	}
	for _, char := range value {
		if char >= 'a' && char <= 'z' {
			continue
		}
		if char >= 'A' && char <= 'Z' {
			continue
		}
		if char >= '0' && char <= '9' {
			continue
		}
		if char == '-' || char == '_' || char == '.' || char == '~' {
			continue
		}
		return false
	}
	return true
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
	if _, err := exec.LookPath("codesign"); err != nil {
		return fmt.Errorf("macOS codesign is required to finalize the helper app: %w", err)
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

	if err := finalizeMacOSHelperApp(appPath, func(name string, args ...string) ([]byte, error) {
		return exec.Command(name, args...).CombinedOutput()
	}); err != nil {
		return err
	}

	if err := registerMacOSProtocolHandler(appPath); err != nil {
		return err
	}

	return nil
}

func finalizeMacOSHelperApp(appPath string, runCommand func(string, ...string) ([]byte, error)) error {
	plistPath := filepath.Join(appPath, "Contents", "Info.plist")
	if err := os.WriteFile(plistPath, []byte(macOSHelperInfoPlist()), 0644); err != nil {
		return fmt.Errorf("failed to write helper app Info.plist: %w", err)
	}

	// osacompile can sign its output. Replacing Info.plist invalidates that signature,
	// so seal the finished, locally generated bundle before registering it.
	// Ad-hoc signing requires no Developer ID and does not provide notarization/trust.
	output, err := runCommand("codesign", "--force", "--sign", "-", "--timestamp=none", appPath)
	if err != nil {
		return fmt.Errorf("failed to sign the local helper app: %w: %s", err, strings.TrimSpace(string(output)))
	}
	output, err = runCommand("codesign", "--verify", "--strict", appPath)
	if err != nil {
		return fmt.Errorf("failed to verify the local helper app signature: %w: %s", err, strings.TrimSpace(string(output)))
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
	set shellCommand to quoted form of gamehubCliPath & " helper launch " & quoted form of helperUrl & " >/dev/null 2>&1 &"
	do shell script shellCommand
end open location

on run
	display dialog "GameHub Helper is installed. Use Connect in the GameHub Admin UI to launch the local helper." buttons {"OK"} default button "OK"
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
	<string>GameHub Helper starts the visible local account-connect helper.</string>
	<key>NSHighResolutionCapable</key>
	<true/>
</dict>
</plist>
`) + "\n"
}

type helperLaunchStatusState struct {
	mu         sync.Mutex
	handoffURL string
	err        error
}

func (state *helperLaunchStatusState) ready(handoffURL string) {
	state.mu.Lock()
	defer state.mu.Unlock()
	if state.err != nil {
		return
	}
	state.handoffURL = handoffURL
}

func (state *helperLaunchStatusState) fail(err error) {
	state.mu.Lock()
	defer state.mu.Unlock()
	if state.handoffURL != "" || state.err != nil {
		return
	}
	state.err = err
}

func (state *helperLaunchStatusState) handler(expectedLaunchID string, allowedOrigins map[string]struct{}) http.HandlerFunc {
	return func(response http.ResponseWriter, request *http.Request) {
		response.Header().Set("Cache-Control", "no-store")
		response.Header().Set("Content-Type", "application/json")

		if !applyGogHandoffCors(response, request, allowedOrigins) {
			http.Error(response, `{"status":"forbidden"}`, http.StatusForbidden)
			return
		}

		if request.Method == http.MethodOptions {
			response.WriteHeader(http.StatusNoContent)
			return
		}
		if request.Method != http.MethodGet {
			http.Error(response, `{"status":"method_not_allowed"}`, http.StatusMethodNotAllowed)
			return
		}
		if request.URL.Query().Get("launchId") != expectedLaunchID {
			// Legacy clients use one fixed port. Report contention without revealing
			// the active launch ID, result nonce, provider error, or credentials.
			http.Error(response, `{"status":"busy"}`, http.StatusConflict)
			return
		}

		state.mu.Lock()
		defer state.mu.Unlock()

		encoder := json.NewEncoder(response)
		encoder.SetEscapeHTML(false)
		if state.err != nil {
			response.WriteHeader(http.StatusInternalServerError)
			_ = encoder.Encode(map[string]string{
				"status":  "failed",
				"message": state.err.Error(),
			})
			return
		}
		if state.handoffURL == "" {
			response.WriteHeader(http.StatusAccepted)
			_ = encoder.Encode(map[string]string{"status": "starting"})
			return
		}

		response.WriteHeader(http.StatusOK)
		_ = encoder.Encode(map[string]string{
			"status":     "handoff_ready",
			"handoffUrl": state.handoffURL,
		})
	}
}

func startHelperLaunchStatusServer(platform string, launchID string, allowedOrigins map[string]struct{}, port int) (*helperLaunchStatusState, func(context.Context) error, error) {
	listener, err := net.Listen("tcp4", "127.0.0.1:"+strconv.Itoa(port))
	if err != nil {
		return nil, nil, fmt.Errorf("failed to start local helper launch status server: %w", err)
	}

	state := &helperLaunchStatusState{}
	mux := http.NewServeMux()
	mux.HandleFunc(helperLaunchStatusPath(platform), state.handler(launchID, allowedOrigins))

	server := &http.Server{
		Handler:           loopbackHostOnly(mux),
		ReadHeaderTimeout: 5 * time.Second,
	}

	go func() {
		if err := server.Serve(listener); err != nil && !errors.Is(err, http.ErrServerClosed) {
			state.fail(err)
		}
	}()

	return state, server.Shutdown, nil
}

func helperLaunchStatusPath(platform string) string {
	return "/connect/" + platform + "/launch-status"
}

func init() {
	rootCmd.AddCommand(helperCmd)
	helperCmd.AddCommand(helperInstallCmd)
	helperCmd.AddCommand(helperUninstallCmd)
	helperCmd.AddCommand(helperLaunchCmd)

	helperInstallCmd.Flags().StringVar(&helperInstallAppPath, "app-path", "", "macOS helper app path (default ~/Applications/GameHub Helper.app)")
	helperInstallCmd.Flags().StringVar(&helperInstallCliPath, "cli-path", "", "gamehub-cli binary to launch (default current executable)")
	helperUninstallCmd.Flags().StringVar(&helperInstallAppPath, "app-path", "", "macOS helper app path (default ~/Applications/GameHub Helper.app)")
	helperLaunchCmd.Flags().DurationVar(&helperLaunchTimeout, "timeout", 10*time.Minute, "maximum time to wait for the visible provider login")
	helperLaunchCmd.Flags().StringVar(&helperLaunchUser, "user", "", "optional GOG username override when userData.json cannot report it")
	helperLaunchCmd.Flags().StringVar(&helperLaunchPsnDebug, "psn-remote-debugging-url", defaultPsnRemoteDebugURL, "local Chrome debugging endpoint used by PlayStation helper launches")
	helperLaunchCmd.Flags().DurationVar(&helperLaunchTTL, "handoff-result-ttl", 10*time.Minute, "maximum time to keep a captured local handoff result available")
}
