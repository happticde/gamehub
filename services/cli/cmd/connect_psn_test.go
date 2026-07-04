package cmd

import (
	"encoding/json"
	"testing"
	"time"
)

func TestNewPsnConnectPayload_WithPersistentCookie_IncludesContractFields(t *testing.T) {
	capturedAt := time.Date(2026, 7, 3, 12, 0, 0, 0, time.UTC)
	expiresAt := time.Date(2026, 7, 4, 12, 0, 0, 0, time.UTC)

	payload := newPsnConnectPayload(
		map[string]string{
			"npsso":          "secret-npsso",
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
