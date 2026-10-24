package cmd

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	neturl "net/url"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/chromedp/cdproto/network"
	"github.com/chromedp/chromedp"
	"github.com/chromedp/chromedp/remote"
	"github.com/spf13/cobra"
)

const (
	epicLauncherClientID     = "34a02cf8f4414e29b15921876da36f9a"
	epicRedirectAPIURL       = "https://www.epicgames.com/id/api/redirect?clientId=" + epicLauncherClientID + "&responseType=code"
	epicAuthorizationCodeTTL = 10 * time.Minute
)

var (
	epicConnectTimeout time.Duration
	epicHandoffServer  bool
	epicHandoffOrigin  string
	epicHandoffTTL     time.Duration
	epicHandoffURLHook func(string)
)

type epicConnectPayload struct {
	Provider          string `json:"provider"`
	AuthorizationCode string `json:"authorizationCode"`
	CapturedAtUTC     string `json:"capturedAtUtc"`
	ExpiresAtUTC      string `json:"expiresAtUtc"`
}

var connectEpicCmd = &cobra.Command{
	Use:   "epic",
	Short: "Open a visible Epic Games login session and print the GameHub helper JSON",
	Long: strings.TrimSpace(`Open a temporary, visible Chrome/Chromium browser profile for Epic Games.

Sign in to Epic Games in that browser window, including any MFA or captcha prompts. The helper
waits for Epic's launcher-compatible redirect API JSON, extracts only authorizationCode, and then
prints one GameHub helper JSON payload. Refresh tokens are never captured by the helper.`),
	RunE: runConnectEpic,
}

func runConnectEpic(cmd *cobra.Command, args []string) error {
	if epicConnectTimeout <= 0 {
		return fmt.Errorf("timeout must be greater than zero")
	}

	profileDir, err := os.MkdirTemp("", "gamehub-epic-connect-*")
	if err != nil {
		return fmt.Errorf("failed to create temporary browser profile: %w", err)
	}
	defer os.RemoveAll(profileDir)

	var handoff *gogHandoffState
	var shutdownHandoff func(context.Context) error
	if epicHandoffServer {
		if epicHandoffTTL <= 0 {
			return fmt.Errorf("handoff result ttl must be greater than zero")
		}

		allowedOrigins, err := parseGogHandoffOrigins(epicHandoffOrigin)
		if err != nil {
			return err
		}

		handoffURL, state, shutdown, err := startPlatformHandoffServer("epic", allowedOrigins)
		if err != nil {
			return err
		}
		handoff = state
		shutdownHandoff = shutdown
		defer func() {
			shutdownCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
			defer cancel()
			_ = shutdownHandoff(shutdownCtx)
		}()

		fmt.Fprintln(cmd.OutOrStdout(), handoffURL)
		if epicHandoffURLHook != nil {
			epicHandoffURLHook(handoffURL)
		}
		fmt.Fprintln(os.Stderr, "Local handoff URL ready for the GameHub Admin Epic Games connector. It contains only a one-time local nonce, not the authorization code.")
	}

	fmt.Fprintln(os.Stderr, "Opening a visible Epic Games browser session. Sign in there; no Epic password is entered into GameHub.")
	if epicHandoffServer {
		fmt.Fprintln(os.Stderr, "Waiting for Epic's authorization JSON. The result will be held only in this helper process until GameHub imports it.")
	} else {
		fmt.Fprintln(os.Stderr, "Waiting for Epic's authorization JSON. GameHub will not import automatically; paste the final JSON into the Admin fallback to save it.")
	}

	payload, err := captureEpicConnectPayload(cmd.Context(), profileDir, epicConnectTimeout)
	if err != nil {
		if handoff != nil {
			handoff.fail(err)
		}
		return err
	}

	if handoff != nil {
		handoff.complete(payload)
		fmt.Fprintln(os.Stderr, "Epic Games authorization captured locally. No credential has been stored yet; keep this helper running until GameHub imports the one-time result.")

		waitCtx, cancel := context.WithTimeout(cmd.Context(), epicHandoffTTL)
		defer cancel()
		if err := handoff.waitConsumed(waitCtx); err != nil {
			if errors.Is(err, context.DeadlineExceeded) {
				fmt.Fprintln(os.Stderr, "GameHub did not import the local handoff result before it expired. Run the helper again, or use the Admin manual fallback with a fresh result.")
				return nil
			}
			return err
		}

		fmt.Fprintln(os.Stderr, "GameHub imported and saved the local Epic Games handoff result. The in-memory copy has been cleared.")
		return nil
	}

	fmt.Fprintln(os.Stderr, "Epic Games authorization captured locally. GameHub has not imported it; paste the JSON payload into the Admin fallback to save it.")
	encoder := json.NewEncoder(cmd.OutOrStdout())
	encoder.SetEscapeHTML(false)
	return encoder.Encode(payload)
}

func captureEpicConnectPayload(parent context.Context, profileDir string, timeout time.Duration) (epicConnectPayload, error) {
	ctx, cancel := context.WithTimeout(parent, timeout)
	defer cancel()

	options, err := epicBrowserAllocatorOptions(profileDir)
	if err != nil {
		return epicConnectPayload{}, err
	}

	allocatorCtx, cancelAllocator := chromedp.NewExecAllocator(ctx, options...)
	defer cancelAllocator()

	browserCtx, cancelBrowser := chromedp.NewContext(allocatorCtx)
	defer cancelBrowser()

	if _, err := chromedp.Call(browserCtx, network.Enable, network.EnableParams{}); err != nil {
		return epicConnectPayload{}, fmt.Errorf("failed to prepare Epic Games browser session: %w", err)
	}
	if err := chromedp.Do(browserCtx, chromedp.Navigate(epicConnectStartURL())); err != nil {
		return epicConnectPayload{}, fmt.Errorf("failed to open Epic Games browser session: %w", err)
	}

	ticker := time.NewTicker(2 * time.Second)
	defer ticker.Stop()

	for {
		code, err := readEpicAuthorizationCode(browserCtx)
		if err != nil {
			return epicConnectPayload{}, fmt.Errorf("failed to read Epic Games authorization response: %w", err)
		}
		if code != "" {
			return newEpicConnectPayload(code, time.Now().UTC()), nil
		}

		select {
		case <-ctx.Done():
			if errors.Is(ctx.Err(), context.DeadlineExceeded) {
				return epicConnectPayload{}, fmt.Errorf("timed out waiting for Epic Games login after %s", timeout)
			}
			return epicConnectPayload{}, ctx.Err()
		case <-ticker.C:
		}
	}
}

func epicConnectStartURL() string {
	url := neturl.URL{
		Scheme: "https",
		Host:   "www.epicgames.com",
		Path:   "/id/login",
	}
	query := url.Query()
	query.Set("redirectUrl", epicRedirectAPIURL)
	url.RawQuery = query.Encode()
	return url.String()
}

func readEpicAuthorizationCode(ctx context.Context) (string, error) {
	currentURL, err := chromedp.Run(ctx, chromedp.Location())
	if err != nil {
		return "", err
	}
	if !isEpicRedirectAPIURL(currentURL) {
		return "", nil
	}
	bodyText, err := chromedp.Run(ctx, chromedp.Text(chromedp.CSS("body")))
	if err != nil {
		return "", err
	}
	return readEpicAuthorizationCodeFromBody(bodyText)
}

func isEpicRedirectAPIURL(value string) bool {
	parsed, err := neturl.Parse(strings.TrimSpace(value))
	if err != nil {
		return false
	}
	return parsed.Scheme == "https" &&
		strings.EqualFold(parsed.Hostname(), "www.epicgames.com") &&
		parsed.Path == "/id/api/redirect" &&
		parsed.Query().Get("clientId") == epicLauncherClientID &&
		parsed.Query().Get("responseType") == "code"
}

func epicBrowserAllocatorOptions(profileDir string) ([]chromedp.ExecAllocatorOption, error) {
	debuggingPort, err := reserveLocalTCPPort()
	if err != nil {
		return nil, fmt.Errorf("failed to reserve local browser debugging port: %w", err)
	}

	flags := epicBrowserLaunchFlags(profileDir, debuggingPort)
	options := make([]chromedp.ExecAllocatorOption, 0, len(flags)+1)
	options = append(options, remote.WebSocket)
	for _, flag := range flags {
		options = append(options, chromedp.Flag(flag.Name, flag.Value))
	}

	return options, nil
}

func epicBrowserLaunchFlags(profileDir string, debuggingPort int) []psnBrowserLaunchFlag {
	// Keep Epic's browser close to a user-started Chrome. The chromedp default allocator adds
	// automation-visible flags such as --enable-automation, which Epic rejects during sign-in.
	// The first-run prompts are suppressed so the helper opens straight into the login flow.
	return []psnBrowserLaunchFlag{
		{Name: "no-default-browser-check", Value: true},
		{Name: "no-first-run", Value: true},
		{Name: "remote-debugging-address", Value: "127.0.0.1"},
		{Name: "remote-debugging-port", Value: strconv.Itoa(debuggingPort)},
		{Name: "user-data-dir", Value: profileDir},
	}
}

func readEpicAuthorizationCodeFromBody(value string) (string, error) {
	body := strings.TrimSpace(value)
	if body == "" {
		return "", nil
	}

	var payload map[string]any
	if err := json.Unmarshal([]byte(body), &payload); err != nil {
		return "", nil
	}

	code, ok := payload["authorizationCode"].(string)
	if !ok {
		return "", nil
	}
	return strings.TrimSpace(code), nil
}

func newEpicConnectPayload(authorizationCode string, capturedAt time.Time) epicConnectPayload {
	capturedAt = capturedAt.UTC()
	return epicConnectPayload{
		Provider:          "epic",
		AuthorizationCode: authorizationCode,
		CapturedAtUTC:     capturedAt.Format(time.RFC3339),
		ExpiresAtUTC:      capturedAt.Add(epicAuthorizationCodeTTL).Format(time.RFC3339),
	}
}

func init() {
	connectCmd.AddCommand(connectEpicCmd)

	connectEpicCmd.Flags().DurationVar(&epicConnectTimeout, "timeout", 10*time.Minute, "maximum time to wait for the visible Epic Games login")
	connectEpicCmd.Flags().BoolVar(&epicHandoffServer, "handoff-server", false, "serve the captured Epic Games result once over a local loopback URL instead of printing the JSON")
	connectEpicCmd.Flags().StringVar(&epicHandoffOrigin, "origin", defaultGogHandoffOriginList, "comma-separated GameHub frontend origins allowed for local handoff CORS")
	connectEpicCmd.Flags().DurationVar(&epicHandoffTTL, "handoff-result-ttl", 10*time.Minute, "maximum time to keep a captured local handoff result available")
}
