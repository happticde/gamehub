package cmd

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"strings"
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

	fmt.Fprintln(os.Stderr, "Opening a visible GOG browser session. Sign in there; no GOG password is entered into GameHub.")
	fmt.Fprintln(os.Stderr, "Waiting for gog-al and the GOG username. The cookie will only be printed in the final JSON payload.")

	payload, err := captureGogConnectPayload(cmd.Context(), profileDir, strings.TrimSpace(gogConnectUser), gogConnectTimeout)
	if err != nil {
		return err
	}

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
	defer cancelBrowser()

	if err := chromedp.Run(browserCtx, network.Enable(), chromedp.Navigate(gogConnectStartURL)); err != nil {
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

func readGogAlCookie(ctx context.Context) (*network.Cookie, error) {
	var cookies []*network.Cookie
	err := chromedp.Run(ctx, chromedp.ActionFunc(func(ctx context.Context) error {
		var err error
		cookies, err = network.GetCookies().
			WithURLs([]string{"https://www.gog.com", "https://gog.com"}).
			Do(ctx)
		return err
	}))
	if err != nil {
		return nil, err
	}

	for _, cookie := range cookies {
		if cookie.Name == "gog-al" && strings.TrimSpace(cookie.Value) != "" {
			return cookie, nil
		}
	}

	return nil, nil
}

func readGogUsername(ctx context.Context) (string, error) {
	var username string
	err := chromedp.Run(ctx, chromedp.Evaluate(
		gogUserDataScript,
		&username,
		func(params *runtime.EvaluateParams) *runtime.EvaluateParams {
			return params.WithAwaitPromise(true)
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

func init() {
	rootCmd.AddCommand(connectCmd)
	connectCmd.AddCommand(connectGogCmd)

	connectGogCmd.Flags().DurationVar(&gogConnectTimeout, "timeout", 10*time.Minute, "maximum time to wait for the visible GOG login")
	connectGogCmd.Flags().StringVar(&gogConnectUser, "user", "", "optional GOG username override when userData.json cannot report it")
}
