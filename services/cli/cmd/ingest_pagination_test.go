package cmd

import (
	"fmt"
	"io"
	"net/http"
	"strings"
	"testing"
)

type paginationTransport func(*http.Request) (*http.Response, error)

func (transport paginationTransport) RoundTrip(request *http.Request) (*http.Response, error) {
	return transport(request)
}

type paginationBody struct {
	io.Reader
	closed bool
}

func (body *paginationBody) Close() error {
	body.closed = true
	return nil
}

func TestFetchLibrariesClosesEachPageBeforeNextRequest(t *testing.T) {
	for _, provider := range []string{"gog", "psn"} {
		t.Run(provider, func(t *testing.T) {
			var bodies []*paginationBody
			client := &http.Client{Transport: paginationTransport(func(request *http.Request) (*http.Response, error) {
				if len(bodies) > 0 && !bodies[len(bodies)-1].closed {
					t.Error("previous response is still open when requesting the next page")
				}
				page := len(bodies) + 1
				if page > 2 {
					return nil, fmt.Errorf("unexpected extra page request")
				}
				payload := fmt.Sprintf(`{"page":%d,"pages":2,"_embedded":{"items":[{"game":{"id":%d,"title":"Game"}}]}}`, page, page)
				if provider == "psn" {
					payload = fmt.Sprintf(`{"data":{"purchasedTitlesRetrieve":{"games":[{"titleId":"%d"}],"pageInfo":{"isLast":%t,"totalCount":2}}}}`, page, page == 2)
				}
				body := &paginationBody{Reader: strings.NewReader(payload)}
				bodies = append(bodies, body)
				return &http.Response{StatusCode: http.StatusOK, Body: body, Header: make(http.Header)}, nil
			})}

			if provider == "gog" {
				games, err := gogFetchAllGames(client, "test-user")
				if err != nil || len(games) != 2 {
					t.Fatalf("got %d games, error %v", len(games), err)
				}
			} else {
				games, err := psnFetchAllGames(client)
				if err != nil || len(games) != 2 {
					t.Fatalf("got %d games, error %v", len(games), err)
				}
			}
			for _, body := range bodies {
				if !body.closed {
					t.Error("response body was not closed")
				}
			}
		})
	}
}

func TestFetchLibrariesClosesResponsesOnFailure(t *testing.T) {
	for _, provider := range []string{"gog", "psn"} {
		for _, status := range []int{http.StatusForbidden, http.StatusOK} {
			t.Run(fmt.Sprintf("%s-%d", provider, status), func(t *testing.T) {
				body := &paginationBody{Reader: strings.NewReader("invalid JSON")}
				client := &http.Client{Transport: paginationTransport(func(*http.Request) (*http.Response, error) {
					return &http.Response{StatusCode: status, Status: http.StatusText(status), Body: body, Header: make(http.Header)}, nil
				})}
				var err error
				if provider == "gog" {
					_, err = gogFetchAllGames(client, "test-user")
				} else {
					_, err = psnFetchAllGames(client)
				}
				if err == nil || !body.closed {
					t.Fatalf("expected error and closed body, got error %v, closed %t", err, body.closed)
				}
			})
		}
	}
}

func TestPsnFetchAllGamesRejectsEmptyNonFinalPage(t *testing.T) {
	requests := 0
	client := &http.Client{Transport: paginationTransport(func(*http.Request) (*http.Response, error) {
		requests++
		if requests > 1 {
			return nil, fmt.Errorf("unexpected retry after malformed page")
		}
		return &http.Response{StatusCode: http.StatusOK, Body: io.NopCloser(strings.NewReader("{}")), Header: make(http.Header)}, nil
	})}

	_, err := psnFetchAllGames(client)
	if err == nil || requests != 1 {
		t.Fatalf("expected immediate failure, got %d requests and error %v", requests, err)
	}
}
