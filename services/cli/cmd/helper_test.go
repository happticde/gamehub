package cmd

import (
	"strings"
	"testing"
)

func TestParseHelperLaunchURL_GogWithOrigin_NormalizesOrigin(t *testing.T) {
	request, err := parseHelperLaunchURL("gamehub-helper://connect/gog?origin=http%3A%2F%2Flocalhost%3A3005")
	if err != nil {
		t.Fatalf("parseHelperLaunchURL returned error: %v", err)
	}

	if request.Platform != "gog" {
		t.Fatalf("Platform = %q, want gog", request.Platform)
	}
	if request.Origin != "http://localhost:3005" {
		t.Fatalf("Origin = %q, want http://localhost:3005", request.Origin)
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

func TestParseHelperLaunchURL_RejectsSecretParameters(t *testing.T) {
	secretParameters := []string{"cookie", "gog-al", "gogAl", "gogAlCookie", "password", "token"}
	for _, parameter := range secretParameters {
		t.Run(parameter, func(t *testing.T) {
			_, err := parseHelperLaunchURL("gamehub-helper://connect/gog?" + parameter + "=secret")
			if err == nil {
				t.Fatalf("expected %q parameter to be rejected", parameter)
			}
			if !strings.Contains(err.Error(), "must not contain secret parameter") {
				t.Fatalf("error = %q, want secret-parameter rejection", err)
			}
		})
	}
}

func TestParseHelperLaunchURL_RejectsUnsupportedTarget(t *testing.T) {
	_, err := parseHelperLaunchURL("gamehub-helper://connect/psn?origin=http%3A%2F%2Flocalhost%3A3005")
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

func TestMacOSHelperAppleScript_UsesLaunchCommandWithoutEmbeddingURL(t *testing.T) {
	script := macOSHelperAppleScript("/Applications/GameHub/gamehub-cli")

	if !strings.Contains(script, "helper launch") {
		t.Fatalf("AppleScript does not launch the helper command")
	}
	if strings.Contains(script, "gog-al") || strings.Contains(script, "cookie") {
		t.Fatalf("AppleScript should not embed cookie-related data")
	}
}
