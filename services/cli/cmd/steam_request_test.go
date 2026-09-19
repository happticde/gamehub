package cmd

import (
	"context"
	"io"
	"net/http"
	"strings"
	"testing"
)

func TestSteamRequestKeepsApiKeyOutOfUrl(t *testing.T) {
	const apiKey = "secret-steam-key"
	var received *http.Request
	client := &http.Client{Transport: roundTripFunc(func(request *http.Request) (*http.Response, error) {
		received = request
		return &http.Response{
			StatusCode: http.StatusOK,
			Body:       io.NopCloser(strings.NewReader(`{"response":{"games":[]}}`)),
			Header:     make(http.Header),
		}, nil
	})}

	result, err := steamFetchGames(context.Background(), client, apiKey, "123")
	if err != nil {
		t.Fatal(err)
	}
	if len(result.Response.Games) != 0 {
		t.Fatalf("expected no games, got %d", len(result.Response.Games))
	}

	if received == nil {
		t.Fatal("request was not sent")
	}
	if got := received.Header.Get("x-webapi-key"); got != apiKey {
		t.Fatalf("API key header = %q, want %q", got, apiKey)
	}
	if strings.Contains(received.URL.String(), apiKey) {
		t.Fatalf("API key leaked into URL: %s", received.URL)
	}
}

func TestSteamRequestDoesNotFollowRedirectWithApiKey(t *testing.T) {
	requests := 0
	client := &http.Client{Transport: roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests++
		return &http.Response{
			StatusCode: http.StatusFound,
			Header:     http.Header{"Location": []string{"https://example.test/redirect"}},
			Body:       io.NopCloser(strings.NewReader("")),
			Request:    request,
		}, nil
	})}

	if _, err := steamFetchGames(context.Background(), client, "secret-steam-key", "123"); err == nil {
		t.Fatal("expected redirect response to fail the Steam request")
	}
	if requests != 1 {
		t.Fatalf("Steam request followed a redirect %d times, want 1", requests)
	}
}

type roundTripFunc func(*http.Request) (*http.Response, error)

func (f roundTripFunc) RoundTrip(request *http.Request) (*http.Response, error) {
	return f(request)
}
