// Package cmd provides the CLI command definitions for gamehub-cli.
//
// This file contains shared helper functions used by all ingest commands
// to avoid code duplication — similar to how IngestControllerBase works
// on the backend side.
package cmd

import (
	"bytes"
	"encoding/json"
	"fmt"
	"net/http"
	"time"
)

// PostToBackend sends a JSON payload to the GameHub Ingest service.
//
// Parameters:
//   - client: HTTP client with timeout configured
//   - platform: one of "steam", "gog", "psn", "epic"
//   - payload: slice of structs to marshal and send as JSON array
//
// The function handles JSON serialization, HTTP POST, and response validation.
// If an API key is configured (via --api-key flag or config file), it's sent
// as the X-Api-Key header for authentication.
// On success it prints a confirmation; on failure it prints the error and returns it.
func PostToBackend(client *http.Client, platform string, payload any) error {
	// Marshal the payload (games list) into JSON bytes
	payloadBytes, err := json.Marshal(payload)
	if err != nil {
		return fmt.Errorf("serialization error: %w", err)
	}

	// POST to the Ingest service endpoint for this platform
	endpoint := fmt.Sprintf("%s/api/ingest/%s", backendUrl, platform)
	req, err := http.NewRequest("POST", endpoint, bytes.NewBuffer(payloadBytes))
	if err != nil {
		return fmt.Errorf("failed to create request: %w", err)
	}
	req.Header.Set("Content-Type", "application/json")

	// Add API key header if configured
	apiKey := GetConfigString("api-key", apiKeyFlag)
	if apiKey != "" {
		req.Header.Set("X-Api-Key", apiKey)
	}

	// X-Api-Key is a custom header and Go would forward it on redirects. Clone the caller's
	// client so backend uploads reject redirects without changing provider-fetch behavior.
	backendClient := *client
	backendClient.CheckRedirect = func(*http.Request, []*http.Request) error {
		return http.ErrUseLastResponse
	}
	resp, err := backendClient.Do(req)
	if err != nil {
		return fmt.Errorf("failed to send to backend: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode >= 200 && resp.StatusCode < 300 {
		fmt.Printf("🎉 %s ingest complete!\n", platform)
		return nil
	}

	if resp.StatusCode == 401 {
		return fmt.Errorf("authentication failed: set api-key in ~/.gamehub.yaml or use --api-key flag")
	}

	return fmt.Errorf("backend rejected data: %s", resp.Status)
}

// NewHTTPClient creates an HTTP client with sensible defaults for API calls.
// Uses a 30-second timeout to handle slow APIs (especially PSN/GOG).
func NewHTTPClient() *http.Client {
	return &http.Client{
		Timeout: 30 * time.Second,
	}
}
