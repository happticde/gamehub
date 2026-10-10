package cmd

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"reflect"
	"strconv"
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
	if request.StatusPort != helperLaunchStatusPort {
		t.Fatalf("legacy launch status port = %d", request.StatusPort)
	}
}

func TestParseHelperLaunchURL_StatusPort_RequiresDynamicPortAndLaunchID(t *testing.T) {
	for _, port := range []string{"49152", "65535"} {
		request, err := parseHelperLaunchURL("gamehub-helper://connect/psn?launchId=launch-1234567890&statusPort=" + port)
		if err != nil || strconv.Itoa(request.StatusPort) != port {
			t.Fatalf("port %s: request = %+v, error = %v", port, request, err)
		}
	}
	for _, query := range []string{
		"launchId=launch-1234567890&statusPort=80",
		"launchId=launch-1234567890&statusPort=65536",
		"launchId=launch-1234567890&statusPort=abc",
		"launchId=launch-1234567890&statusPort=",
		"launchId=launch-1234567890&statusPort=55000&statusPort=55001",
		"statusPort=55000",
	} {
		if _, err := parseHelperLaunchURL("gamehub-helper://connect/psn?" + query); err == nil {
			t.Fatalf("invalid port query accepted: %s", query)
		}
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

func TestParseHelperLaunchURL_EpicWithOrigin_NormalizesOrigin(t *testing.T) {
	request, err := parseHelperLaunchURL("gamehub-helper://connect/epic?origin=http%3A%2F%2Flocalhost%3A3005&launchId=launch-1234567890")
	if err != nil {
		t.Fatalf("parseHelperLaunchURL returned error: %v", err)
	}

	if request.Platform != "epic" {
		t.Fatalf("Platform = %q, want epic", request.Platform)
	}
	if request.Origin != "http://localhost:3005" {
		t.Fatalf("Origin = %q, want http://localhost:3005", request.Origin)
	}
	if request.LaunchID != "launch-1234567890" {
		t.Fatalf("LaunchID = %q, want launch-1234567890", request.LaunchID)
	}
}

func TestParseHelperLaunchURL_RejectsSecretParameters(t *testing.T) {
	secretParameters := []string{"authorizationCode", "code", "cookie", "cookies", "gog-al", "gogAl", "gogAlCookie", "npsso", "psnAuthBearerToken", "JSESSIONID", "pdccws_p", "sb-cdn.session", "psn-csrf-token", "accessToken", "refreshToken", "password", "token", "nonce", "handoffUrl", "resultUrl"}
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
	_, err := parseHelperLaunchURL("gamehub-helper://connect/steam?origin=http%3A%2F%2Flocalhost%3A3005")
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

func TestFinalizeMacOSHelperApp_SignsFinalInfoPlistBeforeVerification(t *testing.T) {
	appPath := filepath.Join(t.TempDir(), "GameHub Helper.app")
	if err := os.MkdirAll(filepath.Join(appPath, "Contents"), 0755); err != nil {
		t.Fatal(err)
	}
	var calls [][]string
	err := finalizeMacOSHelperApp(appPath, func(name string, args ...string) ([]byte, error) {
		plist, err := os.ReadFile(filepath.Join(appPath, "Contents", "Info.plist"))
		if err != nil || string(plist) != macOSHelperInfoPlist() {
			t.Fatal("the protocol Info.plist must be finalized before signing")
		}
		calls = append(calls, append([]string{name}, args...))
		return nil, nil
	})
	if err != nil {
		t.Fatal(err)
	}
	expected := [][]string{
		{"codesign", "--force", "--sign", "-", "--timestamp=none", appPath},
		{"codesign", "--verify", "--strict", appPath},
	}
	if !reflect.DeepEqual(calls, expected) {
		t.Fatalf("finalization commands = %v, want %v", calls, expected)
	}
}

func TestFinalizeMacOSHelperApp_StopsOnSigningOrVerificationFailure(t *testing.T) {
	for _, failingCall := range []int{1, 2} {
		t.Run(fmt.Sprint(failingCall), func(t *testing.T) {
			appPath := filepath.Join(t.TempDir(), "GameHub Helper.app")
			if err := os.MkdirAll(filepath.Join(appPath, "Contents"), 0755); err != nil {
				t.Fatal(err)
			}
			calls := 0
			err := finalizeMacOSHelperApp(appPath, func(name string, args ...string) ([]byte, error) {
				calls++
				if calls == failingCall {
					return []byte("signature rejected"), errors.New("exit status 1")
				}
				return nil, nil
			})
			if err == nil || !strings.Contains(err.Error(), "signature rejected") || calls != failingCall {
				t.Fatalf("failed finalization: error = %v, calls = %d", err, calls)
			}
		})
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
	if helperLaunchStatusPath("epic") != "/connect/epic/launch-status" {
		t.Fatalf("unexpected Epic Games launch status path")
	}
}

func TestHelperLaunchStatusHandler_BusyStatusDoesNotExposePreviousLaunch(t *testing.T) {
	state := &helperLaunchStatusState{}
	state.ready("http://127.0.0.1:55000/connect/psn/result?nonce=private-result-nonce")
	handler := state.handler("previous-launch-12345", mustParseGogHandoffOrigins(t, "https://gamehub.example.com"))
	for _, method := range []string{http.MethodGet, http.MethodOptions} {
		request := httptest.NewRequest(method, "/connect/psn/launch-status?launchId=next-launch-123456", nil)
		request.Header.Set("Origin", "https://gamehub.example.com")
		response := httptest.NewRecorder()
		handler(response, request)
		expected := http.StatusConflict
		if method == http.MethodOptions {
			expected = http.StatusNoContent
		}
		if response.Code != expected || response.Header().Get("Access-Control-Allow-Origin") != "https://gamehub.example.com" {
			t.Fatalf("busy %s status = %d, headers = %v", method, response.Code, response.Header())
		}
		if strings.Contains(response.Body.String(), "private-result-nonce") || strings.Contains(response.Body.String(), "previous-launch-12345") {
			t.Fatal("busy response exposed the previous handoff")
		}
	}
	request := httptest.NewRequest(http.MethodGet, "/connect/psn/launch-status?launchId=next-launch-123456", nil)
	request.Header.Set("Origin", "https://untrusted.example.com")
	response := httptest.NewRecorder()
	handler(response, request)
	if response.Code != http.StatusForbidden || response.Header().Get("Access-Control-Allow-Origin") != "" {
		t.Fatal("busy status must retain the origin boundary")
	}
}

func TestHelperLaunchStatusServers_ConcurrentAttemptsUseSeparatePorts(t *testing.T) {
	origin := "https://gamehub.example.com"
	allowed := mustParseGogHandoffOrigins(t, origin)
	client := &http.Client{Timeout: 2 * time.Second}
	for index, provider := range []string{"psn", "psn", "gog", "epic"} {
		// Reserve an OS-selected port before starting each independent status server.
		reservation, err := net.Listen("tcp4", "127.0.0.1:0")
		if err != nil {
			t.Fatal(err)
		}
		port := reservation.Addr().(*net.TCPAddr).Port
		_ = reservation.Close()
		launchID := fmt.Sprintf("concurrent-launch-%d", index)
		state, shutdown, err := startHelperLaunchStatusServer(provider, launchID, allowed, port)
		if err != nil {
			t.Fatal(err)
		}
		t.Cleanup(func() {
			ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
			defer cancel()
			_ = shutdown(ctx)
		})
		state.ready(fmt.Sprintf("http://127.0.0.1:60000/connect/%s/result?nonce=result-%d", provider, index))
		request, _ := http.NewRequest(http.MethodGet, fmt.Sprintf("http://127.0.0.1:%d%s?launchId=%s", port, helperLaunchStatusPath(provider), launchID), nil)
		request.Header.Set("Origin", origin)
		response, err := client.Do(request)
		if err != nil {
			t.Fatal(err)
		}
		var payload map[string]string
		err = json.NewDecoder(response.Body).Decode(&payload)
		_ = response.Body.Close()
		if err != nil || response.StatusCode != http.StatusOK || !strings.HasSuffix(payload["handoffUrl"], fmt.Sprintf("result-%d", index)) {
			t.Fatalf("concurrent attempt %d received another attempt's status: %+v, error = %v", index, payload, err)
		}
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
