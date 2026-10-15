package cmd

import (
	"context"
	"errors"
	"net"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func TestHelperLaunch_UnapprovedOriginCannotStartAnyProvider(t *testing.T) {
	previousConfirm, previousTimeout, previousTTL := confirmHelperOrigin, helperLaunchTimeout, helperLaunchTTL
	t.Cleanup(func() {
		confirmHelperOrigin, helperLaunchTimeout, helperLaunchTTL = previousConfirm, previousTimeout, previousTTL
	})
	helperLaunchTimeout, helperLaunchTTL = time.Minute, time.Minute
	for _, provider := range []string{"gog", "psn", "epic"} {
		t.Run(provider, func(t *testing.T) {
			called := false
			confirmHelperOrigin = func(_ context.Context, origin, selectedProvider string) error {
				called = true
				if origin != "https://attacker.invalid" || selectedProvider != provider {
					t.Fatal("approval must show the exact destination")
				}
				return errors.New("cancelled")
			}
			err := runHelperLaunch(helperLaunchCmd, []string{"gamehub-helper://connect/" + provider + "?origin=https%3A%2F%2Fattacker.invalid&launchId=audit-1234567890123&statusPort=55001"})
			if err == nil || !called {
				t.Fatal("unapproved launch must fail before provider capture")
			}
			listener, err := net.Listen("tcp4", "127.0.0.1:55001")
			if err != nil {
				t.Fatalf("cancelled launch exposed its status port: %v", err)
			}
			listener.Close()
		})
	}
}

func TestHelperLaunch_MissingOriginCannotBypassApproval(t *testing.T) {
	previousTimeout, previousTTL := helperLaunchTimeout, helperLaunchTTL
	t.Cleanup(func() { helperLaunchTimeout, helperLaunchTTL = previousTimeout, previousTTL })
	helperLaunchTimeout, helperLaunchTTL = time.Minute, time.Minute
	if err := runHelperLaunch(helperLaunchCmd, []string{"gamehub-helper://connect/gog"}); err == nil {
		t.Fatal("missing origin must fail")
	}
}

func TestLoopbackHandler_RejectsDnsRebindingHost(t *testing.T) {
	handler := loopbackHostOnly(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(http.StatusNoContent) }))
	for _, host := range []string{"attacker.invalid:55001", "127.0.0.1.attacker.invalid:55001", "127.0.0.1:55001", "localhost:55001"} {
		request := httptest.NewRequest(http.MethodGet, "http://"+host+"/", nil)
		response := httptest.NewRecorder()
		handler.ServeHTTP(response, request)
		want := http.StatusForbidden
		if host == "127.0.0.1:55001" || host == "localhost:55001" {
			want = http.StatusNoContent
		}
		if response.Code != want {
			t.Fatalf("host %s: got %d want %d", host, response.Code, want)
		}
	}
}
