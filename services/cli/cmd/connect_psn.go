package cmd

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	neturl "net/url"
	"os"
	"slices"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/chromedp/cdproto/network"
	"github.com/chromedp/chromedp"
	"github.com/spf13/cobra"
)

const psnConnectStartURL = "https://store.playstation.com/"

var (
	psnConnectTimeout  time.Duration
	psnDiagnosticsFile string
	psnRemoteDebugURL  string
	psnHandoffServer   bool
	psnHandoffOrigin   string
	psnHandoffTTL      time.Duration
	psnHandoffURLHook  func(string)
)

var psnSessionCookieNames = []string{
	"npsso",
	"psnAuthBearerToken",
	"psnAuthRefreshToken",
	"JSESSIONID",
	"pdccws_p",
	"sb-cdn.session",
	"psn-csrf-token",
}

type psnConnectPayload struct {
	Provider      string            `json:"provider"`
	Cookies       map[string]string `json:"cookies"`
	CapturedAtUTC string            `json:"capturedAtUtc"`
	ExpiresAtUTC  string            `json:"expiresAtUtc,omitempty"`
}

var connectPsnCmd = &cobra.Command{
	Use:   "psn",
	Short: "Open a visible PlayStation login session and print the GameHub helper JSON",
	Long: strings.TrimSpace(`Open a temporary, visible Chrome/Chromium browser profile for PlayStation.

Sign in to PlayStation/Sony in that browser window, including any MFA or captcha prompts. The
helper waits until it can read known PlayStation session cookies from that controlled browser
context, prints one JSON payload to stdout, and then removes the temporary profile.

If Sony blocks the helper-launched login redirects, use --remote-debugging-url to attach to an
already running local Chrome debug profile where PlayStation is signed in.`),
	RunE: runConnectPsn,
}

func runConnectPsn(cmd *cobra.Command, args []string) error {
	if psnConnectTimeout <= 0 {
		return fmt.Errorf("timeout must be greater than zero")
	}

	profileDir := ""
	if strings.TrimSpace(psnRemoteDebugURL) == "" {
		var err error
		profileDir, err = os.MkdirTemp("", "gamehub-psn-connect-*")
		if err != nil {
			return fmt.Errorf("failed to create temporary browser profile: %w", err)
		}
		defer os.RemoveAll(profileDir)
	}

	var handoff *gogHandoffState
	var shutdownHandoff func(context.Context) error
	if psnHandoffServer {
		if psnHandoffTTL <= 0 {
			return fmt.Errorf("handoff result ttl must be greater than zero")
		}

		allowedOrigins, err := parseGogHandoffOrigins(psnHandoffOrigin)
		if err != nil {
			return err
		}

		handoffURL, state, shutdown, err := startPlatformHandoffServer("psn", allowedOrigins)
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
		if psnHandoffURLHook != nil {
			psnHandoffURLHook(handoffURL)
		}
		fmt.Fprintln(os.Stderr, "Paste this local handoff URL into the GameHub Admin PlayStation connector. It contains only a one-time local nonce, not PSN cookies.")
	}

	diagnostics, err := newPsnConnectDiagnostics(strings.TrimSpace(psnDiagnosticsFile))
	if err != nil {
		return err
	}
	if diagnostics != nil {
		defer diagnostics.Close()
		fmt.Fprintf(os.Stderr, "Writing sanitized PlayStation login diagnostics to %s. Cookies, headers, query strings, and fragments are not recorded.\n", psnDiagnosticsFile)
	}

	if strings.TrimSpace(psnRemoteDebugURL) != "" {
		fmt.Fprintln(os.Stderr, "Connecting to an existing Chrome debugging session. Sign in there with PlayStation if needed; no PSN password is entered into GameHub.")
	} else {
		fmt.Fprintln(os.Stderr, "Opening a visible PlayStation browser session. Sign in there; no PSN password is entered into GameHub.")
	}
	if psnHandoffServer {
		fmt.Fprintln(os.Stderr, "Waiting for PlayStation session cookies. Cookies will be held in memory for a one-time local handoff.")
	} else {
		fmt.Fprintln(os.Stderr, "Waiting for PlayStation session cookies. Cookies will only be printed in the final JSON payload.")
	}

	payload, err := capturePsnConnectPayload(cmd.Context(), profileDir, strings.TrimSpace(psnRemoteDebugURL), psnConnectTimeout, diagnostics)
	if err != nil {
		if handoff != nil {
			handoff.fail(err)
			fmt.Fprintln(os.Stderr, err.Error())
			fmt.Fprintln(os.Stderr, "Keep this helper running until GameHub imports the local failure result.")

			waitCtx, cancel := context.WithTimeout(cmd.Context(), psnHandoffTTL)
			defer cancel()
			if waitErr := handoff.waitConsumed(waitCtx); waitErr != nil && !errors.Is(waitErr, context.DeadlineExceeded) {
				return waitErr
			}
			return nil
		}
		return err
	}

	if handoff != nil {
		handoff.complete(payload)
		fmt.Fprintln(os.Stderr, "PlayStation session captured. Keep this helper running until GameHub imports the one-time result.")

		waitCtx, cancel := context.WithTimeout(cmd.Context(), psnHandoffTTL)
		defer cancel()
		if err := handoff.waitConsumed(waitCtx); err != nil {
			if errors.Is(err, context.DeadlineExceeded) {
				fmt.Fprintln(os.Stderr, "The local handoff result expired before GameHub imported it. Run the helper again to reconnect.")
				return nil
			}
			return err
		}

		fmt.Fprintln(os.Stderr, "GameHub imported the local PlayStation handoff result. The in-memory copy has been cleared.")
		return nil
	}

	encoder := json.NewEncoder(cmd.OutOrStdout())
	encoder.SetEscapeHTML(false)
	return encoder.Encode(payload)
}

func capturePsnConnectPayload(parent context.Context, profileDir string, remoteDebugURL string, timeout time.Duration, diagnostics *psnConnectDiagnostics) (psnConnectPayload, error) {
	ctx, cancel := context.WithTimeout(parent, timeout)
	defer cancel()

	allocatorCtx, cancelAllocator, shouldNavigate, err := newPsnBrowserAllocator(ctx, profileDir, remoteDebugURL)
	if err != nil {
		return psnConnectPayload{}, err
	}
	defer cancelAllocator()

	browserCtx, cancelBrowser := chromedp.NewContext(allocatorCtx)
	defer cancelBrowser()

	rateLimit := make(chan string, 1)
	chromedp.ListenTarget(browserCtx, func(event any) {
		switch typedEvent := event.(type) {
		case *network.EventRequestWillBeSent:
			if diagnostics != nil {
				diagnostics.recordRequest(typedEvent)
			}
		case *network.EventResponseReceived:
			if typedEvent.Response == nil {
				return
			}
			if diagnostics != nil {
				diagnostics.recordResponse(typedEvent)
			}
			if !isPsnLoginRateLimit(typedEvent.Response.URL, int(typedEvent.Response.Status)) {
				return
			}
			select {
			case rateLimit <- typedEvent.Response.URL:
			default:
			}
		case *network.EventLoadingFailed:
			if diagnostics != nil {
				diagnostics.recordLoadingFailed(typedEvent)
			}
		default:
			return
		}
	})

	if err := chromedp.Run(browserCtx, network.Enable()); err != nil {
		return psnConnectPayload{}, fmt.Errorf("failed to prepare PlayStation browser session: %w", err)
	}
	if shouldNavigate {
		if err := chromedp.Run(browserCtx, chromedp.Navigate(psnConnectStartURL)); err != nil {
			return psnConnectPayload{}, fmt.Errorf("failed to open PlayStation browser session: %w", err)
		}
	}

	ticker := time.NewTicker(2 * time.Second)
	defer ticker.Stop()
	reportedDiagnosticRateLimit := false

	for {
		cookies, expiresAt, err := readPsnSessionCookies(browserCtx)
		if err != nil {
			return psnConnectPayload{}, fmt.Errorf("failed to read PlayStation browser cookies: %w", err)
		}

		if len(cookies) > 0 {
			if diagnostics != nil {
				diagnostics.recordCookieCapture(cookies)
			}
			return newPsnConnectPayload(cookies, expiresAt, time.Now().UTC()), nil
		}

		select {
		case url := <-rateLimit:
			if diagnostics != nil {
				if !reportedDiagnosticRateLimit {
					fmt.Fprintf(os.Stderr, "Sony returned HTTP 429 during PlayStation sign-in at %s. Diagnostics mode will keep the browser open until cookies are captured, the timeout is reached, or you stop the helper.\n", url)
					reportedDiagnosticRateLimit = true
				}
				continue
			}
			return psnConnectPayload{}, fmt.Errorf("Sony returned HTTP 429 during PlayStation sign-in at %s. Wait before retrying, or use the manual cookie fallback with an already signed-in normal browser session", url)
		case <-ctx.Done():
			if errors.Is(ctx.Err(), context.DeadlineExceeded) {
				return psnConnectPayload{}, fmt.Errorf("timed out waiting for PlayStation login after %s", timeout)
			}
			return psnConnectPayload{}, ctx.Err()
		case <-ticker.C:
		}
	}
}

type psnConnectDiagnostics struct {
	mu          sync.Mutex
	encoder     *json.Encoder
	file        *os.File
	startedAt   time.Time
	requestURLs map[network.RequestID]string
}

type psnDiagnosticEvent struct {
	AtUTC          string   `json:"atUtc"`
	ElapsedMs      int64    `json:"elapsedMs"`
	Event          string   `json:"event"`
	RequestID      string   `json:"requestId,omitempty"`
	Method         string   `json:"method,omitempty"`
	URL            string   `json:"url,omitempty"`
	Status         int      `json:"status,omitempty"`
	StatusText     string   `json:"statusText,omitempty"`
	ResourceType   string   `json:"resourceType,omitempty"`
	HasUserGesture *bool    `json:"hasUserGesture,omitempty"`
	ErrorText      string   `json:"errorText,omitempty"`
	Canceled       *bool    `json:"canceled,omitempty"`
	BlockedReason  string   `json:"blockedReason,omitempty"`
	CookieNames    []string `json:"cookieNames,omitempty"`
}

func newPsnConnectDiagnostics(path string) (*psnConnectDiagnostics, error) {
	if path == "" {
		return nil, nil
	}

	file, err := os.Create(path)
	if err != nil {
		return nil, fmt.Errorf("failed to create PlayStation diagnostics file: %w", err)
	}

	return &psnConnectDiagnostics{
		encoder:     json.NewEncoder(file),
		file:        file,
		startedAt:   time.Now().UTC(),
		requestURLs: make(map[network.RequestID]string),
	}, nil
}

func (diagnostics *psnConnectDiagnostics) Close() {
	if diagnostics == nil || diagnostics.file == nil {
		return
	}
	_ = diagnostics.file.Close()
}

func (diagnostics *psnConnectDiagnostics) record(event psnDiagnosticEvent) {
	if diagnostics == nil {
		return
	}

	diagnostics.mu.Lock()
	defer diagnostics.mu.Unlock()

	now := time.Now().UTC()
	event.AtUTC = now.Format(time.RFC3339Nano)
	event.ElapsedMs = now.Sub(diagnostics.startedAt).Milliseconds()
	_ = diagnostics.encoder.Encode(event)
}

func (diagnostics *psnConnectDiagnostics) recordRequest(event *network.EventRequestWillBeSent) {
	requestURL := ""
	method := ""
	if event.Request != nil {
		requestURL = event.Request.URL
		method = event.Request.Method
	}

	diagnostics.mu.Lock()
	diagnostics.requestURLs[event.RequestID] = requestURL
	diagnostics.mu.Unlock()

	if event.RedirectResponse != nil {
		diagnostics.record(psnDiagnosticEvent{
			Event:        "redirect",
			RequestID:    string(event.RequestID),
			URL:          sanitizePsnDiagnosticURL(event.RedirectResponse.URL),
			Status:       int(event.RedirectResponse.Status),
			StatusText:   event.RedirectResponse.StatusText,
			ResourceType: string(event.Type),
		})
	}

	hasUserGesture := event.HasUserGesture
	diagnostics.record(psnDiagnosticEvent{
		Event:          "request",
		RequestID:      string(event.RequestID),
		Method:         method,
		URL:            sanitizePsnDiagnosticURL(requestURL),
		ResourceType:   string(event.Type),
		HasUserGesture: &hasUserGesture,
	})
}

func (diagnostics *psnConnectDiagnostics) recordResponse(event *network.EventResponseReceived) {
	diagnostics.record(psnDiagnosticEvent{
		Event:        "response",
		RequestID:    string(event.RequestID),
		URL:          sanitizePsnDiagnosticURL(event.Response.URL),
		Status:       int(event.Response.Status),
		StatusText:   event.Response.StatusText,
		ResourceType: string(event.Type),
	})
}

func (diagnostics *psnConnectDiagnostics) recordLoadingFailed(event *network.EventLoadingFailed) {
	diagnostics.mu.Lock()
	requestURL := diagnostics.requestURLs[event.RequestID]
	diagnostics.mu.Unlock()

	canceled := event.Canceled
	diagnostics.record(psnDiagnosticEvent{
		Event:         "loading_failed",
		RequestID:     string(event.RequestID),
		URL:           sanitizePsnDiagnosticURL(requestURL),
		ResourceType:  string(event.Type),
		ErrorText:     event.ErrorText,
		Canceled:      &canceled,
		BlockedReason: string(event.BlockedReason),
	})
}

func (diagnostics *psnConnectDiagnostics) recordCookieCapture(cookies map[string]string) {
	cookieNames := make([]string, 0, len(cookies))
	for name := range cookies {
		cookieNames = append(cookieNames, name)
	}
	sort.Strings(cookieNames)

	diagnostics.record(psnDiagnosticEvent{
		Event:       "cookie_capture",
		CookieNames: cookieNames,
	})
}

func sanitizePsnDiagnosticURL(value string) string {
	parsed, err := neturl.Parse(strings.TrimSpace(value))
	if err != nil || parsed.Scheme == "" || parsed.Host == "" {
		return ""
	}

	parsed.User = nil
	parsed.RawQuery = ""
	parsed.Fragment = ""
	return parsed.String()
}

func newPsnBrowserAllocator(parent context.Context, profileDir string, remoteDebugURL string) (context.Context, context.CancelFunc, bool, error) {
	if strings.TrimSpace(remoteDebugURL) != "" {
		browserWSURL, err := resolvePsnRemoteDebugURL(parent, remoteDebugURL)
		if err != nil {
			return nil, nil, false, err
		}
		allocatorCtx, cancelAllocator := chromedp.NewRemoteAllocator(parent, browserWSURL, chromedp.NoModifyURL)
		return allocatorCtx, cancelAllocator, false, nil
	}

	options, err := psnBrowserAllocatorOptions(profileDir)
	if err != nil {
		return nil, nil, false, err
	}

	allocatorCtx, cancelAllocator := chromedp.NewExecAllocator(parent, options...)
	return allocatorCtx, cancelAllocator, true, nil
}

func normalizePsnRemoteDebugURL(value string) (string, error) {
	remoteURL := strings.TrimSpace(value)
	parsed, err := neturl.Parse(remoteURL)
	if err != nil || parsed.Scheme == "" || parsed.Host == "" {
		return "", fmt.Errorf("remote debugging URL must be an endpoint like http://127.0.0.1:49333")
	}
	if parsed.Scheme != "http" && parsed.Scheme != "ws" {
		return "", fmt.Errorf("remote debugging URL must use http or ws")
	}

	hostname := strings.ToLower(parsed.Hostname())
	ip := net.ParseIP(hostname)
	if hostname != "localhost" && (ip == nil || !ip.IsLoopback()) {
		return "", fmt.Errorf("remote debugging URL must point to localhost or 127.0.0.1")
	}

	return parsed.Scheme + "://" + parsed.Host, nil
}

func resolvePsnRemoteDebugURL(ctx context.Context, value string) (string, error) {
	normalizedURL, err := normalizePsnRemoteDebugURL(value)
	if err != nil {
		return "", err
	}

	parsed, err := neturl.Parse(normalizedURL)
	if err != nil {
		return "", fmt.Errorf("remote debugging URL must be an endpoint like http://127.0.0.1:49333")
	}

	versionURL := "http://" + parsed.Host + "/json/version"
	requestCtx, cancel := context.WithTimeout(ctx, 5*time.Second)
	defer cancel()

	request, err := http.NewRequestWithContext(requestCtx, http.MethodGet, versionURL, nil)
	if err != nil {
		return "", fmt.Errorf("failed to prepare remote debugging health check: %w", err)
	}

	response, err := http.DefaultClient.Do(request)
	if err != nil {
		return "", fmt.Errorf("remote Chrome debugging endpoint %s did not answer %s: %w. Make sure Chrome is still open and really started with the same --remote-debugging-port value used in --remote-debugging-url; on macOS, quit all Chrome windows before starting the debug profile if this endpoint is not reachable", normalizedURL, versionURL, err)
	}
	defer response.Body.Close()

	if response.StatusCode != http.StatusOK {
		return "", fmt.Errorf("remote Chrome debugging endpoint %s returned HTTP %d for %s. Make sure the URL points to a Chrome instance started with the same --remote-debugging-port value", normalizedURL, response.StatusCode, versionURL)
	}

	var version struct {
		WebSocketDebuggerURL string `json:"webSocketDebuggerUrl"`
	}
	if err := json.NewDecoder(io.LimitReader(response.Body, 1<<20)).Decode(&version); err != nil {
		return "", fmt.Errorf("remote Chrome debugging endpoint %s did not return valid /json/version JSON: %w", normalizedURL, err)
	}

	browserWSURL := strings.TrimSpace(version.WebSocketDebuggerURL)
	if browserWSURL == "" {
		return "", fmt.Errorf("remote Chrome debugging endpoint %s did not expose webSocketDebuggerUrl in /json/version", normalizedURL)
	}

	wsURL, err := neturl.Parse(browserWSURL)
	if err != nil || wsURL.Scheme != "ws" || wsURL.Host == "" || !strings.Contains(wsURL.Path, "/devtools/browser/") {
		return "", fmt.Errorf("remote Chrome debugging endpoint %s returned an unsupported browser websocket URL", normalizedURL)
	}

	hostname := strings.ToLower(wsURL.Hostname())
	ip := net.ParseIP(hostname)
	if hostname != "localhost" && (ip == nil || !ip.IsLoopback()) {
		return "", fmt.Errorf("remote Chrome debugging endpoint %s returned a non-loopback browser websocket URL", normalizedURL)
	}

	return browserWSURL, nil
}

func isPsnLoginRateLimit(url string, status int) bool {
	if status != http.StatusTooManyRequests {
		return false
	}

	normalizedURL := strings.ToLower(strings.TrimSpace(url))
	return strings.Contains(normalizedURL, "account.sony.com") ||
		strings.Contains(normalizedURL, "my.account.sony.com") ||
		strings.Contains(normalizedURL, "ca.account.sony.com")
}

func psnBrowserAllocatorOptions(profileDir string) ([]chromedp.ExecAllocatorOption, error) {
	debuggingPort, err := reserveLocalTCPPort()
	if err != nil {
		return nil, fmt.Errorf("failed to reserve local browser debugging port: %w", err)
	}

	// Sony's login can reject browsers that advertise the usual automation defaults. PSN keeps a
	// provider-specific allocator so the working GOG helper path remains unchanged.
	return []chromedp.ExecAllocatorOption{
		chromedp.NoDefaultBrowserCheck,
		chromedp.NoFirstRun,
		chromedp.Flag("headless", false),
		chromedp.Flag("disable-gpu", false),
		chromedp.Flag("enable-automation", false),
		chromedp.Flag("remote-debugging-port", strconv.Itoa(debuggingPort)),
		chromedp.UserDataDir(profileDir),
	}, nil
}

func reserveLocalTCPPort() (int, error) {
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		return 0, err
	}
	defer listener.Close()

	tcpAddr, ok := listener.Addr().(*net.TCPAddr)
	if !ok {
		return 0, fmt.Errorf("reserved listener did not return a TCP address")
	}
	return tcpAddr.Port, nil
}

func readPsnSessionCookies(ctx context.Context) (map[string]string, float64, error) {
	var browserCookies []*network.Cookie
	err := chromedp.Run(ctx, chromedp.ActionFunc(func(ctx context.Context) error {
		var err error
		browserCookies, err = network.GetCookies().
			WithURLs([]string{
				"https://store.playstation.com",
				"https://www.playstation.com",
				"https://my.account.sony.com",
				"https://web.np.playstation.com",
			}).
			Do(ctx)
		return err
	}))
	if err != nil {
		return nil, 0, err
	}

	captured := make(map[string]string)
	var earliestPersistentExpiry float64
	for _, cookie := range browserCookies {
		if !slices.Contains(psnSessionCookieNames, cookie.Name) {
			continue
		}
		value := strings.TrimSpace(cookie.Value)
		if value == "" {
			continue
		}

		captured[cookie.Name] = value
		if cookie.Expires > 0 && (earliestPersistentExpiry == 0 || cookie.Expires < earliestPersistentExpiry) {
			earliestPersistentExpiry = cookie.Expires
		}
	}

	return captured, earliestPersistentExpiry, nil
}

func newPsnConnectPayload(cookies map[string]string, expires float64, capturedAt time.Time) psnConnectPayload {
	payload := psnConnectPayload{
		Provider:      "psn",
		Cookies:       cookies,
		CapturedAtUTC: capturedAt.UTC().Format(time.RFC3339),
	}

	if expires > 0 {
		payload.ExpiresAtUTC = time.Unix(int64(expires), 0).UTC().Format(time.RFC3339)
	}

	return payload
}

func init() {
	connectCmd.AddCommand(connectPsnCmd)

	connectPsnCmd.Flags().DurationVar(&psnConnectTimeout, "timeout", 10*time.Minute, "maximum time to wait for the visible PlayStation login")
	connectPsnCmd.Flags().StringVar(&psnDiagnosticsFile, "diagnostics-file", "", "write sanitized PlayStation login network diagnostics as JSON lines to this file")
	connectPsnCmd.Flags().StringVar(&psnRemoteDebugURL, "remote-debugging-url", "", "connect to an existing Chrome debugging endpoint such as http://127.0.0.1:49333 instead of opening a new PlayStation login browser")
	connectPsnCmd.Flags().BoolVar(&psnHandoffServer, "handoff-server", false, "serve the captured PlayStation result once over a local loopback URL instead of printing the cookie JSON")
	connectPsnCmd.Flags().StringVar(&psnHandoffOrigin, "origin", defaultGogHandoffOriginList, "comma-separated GameHub frontend origins allowed for local handoff CORS")
	connectPsnCmd.Flags().DurationVar(&psnHandoffTTL, "handoff-result-ttl", 10*time.Minute, "maximum time to keep a captured local handoff result available")
}
