package cmd

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"os"
	"slices"
	"strconv"
	"strings"
	"time"

	"github.com/chromedp/cdproto/network"
	"github.com/chromedp/chromedp"
	"github.com/spf13/cobra"
)

const psnConnectStartURL = "https://store.playstation.com/"

var (
	psnConnectTimeout time.Duration
	psnHandoffServer  bool
	psnHandoffOrigin  string
	psnHandoffTTL     time.Duration
	psnHandoffURLHook func(string)
)

var psnSessionCookieNames = []string{
	"npsso",
	"psnAuthBearerToken",
	"psnAuthRefreshToken",
	"JSESSIONID",
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
context, prints one JSON payload to stdout, and then removes the temporary profile.`),
	RunE: runConnectPsn,
}

func runConnectPsn(cmd *cobra.Command, args []string) error {
	if psnConnectTimeout <= 0 {
		return fmt.Errorf("timeout must be greater than zero")
	}

	profileDir, err := os.MkdirTemp("", "gamehub-psn-connect-*")
	if err != nil {
		return fmt.Errorf("failed to create temporary browser profile: %w", err)
	}
	defer os.RemoveAll(profileDir)

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

	fmt.Fprintln(os.Stderr, "Opening a visible PlayStation browser session. Sign in there; no PSN password is entered into GameHub.")
	if psnHandoffServer {
		fmt.Fprintln(os.Stderr, "Waiting for PlayStation session cookies. Cookies will be held in memory for a one-time local handoff.")
	} else {
		fmt.Fprintln(os.Stderr, "Waiting for PlayStation session cookies. Cookies will only be printed in the final JSON payload.")
	}

	payload, err := capturePsnConnectPayload(cmd.Context(), profileDir, psnConnectTimeout)
	if err != nil {
		if handoff != nil {
			handoff.fail(err)
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

func capturePsnConnectPayload(parent context.Context, profileDir string, timeout time.Duration) (psnConnectPayload, error) {
	ctx, cancel := context.WithTimeout(parent, timeout)
	defer cancel()

	options, err := psnBrowserAllocatorOptions(profileDir)
	if err != nil {
		return psnConnectPayload{}, err
	}

	allocatorCtx, cancelAllocator := chromedp.NewExecAllocator(ctx, options...)
	defer cancelAllocator()

	browserCtx, cancelBrowser := chromedp.NewContext(allocatorCtx)
	defer cancelBrowser()

	if err := chromedp.Run(browserCtx, network.Enable(), chromedp.Navigate(psnConnectStartURL)); err != nil {
		return psnConnectPayload{}, fmt.Errorf("failed to open PlayStation browser session: %w", err)
	}

	ticker := time.NewTicker(2 * time.Second)
	defer ticker.Stop()

	for {
		cookies, expiresAt, err := readPsnSessionCookies(browserCtx)
		if err != nil {
			return psnConnectPayload{}, fmt.Errorf("failed to read PlayStation browser cookies: %w", err)
		}

		if len(cookies) > 0 {
			return newPsnConnectPayload(cookies, expiresAt, time.Now().UTC()), nil
		}

		select {
		case <-ctx.Done():
			if errors.Is(ctx.Err(), context.DeadlineExceeded) {
				return psnConnectPayload{}, fmt.Errorf("timed out waiting for PlayStation login after %s", timeout)
			}
			return psnConnectPayload{}, ctx.Err()
		case <-ticker.C:
		}
	}
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
	connectPsnCmd.Flags().BoolVar(&psnHandoffServer, "handoff-server", false, "serve the captured PlayStation result once over a local loopback URL instead of printing the cookie JSON")
	connectPsnCmd.Flags().StringVar(&psnHandoffOrigin, "origin", defaultGogHandoffOriginList, "comma-separated GameHub frontend origins allowed for local handoff CORS")
	connectPsnCmd.Flags().DurationVar(&psnHandoffTTL, "handoff-result-ttl", 10*time.Minute, "maximum time to keep a captured local handoff result available")
}
