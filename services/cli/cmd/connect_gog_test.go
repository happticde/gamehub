package cmd

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func TestNewGogConnectPayload_WithPersistentCookie_IncludesContractFields(t *testing.T) {
	capturedAt := time.Date(2026, 7, 3, 12, 0, 0, 0, time.UTC)
	expiresAt := time.Date(2026, 7, 4, 12, 0, 0, 0, time.UTC)

	payload := newGogConnectPayload("gog-user", "secret-cookie", float64(expiresAt.Unix()), capturedAt)

	if payload.Provider != "gog" {
		t.Fatalf("Provider = %q, want gog", payload.Provider)
	}
	if payload.User != "gog-user" {
		t.Fatalf("User = %q, want gog-user", payload.User)
	}
	if payload.Cookies["gog-al"] != "secret-cookie" {
		t.Fatalf("gog-al cookie was not preserved in the helper payload")
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

func TestNewGogConnectPayload_WithSessionCookie_OmitsExpiry(t *testing.T) {
	capturedAt := time.Date(2026, 7, 3, 12, 0, 0, 0, time.UTC)

	payload := newGogConnectPayload("gog-user", "secret-cookie", -1, capturedAt)

	if payload.ExpiresAtUTC != "" {
		t.Fatalf("ExpiresAtUTC = %q, want empty for a session cookie", payload.ExpiresAtUTC)
	}
}

func TestGogHandoffHandler_PendingResult_RequiresAllowedOrigin(t *testing.T) {
	state := newGogHandoffState()
	handler := state.handler("nonce", mustParseGogHandoffOrigins(t, "http://localhost:3005,http://127.0.0.1:3005"))

	disallowedRequest := httptest.NewRequest(http.MethodGet, "/connect/gog/result?nonce=nonce", nil)
	disallowedRequest.Header.Set("Origin", "http://malicious.test")
	disallowedResponse := httptest.NewRecorder()
	handler.ServeHTTP(disallowedResponse, disallowedRequest)

	if disallowedResponse.Code != http.StatusForbidden {
		t.Fatalf("disallowed origin status = %d, want %d", disallowedResponse.Code, http.StatusForbidden)
	}

	allowedRequest := httptest.NewRequest(http.MethodGet, "/connect/gog/result?nonce=nonce", nil)
	allowedRequest.Header.Set("Origin", "http://127.0.0.1:3005")
	allowedRequest.Header.Set("Access-Control-Request-Private-Network", "true")
	allowedResponse := httptest.NewRecorder()
	handler.ServeHTTP(allowedResponse, allowedRequest)

	if allowedResponse.Code != http.StatusAccepted {
		t.Fatalf("pending handoff status = %d, want %d", allowedResponse.Code, http.StatusAccepted)
	}
	if allowedResponse.Header().Get("Access-Control-Allow-Origin") != "http://127.0.0.1:3005" {
		t.Fatalf("allowed origin CORS header was not set")
	}
	if allowedResponse.Header().Get("Access-Control-Allow-Private-Network") != "true" {
		t.Fatalf("private network access header was not set")
	}
}

func TestGogHandoffHandler_RejectsSelfDeclaredPrivateOrigin(t *testing.T) {
	state := newGogHandoffState()
	handler := state.handler("nonce", mustParseGogHandoffOrigins(t, "http://localhost:3005"))

	request := httptest.NewRequest(http.MethodGet, "/connect/gog/result?nonce=nonce&origin=http%3A%2F%2F192.168.1.25%3A3005", nil)
	request.Header.Set("Origin", "http://192.168.1.25:3005")
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)

	if response.Code != http.StatusForbidden {
		t.Fatalf("private origin handoff status = %d, want %d", response.Code, http.StatusForbidden)
	}
	if response.Header().Get("Access-Control-Allow-Origin") != "" {
		t.Fatalf("unapproved private origin received CORS permission")
	}
}

func TestGogHandoffHandler_RejectsMismatchedNonceBoundOrigin(t *testing.T) {
	state := newGogHandoffState()
	handler := state.handler("nonce", mustParseGogHandoffOrigins(t, "http://localhost:3005"))

	request := httptest.NewRequest(http.MethodGet, "/connect/gog/result?nonce=nonce&origin=http%3A%2F%2F192.168.1.25%3A3005", nil)
	request.Header.Set("Origin", "http://192.168.1.26:3005")
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)

	if response.Code != http.StatusForbidden {
		t.Fatalf("mismatched origin status = %d, want %d", response.Code, http.StatusForbidden)
	}
}

func TestGogHandoffHandler_CompletedResult_IsAvailableOnce(t *testing.T) {
	state := newGogHandoffState()
	state.complete(newGogConnectPayload(
		"gog-user",
		"secret-cookie",
		float64(time.Date(2026, 7, 4, 12, 0, 0, 0, time.UTC).Unix()),
		time.Date(2026, 7, 3, 12, 0, 0, 0, time.UTC),
	))
	handler := state.handler("nonce", mustParseGogHandoffOrigins(t, "http://localhost:3005"))

	firstRequest := httptest.NewRequest(http.MethodGet, "/connect/gog/result?nonce=nonce", nil)
	firstRequest.Header.Set("Origin", "http://localhost:3005")
	firstResponse := httptest.NewRecorder()
	handler.ServeHTTP(firstResponse, firstRequest)

	if firstResponse.Code != http.StatusOK {
		t.Fatalf("first handoff status = %d, want %d", firstResponse.Code, http.StatusOK)
	}

	var payload gogConnectPayload
	if err := json.Unmarshal(firstResponse.Body.Bytes(), &payload); err != nil {
		t.Fatalf("failed to decode first handoff payload: %v", err)
	}
	if payload.Cookies["gog-al"] != "secret-cookie" {
		t.Fatalf("handoff cookie was not returned in the one-time payload")
	}

	waitCtx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	if err := state.waitConsumed(waitCtx); err != nil {
		t.Fatalf("handoff state was not marked consumed: %v", err)
	}

	secondRequest := httptest.NewRequest(http.MethodGet, "/connect/gog/result?nonce=nonce", nil)
	secondRequest.Header.Set("Origin", "http://localhost:3005")
	secondResponse := httptest.NewRecorder()
	handler.ServeHTTP(secondResponse, secondRequest)

	if secondResponse.Code != http.StatusGone {
		t.Fatalf("second handoff status = %d, want %d", secondResponse.Code, http.StatusGone)
	}
}

func TestGogHandoffHandler_FailedResult_ReturnsMessageOnce(t *testing.T) {
	state := newGogHandoffState()
	state.fail(errors.New("provider returned HTTP 429"))
	handler := state.handler("nonce", mustParseGogHandoffOrigins(t, "http://localhost:3005"))

	firstRequest := httptest.NewRequest(http.MethodGet, "/connect/gog/result?nonce=nonce", nil)
	firstRequest.Header.Set("Origin", "http://localhost:3005")
	firstResponse := httptest.NewRecorder()
	handler.ServeHTTP(firstResponse, firstRequest)

	if firstResponse.Code != http.StatusInternalServerError {
		t.Fatalf("first failed handoff status = %d, want %d", firstResponse.Code, http.StatusInternalServerError)
	}

	var payload map[string]string
	if err := json.Unmarshal(firstResponse.Body.Bytes(), &payload); err != nil {
		t.Fatalf("failed to decode failed handoff payload: %v", err)
	}
	if payload["status"] != "failed" {
		t.Fatalf("status = %q, want failed", payload["status"])
	}
	if payload["message"] != "provider returned HTTP 429" {
		t.Fatalf("message = %q, want provider error", payload["message"])
	}

	waitCtx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	if err := state.waitConsumed(waitCtx); err != nil {
		t.Fatalf("failed handoff state was not marked consumed: %v", err)
	}

	secondRequest := httptest.NewRequest(http.MethodGet, "/connect/gog/result?nonce=nonce", nil)
	secondRequest.Header.Set("Origin", "http://localhost:3005")
	secondResponse := httptest.NewRecorder()
	handler.ServeHTTP(secondResponse, secondRequest)

	if secondResponse.Code != http.StatusGone {
		t.Fatalf("second failed handoff status = %d, want %d", secondResponse.Code, http.StatusGone)
	}
}

func TestParseGogHandoffOrigins_RejectsInvalidOrigin(t *testing.T) {
	if _, err := parseGogHandoffOrigins("http://localhost:3005/path"); err == nil {
		t.Fatalf("expected invalid handoff origin to fail")
	}
}

func mustParseGogHandoffOrigins(t *testing.T, value string) map[string]struct{} {
	t.Helper()
	origins, err := parseGogHandoffOrigins(value)
	if err != nil {
		t.Fatalf("failed to parse test handoff origins: %v", err)
	}
	return origins
}
