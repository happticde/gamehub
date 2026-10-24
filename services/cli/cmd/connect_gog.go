package cmd

import (
	"context"
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"net/http"
	"net/url"
	"os"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/chromedp/cdproto/network"
	"github.com/chromedp/cdproto/runtime"
	"github.com/chromedp/chromedp"
	"github.com/spf13/cobra"
)

const (
	gogConnectStartURL = "https://www.gog.com/account"
	gogUserDataScript  = `(() => fetch("https://www.gog.com/userData.json", { credentials: "include", cache: "no-store" })
	.then(async (response) => {
		if (!response.ok) return "";
		const data = await response.json();
		const candidates = [data.username, data.user?.username, data.user?.name, data.nickname, data.login];
		const username = candidates.find((value) => typeof value === "string" && value.trim().length > 0);
		return username || "";
	})
	.catch(() => ""))()`
)

var (
	gogConnectTimeout time.Duration
	gogConnectUser    string
	gogHandoffServer  bool
	gogHandoffOrigin  string
	gogHandoffTTL     time.Duration
	gogHandoffURLHook func(string)
)

type gogConnectPayload struct {
	Provider      string            `json:"provider"`
	User          string            `json:"user"`
	Cookies       map[string]string `json:"cookies"`
	CapturedAtUTC string            `json:"capturedAtUtc"`
	ExpiresAtUTC  string            `json:"expiresAtUtc,omitempty"`
}

var connectCmd = &cobra.Command{
	Use:   "connect",
	Short: "Capture local account connection payloads",
}

var connectGogCmd = &cobra.Command{
	Use:   "gog",
	Short: "Open a visible GOG login session and print the GameHub helper JSON",
	Long: strings.TrimSpace(`Open a temporary, visible Chrome/Chromium browser profile for GOG.

Sign in to GOG in that browser window, including any MFA or captcha prompts. The helper waits until
it can read the gog-al cookie and GOG username from that controlled browser context, prints one JSON
payload to stdout, and then removes the temporary profile.`),
	RunE: runConnectGog,
}

func runConnectGog(cmd *cobra.Command, args []string) error {
	if gogConnectTimeout <= 0 {
		return fmt.Errorf("timeout must be greater than zero")
	}

	profileDir, err := os.MkdirTemp("", "gamehub-gog-connect-*")
	if err != nil {
		return fmt.Errorf("failed to create temporary browser profile: %w", err)
	}
	defer os.RemoveAll(profileDir)

	var handoff *gogHandoffState
	var shutdownHandoff func(context.Context) error
	if gogHandoffServer {
		if gogHandoffTTL <= 0 {
			return fmt.Errorf("handoff result ttl must be greater than zero")
		}

		allowedOrigins, err := parseGogHandoffOrigins(gogHandoffOrigin)
		if err != nil {
			return err
		}

		handoffURL, state, shutdown, err := startGogHandoffServer(allowedOrigins)
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
		if gogHandoffURLHook != nil {
			gogHandoffURLHook(handoffURL)
		}
		fmt.Fprintln(os.Stderr, "Local handoff URL ready for the GameHub Admin GOG connector. It contains only a one-time local nonce, not the GOG cookie.")
	}

	fmt.Fprintln(os.Stderr, "Opening a visible GOG browser session. Sign in there; no GOG password is entered into GameHub.")
	if gogHandoffServer {
		fmt.Fprintln(os.Stderr, "Waiting for gog-al and the GOG username. The result will be held only in this helper process until GameHub imports it.")
	} else {
		fmt.Fprintln(os.Stderr, "Waiting for gog-al and the GOG username. GameHub will not import automatically; paste the final JSON into the Admin fallback to save it.")
	}

	payload, err := captureGogConnectPayload(cmd.Context(), profileDir, strings.TrimSpace(gogConnectUser), gogConnectTimeout)
	if err != nil {
		if handoff != nil {
			handoff.fail(err)
		}
		return err
	}

	if handoff != nil {
		handoff.complete(payload)
		fmt.Fprintln(os.Stderr, "GOG session captured locally. No credential has been stored yet; keep this helper running until GameHub imports the one-time result.")

		waitCtx, cancel := context.WithTimeout(cmd.Context(), gogHandoffTTL)
		defer cancel()
		if err := handoff.waitConsumed(waitCtx); err != nil {
			if errors.Is(err, context.DeadlineExceeded) {
				fmt.Fprintln(os.Stderr, "GameHub did not import the local handoff result before it expired. Run the helper again, or use the Admin manual fallback with a fresh result.")
				return nil
			}
			return err
		}

		fmt.Fprintln(os.Stderr, "GameHub imported and saved the local GOG handoff result. The in-memory copy has been cleared.")
		return nil
	}

	fmt.Fprintln(os.Stderr, "GOG session captured locally. GameHub has not imported it; paste the JSON payload into the Admin fallback to save it.")
	encoder := json.NewEncoder(cmd.OutOrStdout())
	encoder.SetEscapeHTML(false)
	return encoder.Encode(payload)
}

func captureGogConnectPayload(parent context.Context, profileDir string, userOverride string, timeout time.Duration) (gogConnectPayload, error) {
	ctx, cancel := context.WithTimeout(parent, timeout)
	defer cancel()

	options := append(chromedp.DefaultExecAllocatorOptions[:],
		chromedp.Flag("headless", false),
		chromedp.Flag("disable-gpu", false),
		chromedp.NoDefaultBrowserCheck,
		chromedp.NoFirstRun,
		chromedp.UserDataDir(profileDir),
	)

	allocatorCtx, cancelAllocator := chromedp.NewExecAllocator(ctx, options...)
	defer cancelAllocator()

	browserCtx, cancelBrowser := chromedp.NewContext(allocatorCtx)
	defer closeConnectBrowser(browserCtx, cancelBrowser)

	if _, err := chromedp.Call(browserCtx, network.Enable, network.EnableParams{}); err != nil {
		return gogConnectPayload{}, fmt.Errorf("failed to prepare GOG browser session: %w", err)
	}
	if err := chromedp.Do(browserCtx, chromedp.Navigate(gogConnectStartURL)); err != nil {
		return gogConnectPayload{}, fmt.Errorf("failed to open GOG browser session: %w", err)
	}

	ticker := time.NewTicker(2 * time.Second)
	defer ticker.Stop()

	for {
		cookie, err := readGogAlCookie(browserCtx)
		if err != nil {
			return gogConnectPayload{}, fmt.Errorf("failed to read GOG browser cookies: %w", err)
		}

		username := userOverride
		if username == "" {
			username, err = readGogUsername(browserCtx)
			if err != nil {
				return gogConnectPayload{}, fmt.Errorf("failed to read GOG username: %w", err)
			}
		}

		if cookie != nil && strings.TrimSpace(username) != "" {
			return newGogConnectPayload(strings.TrimSpace(username), cookie.Value, cookie.Expires, time.Now().UTC()), nil
		}

		select {
		case <-ctx.Done():
			if errors.Is(ctx.Err(), context.DeadlineExceeded) {
				return gogConnectPayload{}, fmt.Errorf("timed out waiting for GOG login after %s", timeout)
			}
			return gogConnectPayload{}, ctx.Err()
		case <-ticker.C:
		}
	}
}

// Let Chrome flush its temporary profile before removal. Remote allocators close only our tab.
func closeConnectBrowser(ctx context.Context, cancel context.CancelFunc) {
	defer cancel()
	shutdownCtx, stop := context.WithTimeout(ctx, 5*time.Second)
	defer stop()
	_ = chromedp.Cancel(shutdownCtx)
}

func readGogAlCookie(ctx context.Context) (*network.Cookie, error) {
	result, err := chromedp.Call(ctx, network.GetCookies, network.GetCookiesParams{
		URLs: []string{"https://www.gog.com", "https://gog.com"},
	})
	if err != nil {
		return nil, err
	}

	for _, cookie := range result.Cookies {
		if cookie.Name == "gog-al" && strings.TrimSpace(cookie.Value) != "" {
			return cookie, nil
		}
	}

	return nil, nil
}

func readGogUsername(ctx context.Context) (string, error) {
	username, err := chromedp.Run(ctx, chromedp.Evaluate[string](
		gogUserDataScript,
		func(params *runtime.EvaluateParams) {
			params.AwaitPromise = new(true)
		},
	))
	if err != nil {
		return "", err
	}
	return strings.TrimSpace(username), nil
}

func newGogConnectPayload(user string, cookie string, expires float64, capturedAt time.Time) gogConnectPayload {
	payload := gogConnectPayload{
		Provider:      "gog",
		User:          user,
		Cookies:       map[string]string{"gog-al": cookie},
		CapturedAtUTC: capturedAt.UTC().Format(time.RFC3339),
	}

	if expires > 0 {
		payload.ExpiresAtUTC = time.Unix(int64(expires), 0).UTC().Format(time.RFC3339)
	}

	return payload
}

type gogHandoffState struct {
	mu       sync.Mutex
	payload  any
	err      error
	consumed bool
	done     chan struct{}
}

func newGogHandoffState() *gogHandoffState {
	return &gogHandoffState{done: make(chan struct{})}
}

func (state *gogHandoffState) complete(payload any) {
	state.mu.Lock()
	defer state.mu.Unlock()
	if state.consumed || state.payload != nil || state.err != nil {
		return
	}
	state.payload = payload
}

func (state *gogHandoffState) fail(err error) {
	state.mu.Lock()
	defer state.mu.Unlock()
	if state.consumed || state.payload != nil || state.err != nil {
		return
	}
	state.err = err
}

func (state *gogHandoffState) waitConsumed(ctx context.Context) error {
	select {
	case <-state.done:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

func (state *gogHandoffState) handler(expectedNonce string, allowedOrigins map[string]struct{}) http.HandlerFunc {
	return func(response http.ResponseWriter, request *http.Request) {
		response.Header().Set("Cache-Control", "no-store")
		response.Header().Set("Content-Type", "application/json")

		if request.URL.Query().Get("nonce") != expectedNonce {
			http.Error(response, `{"status":"invalid_nonce"}`, http.StatusNotFound)
			return
		}

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

		payload, status, message, code := state.take()
		response.WriteHeader(code)
		encoder := json.NewEncoder(response)
		encoder.SetEscapeHTML(false)
		if payload != nil {
			_ = encoder.Encode(payload)
			return
		}
		body := map[string]string{"status": status}
		if message != "" {
			body["message"] = message
		}
		_ = encoder.Encode(body)
	}
}

func (state *gogHandoffState) take() (any, string, string, int) {
	state.mu.Lock()
	defer state.mu.Unlock()

	if state.consumed {
		return nil, "consumed", "", http.StatusGone
	}
	if state.err != nil {
		message := state.err.Error()
		state.err = nil
		state.consumed = true
		close(state.done)
		return nil, "failed", message, http.StatusInternalServerError
	}
	if state.payload == nil {
		return nil, "pending", "", http.StatusAccepted
	}

	payload := state.payload
	state.payload = nil
	state.consumed = true
	close(state.done)
	return payload, "", "", http.StatusOK
}

func applyGogHandoffCors(response http.ResponseWriter, request *http.Request, allowedOrigins map[string]struct{}) bool {
	origin := request.Header.Get("Origin")
	if origin == "" {
		return true
	}

	allowedOrigin, ok := allowedGogHandoffOrigin(request, allowedOrigins)
	if !ok {
		return false
	}

	response.Header().Set("Access-Control-Allow-Origin", allowedOrigin)
	response.Header().Set("Access-Control-Allow-Methods", "GET, OPTIONS")
	response.Header().Set("Access-Control-Allow-Headers", "Content-Type")
	if request.Header.Get("Access-Control-Request-Private-Network") == "true" {
		response.Header().Set("Access-Control-Allow-Private-Network", "true")
	}
	response.Header().Set("Vary", "Origin")
	return true
}

func allowedGogHandoffOrigin(request *http.Request, allowedOrigins map[string]struct{}) (string, bool) {
	origin := request.Header.Get("Origin")
	if origin == "" {
		return "", true
	}
	if _, ok := allowedOrigins[origin]; ok {
		return origin, true
	}

	// A query parameter (even a private-network address) is never a trust grant.
	return "", false
}

func startGogHandoffServer(allowedOrigins map[string]struct{}) (string, *gogHandoffState, func(context.Context) error, error) {
	return startPlatformHandoffServer("gog", allowedOrigins)
}

func startPlatformHandoffServer(platform string, allowedOrigins map[string]struct{}) (string, *gogHandoffState, func(context.Context) error, error) {
	nonce, err := newGogHandoffNonce()
	if err != nil {
		return "", nil, nil, fmt.Errorf("failed to create local handoff nonce: %w", err)
	}

	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		return "", nil, nil, fmt.Errorf("failed to start local handoff server: %w", err)
	}

	state := newGogHandoffState()
	mux := http.NewServeMux()
	resultPath := "/connect/" + platform + "/result"
	mux.HandleFunc(resultPath, state.handler(nonce, allowedOrigins))

	server := &http.Server{
		Handler:           loopbackHostOnly(mux),
		ReadHeaderTimeout: 5 * time.Second,
	}

	go func() {
		if err := server.Serve(listener); err != nil && !errors.Is(err, http.ErrServerClosed) {
			state.fail(err)
		}
	}()

	tcpAddr, ok := listener.Addr().(*net.TCPAddr)
	if !ok {
		_ = listener.Close()
		return "", nil, nil, fmt.Errorf("local handoff server did not return a TCP address")
	}

	resultURL := "http://127.0.0.1:" + strconv.Itoa(tcpAddr.Port) + resultPath + "?nonce=" + nonce
	return resultURL, state, server.Shutdown, nil
}

func newGogHandoffNonce() (string, error) {
	var bytes [32]byte
	if _, err := rand.Read(bytes[:]); err != nil {
		return "", err
	}
	return base64.RawURLEncoding.EncodeToString(bytes[:]), nil
}

func parseGogHandoffOrigins(value string) (map[string]struct{}, error) {
	origins := make(map[string]struct{})
	for _, candidate := range strings.Split(value, ",") {
		if strings.TrimSpace(candidate) == "" {
			continue
		}

		origin, _, err := normalizeGogHandoffOrigin(candidate)
		if err != nil {
			return nil, err
		}
		origins[origin] = struct{}{}
	}

	if len(origins) == 0 {
		return nil, fmt.Errorf("at least one handoff origin is required")
	}
	return origins, nil
}

func normalizeGogHandoffOrigin(value string) (string, *url.URL, error) {
	origin := strings.TrimSpace(value)
	parsed, err := url.Parse(origin)
	if err != nil || parsed.Scheme == "" || parsed.Host == "" || parsed.User != nil || parsed.Path != "" || parsed.RawQuery != "" || parsed.ForceQuery || parsed.Fragment != "" {
		return "", nil, fmt.Errorf("handoff origin %q must be an origin like http://localhost:3005", origin)
	}
	if parsed.Scheme != "http" && parsed.Scheme != "https" {
		return "", nil, fmt.Errorf("handoff origin %q must use http or https", origin)
	}
	for _, character := range parsed.Host {
		if character < '!' || character > '~' {
			return "", nil, fmt.Errorf("handoff origin must use a printable ASCII host")
		}
	}

	return parsed.Scheme + "://" + parsed.Host, parsed, nil
}

func loopbackHostOnly(next http.Handler) http.Handler {
	return http.HandlerFunc(func(response http.ResponseWriter, request *http.Request) {
		host, _, err := net.SplitHostPort(request.Host)
		if err != nil || (host != "127.0.0.1" && host != "localhost") {
			http.Error(response, "invalid loopback host", http.StatusForbidden)
			return
		}
		next.ServeHTTP(response, request)
	})
}

func init() {
	rootCmd.AddCommand(connectCmd)
	connectCmd.AddCommand(connectGogCmd)

	connectGogCmd.Flags().DurationVar(&gogConnectTimeout, "timeout", 10*time.Minute, "maximum time to wait for the visible GOG login")
	connectGogCmd.Flags().StringVar(&gogConnectUser, "user", "", "optional GOG username override when userData.json cannot report it")
	connectGogCmd.Flags().BoolVar(&gogHandoffServer, "handoff-server", false, "serve the captured GOG result once over a local loopback URL instead of printing the cookie JSON")
	connectGogCmd.Flags().StringVar(&gogHandoffOrigin, "origin", "http://localhost:3005,http://127.0.0.1:3005", "comma-separated GameHub frontend origins allowed for local handoff CORS")
	connectGogCmd.Flags().DurationVar(&gogHandoffTTL, "handoff-result-ttl", 10*time.Minute, "maximum time to keep a captured local handoff result available")
}
