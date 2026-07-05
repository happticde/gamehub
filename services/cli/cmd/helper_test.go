package cmd

import (
	"bytes"
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestParseHelperLaunchURL_GogWithOrigin_NormalizesOrigin(t *testing.T) {
	request, err := parseHelperLaunchURL("gamehub-helper://connect/gog?origin=http%3A%2F%2Flocalhost%3A3005&launchId=launch-1234567890")
	if err != nil {
		t.Fatalf("parseHelperLaunchURL returned error: %v", err)
	}

	if request.Platform != "gog" {
		t.Fatalf("Platform = %q, want gog", request.Platform)
	}
	if request.Origin != "http://localhost:3005" {
		t.Fatalf("Origin = %q, want http://localhost:3005", request.Origin)
	}
	if request.LaunchID != "launch-1234567890" {
		t.Fatalf("LaunchID = %q, want launch-1234567890", request.LaunchID)
	}
}

func TestParseHelperLaunchURL_GogWithoutOrigin_UsesDefaultLater(t *testing.T) {
	request, err := parseHelperLaunchURL("gamehub-helper://connect/gog")
	if err != nil {
		t.Fatalf("parseHelperLaunchURL returned error: %v", err)
	}

	if request.Platform != "gog" {
		t.Fatalf("Platform = %q, want gog", request.Platform)
	}
	if request.Origin != "" {
		t.Fatalf("Origin = %q, want empty", request.Origin)
	}
}

func TestParseHelperLaunchURL_PsnWithOrigin_NormalizesOrigin(t *testing.T) {
	request, err := parseHelperLaunchURL("gamehub-helper://connect/psn?origin=http%3A%2F%2Flocalhost%3A3005&launchId=launch-1234567890")
	if err != nil {
		t.Fatalf("parseHelperLaunchURL returned error: %v", err)
	}

	if request.Platform != "psn" {
		t.Fatalf("Platform = %q, want psn", request.Platform)
	}
	if request.Origin != "http://localhost:3005" {
		t.Fatalf("Origin = %q, want http://localhost:3005", request.Origin)
	}
	if request.LaunchID != "launch-1234567890" {
		t.Fatalf("LaunchID = %q, want launch-1234567890", request.LaunchID)
	}
}

func TestParseHelperLaunchURL_RejectsSecretParameters(t *testing.T) {
	secretParameters := []string{"cookie", "cookies", "gog-al", "gogAl", "gogAlCookie", "npsso", "psnAuthBearerToken", "JSESSIONID", "pdccws_p", "sb-cdn.session", "psn-csrf-token", "password", "token", "nonce", "handoffUrl", "resultUrl"}
	for _, parameter := range secretParameters {
		t.Run(parameter, func(t *testing.T) {
			_, err := parseHelperLaunchURL("gamehub-helper://connect/psn?" + parameter + "=secret")
			if err == nil {
				t.Fatalf("expected %q parameter to be rejected", parameter)
			}
			if !strings.Contains(err.Error(), "must not contain secret parameter") {
				t.Fatalf("error = %q, want secret-parameter rejection", err)
			}
		})
	}
}

func TestParseHelperLaunchURL_RejectsInvalidLaunchID(t *testing.T) {
	_, err := parseHelperLaunchURL("gamehub-helper://connect/gog?launchId=short")
	if err == nil {
		t.Fatalf("expected short launchId to fail")
	}
}

func TestParseHelperLaunchURL_RejectsUnsupportedTarget(t *testing.T) {
	_, err := parseHelperLaunchURL("gamehub-helper://connect/epic?origin=http%3A%2F%2Flocalhost%3A3005")
	if err == nil {
		t.Fatalf("expected unsupported target to fail")
	}
}

func TestParseHelperLaunchURL_RejectsUnsupportedParameter(t *testing.T) {
	_, err := parseHelperLaunchURL("gamehub-helper://connect/gog?returnTo=http%3A%2F%2Flocalhost%3A3005")
	if err == nil {
		t.Fatalf("expected unsupported parameter to fail")
	}
}

func TestMacOSHelperAppleScript_StartsLaunchCommandWithoutTerminalOrCookieData(t *testing.T) {
	script := macOSHelperAppleScript("/Applications/GameHub/gamehub-cli")

	if !strings.Contains(script, "helper launch") {
		t.Fatalf("AppleScript does not launch the helper command")
	}
	if strings.Contains(script, "Terminal") || strings.Contains(script, "do script") {
		t.Fatalf("AppleScript should not open Terminal for the normal helper launch")
	}
	if strings.Contains(script, "/tmp") {
		t.Fatalf("AppleScript should not write launch output to a local log file")
	}
	if strings.Contains(script, "gog-al") || strings.Contains(script, "cookie") {
		t.Fatalf("AppleScript should not embed cookie-related data")
	}
}

func TestConfigurePsnHelperLaunch_DefaultsToExistingChromeDebugEndpoint(t *testing.T) {
	psnRemoteDebugURL = ""
	psnHandoffServer = false
	psnHandoffOrigin = ""
	psnHandoffTTL = 0
	psnConnectTimeout = 0

	restore := configurePsnHelperLaunch(5*time.Minute, 7*time.Minute, "http://localhost:3005", "")
	defer restore()

	if psnRemoteDebugURL != defaultPsnRemoteDebugURL {
		t.Fatalf("psnRemoteDebugURL = %q, want %q", psnRemoteDebugURL, defaultPsnRemoteDebugURL)
	}
	if !psnHandoffServer {
		t.Fatalf("psnHandoffServer = false, want true")
	}
	if psnHandoffOrigin != "http://localhost:3005" {
		t.Fatalf("psnHandoffOrigin = %q, want http://localhost:3005", psnHandoffOrigin)
	}
	if psnConnectTimeout != 5*time.Minute {
		t.Fatalf("psnConnectTimeout = %s, want 5m", psnConnectTimeout)
	}
	if psnHandoffTTL != 7*time.Minute {
		t.Fatalf("psnHandoffTTL = %s, want 7m", psnHandoffTTL)
	}
}

func TestConfigurePsnHelperLaunch_AllowsCustomDebugEndpoint(t *testing.T) {
	restore := configurePsnHelperLaunch(time.Minute, time.Minute, "http://localhost:3005", " http://127.0.0.1:49444 ")
	defer restore()

	if psnRemoteDebugURL != "http://127.0.0.1:49444" {
		t.Fatalf("psnRemoteDebugURL = %q, want custom endpoint", psnRemoteDebugURL)
	}
}

func TestPreparePsnRemoteDebugChrome_SkipsLaunchWhenEndpointIsReady(t *testing.T) {
	restore := stubPsnChromePreparation(t, "darwin")
	defer restore()

	psnRemoteDebugEndpointReady = func(ctx context.Context, remoteDebugURL string) bool {
		return true
	}
	started := false
	startPsnRemoteDebugChrome = func(ctx context.Context, remoteDebugURL string) error {
		started = true
		return nil
	}

	if err := preparePsnRemoteDebugChrome(context.Background(), defaultPsnRemoteDebugURL, &bytes.Buffer{}); err != nil {
		t.Fatalf("preparePsnRemoteDebugChrome returned error: %v", err)
	}
	if started {
		t.Fatalf("Chrome should not be started when the debugging endpoint is already ready")
	}
}

func TestPreparePsnRemoteDebugChrome_StartsChromeOnMacWhenEndpointIsMissing(t *testing.T) {
	restore := stubPsnChromePreparation(t, "darwin")
	defer restore()

	checks := 0
	psnRemoteDebugEndpointReady = func(ctx context.Context, remoteDebugURL string) bool {
		checks++
		return checks > 1
	}
	startedWith := ""
	startPsnRemoteDebugChrome = func(ctx context.Context, remoteDebugURL string) error {
		startedWith = remoteDebugURL
		return nil
	}

	var stderr bytes.Buffer
	if err := preparePsnRemoteDebugChrome(context.Background(), defaultPsnRemoteDebugURL, &stderr); err != nil {
		t.Fatalf("preparePsnRemoteDebugChrome returned error: %v", err)
	}
	if startedWith != defaultPsnRemoteDebugURL {
		t.Fatalf("startedWith = %q, want %q", startedWith, defaultPsnRemoteDebugURL)
	}
	if !strings.Contains(stderr.String(), "Opening Chrome with local remote debugging enabled") {
		t.Fatalf("stderr did not explain Chrome launch: %s", stderr.String())
	}
}

func TestPreparePsnRemoteDebugChrome_DoesNotLaunchChromeOutsideMac(t *testing.T) {
	restore := stubPsnChromePreparation(t, "linux")
	defer restore()

	psnRemoteDebugEndpointReady = func(ctx context.Context, remoteDebugURL string) bool {
		return false
	}
	startPsnRemoteDebugChrome = func(ctx context.Context, remoteDebugURL string) error {
		t.Fatalf("Chrome should not be auto-started outside macOS")
		return nil
	}

	if err := preparePsnRemoteDebugChrome(context.Background(), defaultPsnRemoteDebugURL, &bytes.Buffer{}); err != nil {
		t.Fatalf("preparePsnRemoteDebugChrome returned error: %v", err)
	}
}

func TestDefaultPsnRemoteDebugProfileDir_UsesStableGameHubProfile(t *testing.T) {
	profileDir, err := defaultPsnRemoteDebugProfileDir()
	if err != nil {
		t.Fatalf("defaultPsnRemoteDebugProfileDir returned error: %v", err)
	}
	if !strings.Contains(profileDir, "GameHub") || !strings.HasSuffix(profileDir, psnChromeProfileDirName) {
		t.Fatalf("profileDir = %q, want stable GameHub PSN profile", profileDir)
	}
}

func TestHelperLaunchStatusHandler_ReturnsHandoffURLWithoutCookieData(t *testing.T) {
	state := &helperLaunchStatusState{}
	handler := state.handler("launch-1234567890", mustParseGogHandoffOrigins(t, "http://localhost:3005"))

	pendingRequest := httptest.NewRequest(http.MethodGet, "/connect/gog/launch-status?launchId=launch-1234567890", nil)
	pendingRequest.Header.Set("Origin", "http://localhost:3005")
	pendingResponse := httptest.NewRecorder()
	handler.ServeHTTP(pendingResponse, pendingRequest)

	if pendingResponse.Code != http.StatusAccepted {
		t.Fatalf("pending launch status = %d, want %d", pendingResponse.Code, http.StatusAccepted)
	}

	state.ready("http://127.0.0.1:49152/connect/gog/result?nonce=nonce-value")
	readyRequest := httptest.NewRequest(http.MethodGet, "/connect/gog/launch-status?launchId=launch-1234567890", nil)
	readyRequest.Header.Set("Origin", "http://localhost:3005")
	readyResponse := httptest.NewRecorder()
	handler.ServeHTTP(readyResponse, readyRequest)

	if readyResponse.Code != http.StatusOK {
		t.Fatalf("ready launch status = %d, want %d", readyResponse.Code, http.StatusOK)
	}

	var payload map[string]string
	if err := json.Unmarshal(readyResponse.Body.Bytes(), &payload); err != nil {
		t.Fatalf("failed to decode ready launch status: %v", err)
	}
	if payload["status"] != "handoff_ready" {
		t.Fatalf("status = %q, want handoff_ready", payload["status"])
	}
	if !strings.Contains(payload["handoffUrl"], "/connect/gog/result?nonce=") {
		t.Fatalf("handoffUrl did not contain the local result endpoint")
	}
	if strings.Contains(readyResponse.Body.String(), "gog-al") || strings.Contains(readyResponse.Body.String(), "secret-cookie") {
		t.Fatalf("launch status response must not contain provider cookie data")
	}
}

func TestHelperLaunchStatusPath_UsesProviderSpecificPath(t *testing.T) {
	if helperLaunchStatusPath("gog") != "/connect/gog/launch-status" {
		t.Fatalf("unexpected GOG launch status path")
	}
	if helperLaunchStatusPath("psn") != "/connect/psn/launch-status" {
		t.Fatalf("unexpected PSN launch status path")
	}
}

func stubPsnChromePreparation(t *testing.T, goos string) func() {
	t.Helper()

	previousGOOS := helperRuntimeGOOS
	previousEndpointReady := psnRemoteDebugEndpointReady
	previousStartChrome := startPsnRemoteDebugChrome
	helperRuntimeGOOS = goos

	return func() {
		helperRuntimeGOOS = previousGOOS
		psnRemoteDebugEndpointReady = previousEndpointReady
		startPsnRemoteDebugChrome = previousStartChrome
	}
}
