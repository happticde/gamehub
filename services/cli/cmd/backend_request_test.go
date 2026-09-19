package cmd

import (
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestPostToBackendRejectsRedirectWithoutForwardingAPIKey(t *testing.T) {
	forwarded := make(chan string, 1)
	destination := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		forwarded <- r.Header.Get("X-Api-Key")
		w.WriteHeader(http.StatusOK)
	}))
	defer destination.Close()
	backend := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("X-Api-Key") != "test-gamehub-key" {
			t.Error("initial backend request did not receive the API key")
		}
		http.Redirect(w, r, destination.URL, http.StatusTemporaryRedirect)
	}))
	defer backend.Close()

	previousURL, previousKey := backendUrl, apiKeyFlag
	backendUrl, apiKeyFlag = backend.URL, "test-gamehub-key"
	defer func() { backendUrl, apiKeyFlag = previousURL, previousKey }()
	client := backend.Client()

	if err := PostToBackend(client, "steam", []SteamGame{{AppId: 42}}); err == nil {
		t.Fatal("expected redirect rejection")
	}
	select {
	case key := <-forwarded:
		t.Fatalf("redirect destination received an unexpected request with key %q", key)
	default:
	}
	if client.CheckRedirect != nil {
		t.Error("upload changed the caller's redirect policy")
	}
}
