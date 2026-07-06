package cmd

import (
	"strings"
	"testing"
	"time"
)

func TestNewEpicConnectPayload_ContainsOnlyAuthorizationCode(t *testing.T) {
	capturedAt := time.Date(2026, 7, 6, 12, 0, 0, 0, time.UTC)
	payload := newEpicConnectPayload("short-lived-code", capturedAt)

	if payload.Provider != "epic" {
		t.Fatalf("Provider = %q, want epic", payload.Provider)
	}
	if payload.AuthorizationCode != "short-lived-code" {
		t.Fatalf("authorization code was not preserved in the helper payload")
	}
	if payload.CapturedAtUTC != "2026-07-06T12:00:00Z" {
		t.Fatalf("CapturedAtUTC = %q, want 2026-07-06T12:00:00Z", payload.CapturedAtUTC)
	}
	if payload.ExpiresAtUTC != "2026-07-06T12:10:00Z" {
		t.Fatalf("ExpiresAtUTC = %q, want 2026-07-06T12:10:00Z", payload.ExpiresAtUTC)
	}
}

func TestReadEpicAuthorizationCodeFromBody_ExtractsOnlyAuthorizationCode(t *testing.T) {
	code, err := readEpicAuthorizationCodeFromBody(`{
		"warning": "Do not share this code with any 3rd party service.",
		"authorizationCode": " short-lived-code ",
		"accessToken": "must-not-be-used",
		"refreshToken": "must-not-be-used"
	}`)
	if err != nil {
		t.Fatalf("readEpicAuthorizationCodeFromBody returned error: %v", err)
	}
	if code != "short-lived-code" {
		t.Fatalf("code = %q, want short-lived-code", code)
	}
}

func TestReadEpicAuthorizationCodeFromBody_IgnoresMissingCode(t *testing.T) {
	code, err := readEpicAuthorizationCodeFromBody(`{"exchangeCode": null}`)
	if err != nil {
		t.Fatalf("readEpicAuthorizationCodeFromBody returned error: %v", err)
	}
	if code != "" {
		t.Fatalf("code = %q, want empty", code)
	}
}

func TestIsEpicRedirectAPIURL_MatchesLauncherRedirectOnly(t *testing.T) {
	if !isEpicRedirectAPIURL(epicRedirectAPIURL) {
		t.Fatalf("expected launcher redirect API URL to match")
	}
	if isEpicRedirectAPIURL("https://www.epicgames.com/id/api/redirect?clientId=other&responseType=code") {
		t.Fatalf("unexpected match for a different Epic client")
	}
	if isEpicRedirectAPIURL("https://www.epicgames.com/id/login") {
		t.Fatalf("unexpected match for the Epic login page")
	}
}

func TestEpicConnectStartURL_TargetsRedirectAPIWithoutSecrets(t *testing.T) {
	startURL := epicConnectStartURL()
	if !strings.HasPrefix(startURL, "https://www.epicgames.com/id/login?") {
		t.Fatalf("startURL = %q, want Epic login URL", startURL)
	}
	if strings.Contains(strings.ToLower(startURL), "authorizationcode") ||
		strings.Contains(strings.ToLower(startURL), "refreshtoken") ||
		strings.Contains(strings.ToLower(startURL), "accesstoken") {
		t.Fatalf("start URL must not contain provider secrets: %s", startURL)
	}
}

func TestEpicBrowserLaunchFlags_UsesMinimalChromeFingerprint(t *testing.T) {
	flags := epicBrowserLaunchFlags("/tmp/gamehub-epic-connect-test", 49334)
	values := make(map[string]any)
	for _, flag := range flags {
		values[flag.Name] = flag.Value
	}

	if values["remote-debugging-address"] != "127.0.0.1" {
		t.Fatalf("remote-debugging-address = %v, want 127.0.0.1", values["remote-debugging-address"])
	}
	if values["remote-debugging-port"] != "49334" {
		t.Fatalf("remote-debugging-port = %v, want 49334", values["remote-debugging-port"])
	}
	if values["user-data-dir"] != "/tmp/gamehub-epic-connect-test" {
		t.Fatalf("user-data-dir = %v, want test profile dir", values["user-data-dir"])
	}
	if values["no-default-browser-check"] != true {
		t.Fatalf("no-default-browser-check = %v, want true", values["no-default-browser-check"])
	}
	if values["no-first-run"] != true {
		t.Fatalf("no-first-run = %v, want true", values["no-first-run"])
	}

	rejectedFlags := []string{
		"disable-blink-features",
		"disable-extensions",
		"disable-sync",
		"enable-automation",
		"headless",
		"metrics-recording-only",
	}
	for _, flag := range rejectedFlags {
		if _, ok := values[flag]; ok {
			t.Fatalf("Epic browser launch unexpectedly includes %s", flag)
		}
	}
}
