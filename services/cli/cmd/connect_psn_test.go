package cmd

import (
	"context"
	"encoding/json"
	"net"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"testing"
	"time"
)

func TestNewPsnConnectPayload_WithPersistentCookie_IncludesContractFields(t *testing.T) {
	capturedAt := time.Date(2026, 7, 3, 12, 0, 0, 0, time.UTC)
	expiresAt := time.Date(2026, 7, 4, 12, 0, 0, 0, time.UTC)

	payload := newPsnConnectPayload(
		map[string]string{
			"npsso":          "secret-npsso",
			"pdccws_p":       "secret-pdccws",
			"sb-cdn.session": "secret-session",
		},
		float64(expiresAt.Unix()),
		capturedAt,
	)

	if payload.Provider != "psn" {
		t.Fatalf("Provider = %q, want psn", payload.Provider)
	}
	if payload.Cookies["npsso"] != "secret-npsso" {
		t.Fatalf("npsso cookie was not preserved in the helper payload")
	}
	if payload.Cookies["pdccws_p"] != "secret-pdccws" {
		t.Fatalf("pdccws_p cookie was not preserved in the helper payload")
	}
	if payload.CapturedAtUTC != "2026-07-03T12:00:00Z" {
		t.Fatalf("CapturedAtUTC = %q, want 2026-07-03T12:00:00Z", payload.CapturedAtUTC)
	}
	if payload.ExpiresAtUTC != "2026-07-04T12:00:00Z" {
		t.Fatalf("ExpiresAtUTC = %q, want 2026-07-04T12:00:00Z", payload.ExpiresAtUTC)
	}

	encoded, err := json.Marshal(payload)
	if err != nil {
		t.Fatalf("failed to marshal payload: %v", err)
	}
	if !json.Valid(encoded) {
		t.Fatalf("payload did not marshal to valid JSON")
	}
}

func TestNewPsnConnectPayload_WithSessionCookie_OmitsExpiry(t *testing.T) {
	capturedAt := time.Date(2026, 7, 3, 12, 0, 0, 0, time.UTC)

	payload := newPsnConnectPayload(map[string]string{"npsso": "secret-npsso"}, -1, capturedAt)

	if payload.ExpiresAtUTC != "" {
		t.Fatalf("ExpiresAtUTC = %q, want empty for a session cookie", payload.ExpiresAtUTC)
	}
}

func TestReserveLocalTCPPort_ReturnsUsableLoopbackPort(t *testing.T) {
	port, err := reserveLocalTCPPort()
	if err != nil {
		t.Fatalf("reserveLocalTCPPort returned error: %v", err)
	}
	if port <= 0 {
		t.Fatalf("port = %d, want positive port", port)
	}

	listener, err := net.Listen("tcp4", "127.0.0.1:"+strconv.Itoa(port))
	if err != nil {
		t.Fatalf("reserved port was not reusable after release: %v", err)
	}
	_ = listener.Close()
}

func TestIsPsnLoginRateLimit_DetectsSonyAccount429(t *testing.T) {
	if !isPsnLoginRateLimit("https://ca.account.sony.com/api/v1/ssocookie", 429) {
		t.Fatalf("expected Sony account 429 to be detected")
	}
	if isPsnLoginRateLimit("https://ca.account.sony.com/api/v1/ssocookie", 200) {
		t.Fatalf("expected non-429 Sony account response to be ignored")
	}
	if isPsnLoginRateLimit("https://example.test/api", 429) {
		t.Fatalf("expected unrelated 429 response to be ignored")
	}
}

func TestNormalizePsnRemoteDebugURL_AllowsLoopbackHTTPAndWS(t *testing.T) {
	httpURL, err := normalizePsnRemoteDebugURL("http://127.0.0.1:9222/json/version")
	if err != nil {
		t.Fatalf("normalizePsnRemoteDebugURL returned error: %v", err)
	}
	if httpURL != "http://127.0.0.1:9222" {
		t.Fatalf("httpURL = %q, want http://127.0.0.1:9222", httpURL)
	}

	wsURL, err := normalizePsnRemoteDebugURL("ws://localhost:9222")
	if err != nil {
		t.Fatalf("normalizePsnRemoteDebugURL returned error: %v", err)
	}
	if wsURL != "ws://localhost:9222" {
		t.Fatalf("wsURL = %q, want ws://localhost:9222", wsURL)
	}
}

func TestNormalizePsnRemoteDebugURL_RejectsRemoteHosts(t *testing.T) {
	if _, err := normalizePsnRemoteDebugURL("http://192.168.1.10:9222"); err == nil {
		t.Fatalf("expected private non-loopback remote debugging URL to be rejected")
	}
	if _, err := normalizePsnRemoteDebugURL("https://127.0.0.1:9222"); err == nil {
		t.Fatalf("expected unsupported scheme to be rejected")
	}
}

func TestResolvePsnRemoteDebugURL_ReadsBrowserWebSocketURL(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/json/version" {
			t.Fatalf("requested path = %q, want /json/version", r.URL.Path)
		}
		_, _ = w.Write([]byte(`{"webSocketDebuggerUrl":"ws://127.0.0.1:9222/devtools/browser/test-browser"}`))
	}))
	t.Cleanup(server.Close)

	wsURL, err := resolvePsnRemoteDebugURL(context.Background(), server.URL)
	if err != nil {
		t.Fatalf("resolvePsnRemoteDebugURL returned error: %v", err)
	}
	if wsURL != "ws://127.0.0.1:9222/devtools/browser/test-browser" {
		t.Fatalf("wsURL = %q, want browser websocket URL", wsURL)
	}
}

func TestResolvePsnRemoteDebugURL_ExplainsBadDebugEndpoint(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Error(w, "not chrome", http.StatusNotFound)
	}))
	t.Cleanup(server.Close)

	_, err := resolvePsnRemoteDebugURL(context.Background(), server.URL)
	if err == nil {
		t.Fatalf("expected bad debug endpoint to be rejected")
	}
	if !strings.Contains(err.Error(), "/json/version") {
		t.Fatalf("error = %q, want /json/version hint", err.Error())
	}
}
