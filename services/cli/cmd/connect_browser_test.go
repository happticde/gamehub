package cmd

import (
	"context"
	"fmt"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/chromedp/cdproto/fetch"
	"github.com/chromedp/cdproto/network"
	"github.com/chromedp/chromedp"
)

// Opt in locally. CI requires Chrome and runs these tests with synthetic data only.
func TestBrowserConnectReaders(t *testing.T) {
	if os.Getenv("GAMEHUB_BROWSER_TESTS") != "1" {
		t.Skip("set GAMEHUB_BROWSER_TESTS=1 to run real-browser checks")
	}
	var browserPath string
	for _, name := range []string{"google-chrome", "chromium", "chromium-browser"} {
		if path, err := exec.LookPath(name); err == nil {
			browserPath = path
			break
		}
	}
	if browserPath == "" {
		t.Fatal("Chrome or Chromium is required")
	}

	for _, transport := range []string{"pipe", "epic-websocket", "psn-websocket"} {
		t.Run(transport, func(t *testing.T) {
			ctx, cancel := context.WithTimeout(context.Background(), 45*time.Second)
			defer cancel()
			profile := t.TempDir()
			options := append([]chromedp.ExecAllocatorOption{}, chromedp.DefaultExecAllocatorOptions[:]...)
			var err error
			if transport == "epic-websocket" {
				options, err = epicBrowserAllocatorOptions(profile)
			}
			if transport == "psn-websocket" {
				options, err = psnBrowserAllocatorOptions(profile)
			}
			if err != nil {
				t.Fatal(err)
			}
			port, err := reserveLocalTCPPort()
			if err != nil {
				t.Fatal(err)
			}
			options = append(options, chromedp.ExecPath(browserPath), chromedp.UserDataDir(profile),
				chromedp.Flag("headless", true), chromedp.Flag("no-sandbox", true))
			if transport != "pipe" {
				options = append(options, chromedp.Flag("remote-debugging-port", fmt.Sprint(port)))
			}
			allocator, stopAllocator := chromedp.NewExecAllocator(ctx, options...)
			defer stopAllocator()
			browser, stopBrowser := chromedp.NewContext(allocator)
			defer closeConnectBrowser(browser, stopBrowser)
			if _, err := chromedp.Call(browser, network.Enable, network.EnableParams{}); err != nil {
				t.Fatal(err)
			}

			// Intercept every page request. No provider site receives a request or a credential.
			interceptCtx, stopIntercept := context.WithCancel(browser)
			defer stopIntercept()
			requests := chromedp.Events(interceptCtx, fetch.RequestPaused)
			interceptDone := make(chan struct{})
			go func() {
				defer close(interceptDone)
				for event, eventErr := range requests {
					if eventErr != nil {
						return
					}
					body := `<html><body><script>window.fetch = () => Promise.resolve({ok:true,json:async()=>({username:"Fixture user"})});</script></body></html>`
					status := int64(http.StatusOK)
					if strings.Contains(event.Request.URL, "epicgames.com") {
						body = `<html><body>{"authorizationCode":"fixture-code"}</body></html>`
					}
					if strings.Contains(event.Request.URL, "account.sony.com") {
						status = http.StatusTooManyRequests
					}
					_, callErr := chromedp.Call(interceptCtx, fetch.FulfillRequest, fetch.FulfillRequestParams{
						RequestID: event.RequestID, ResponseCode: status, Body: []byte(body),
						ResponseHeaders: []*fetch.HeaderEntry{{Name: "Content-Type", Value: "text/html"}},
					})
					if callErr != nil {
						return
					}
				}
			}()
			defer func() { stopIntercept(); <-interceptDone }()
			if _, err := chromedp.Call(browser, fetch.Enable, fetch.EnableParams{}); err != nil {
				t.Fatal(err)
			}
			for _, cookie := range []network.SetCookieParams{
				{Name: "gog-al", Value: "fixture-gog", URL: "https://www.gog.com"},
				{Name: "pdccws_p", Value: "fixture-psn", URL: "https://store.playstation.com"},
				{Name: "unrelated", Value: "fixture-unrelated", URL: "https://store.playstation.com"},
			} {
				if _, err := chromedp.Call(browser, network.SetCookie, cookie); err != nil {
					t.Fatal(err)
				}
			}
			cookie, err := readGogAlCookie(browser)
			if err != nil || cookie == nil || cookie.Value != "fixture-gog" {
				t.Fatalf("GOG cookie: %v, %v", cookie, err)
			}
			cookies, _, err := readPsnSessionCookies(browser)
			if err != nil || len(cookies) != 1 || cookies["pdccws_p"] != "fixture-psn" {
				t.Fatalf("PSN cookies: %v, %v", cookies, err)
			}
			if err := chromedp.Do(browser, chromedp.Navigate(gogConnectStartURL)); err != nil {
				t.Fatal(err)
			}
			username, err := readGogUsername(browser)
			if err != nil || username != "Fixture user" {
				t.Fatalf("GOG username: %q, %v", username, err)
			}
			if err := chromedp.Do(browser, chromedp.Navigate(epicRedirectAPIURL)); err != nil {
				t.Fatal(err)
			}
			code, err := readEpicAuthorizationCode(browser)
			if err != nil || code != "fixture-code" {
				t.Fatalf("Epic code: %q, %v", code, err)
			}
			location, err := readCurrentPsnBrowserURL(browser)
			if err != nil || location != epicRedirectAPIURL {
				t.Fatalf("location: %q, %v", location, err)
			}

			diagnosticsPath := filepath.Join(t.TempDir(), "diagnostics.jsonl")
			diagnostics, err := newPsnConnectDiagnostics(diagnosticsPath)
			if err != nil {
				t.Fatal(err)
			}
			defer diagnostics.Close()
			rateLimits := make(chan string, 1)
			stopEvents := monitorPsnNetworkEvents(browser, diagnostics, rateLimits)
			defer stopEvents()
			if err := chromedp.Do(browser, chromedp.Navigate("https://account.sony.com/fixture?secret=hidden")); err != nil {
				t.Fatal(err)
			}
			select {
			case <-rateLimits:
			case <-ctx.Done():
				t.Fatal("Sony 429 response was not delivered")
			}
			stopEvents()
			diagnostics.Close()
			log, err := os.ReadFile(diagnosticsPath)
			if err != nil || !strings.Contains(string(log), `"status":429`) || strings.Contains(string(log), "secret=hidden") {
				t.Fatalf("sanitized response diagnostics missing: %s, %v", log, err)
			}

			if transport == "psn-websocket" {
				remoteAllocator, stopRemote, _, err := newPsnBrowserAllocator(ctx, "", fmt.Sprintf("http://127.0.0.1:%d", port))
				if err != nil {
					t.Fatal(err)
				}
				defer stopRemote()
				remoteBrowser, stopRemoteBrowser := chromedp.NewContext(remoteAllocator)
				defer stopRemoteBrowser()
				if _, _, err := readPsnSessionCookies(remoteBrowser); err != nil {
					t.Fatalf("remote browser: %v", err)
				}
				closeConnectBrowser(remoteBrowser, stopRemoteBrowser)
				if _, err := readCurrentPsnBrowserURL(browser); err != nil {
					t.Fatalf("remote cleanup closed the existing browser: %v", err)
				}
			}
		})
	}
}
