// PSN ingest command — fetches the user's PlayStation library and pushes it
// to the GameHub Ingest service.
//
// This is a Go port of the user's working Python PSN_Client implementation.
// It uses PlayStation's internal GraphQL API with a persisted query hash
// and cookie-based authentication.
//
// Usage:
//
//	gamehub-cli ingest psn --cookies '{"JSESSIONID":"abc123","sb-cdn.session":"xyz"}'
//
// Or with config file (~/.gamehub.yaml):
//
//	psn:
//	  cookies: '{"JSESSIONID":"abc123","sb-cdn.session":"xyz"}'
//
// How to get the cookies:
//  1. Log into store.playstation.com in your browser
//  2. Open Developer Tools → Application → Cookies
//  3. Copy the relevant cookies as a JSON object
//
// The PSN GraphQL API returns JSON like:
//
//	{
//	  "data": {
//	    "purchasedTitlesRetrieve": {
//	      "games": [
//	        { "titleId": "CUSA00001_00", "name": "Game Title", "platform": "PS4", ... },
//	        ...
//	      ],
//	      "pageInfo": {
//	        "isLast": false,
//	        "totalCount": 42
//	      }
//	    }
//	  }
//	}
package cmd

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/cookiejar"
	"net/url"
	"time"

	"github.com/spf13/cobra"
)

// --- PSN GraphQL Response Structs ---

// PsnGraphQLResponse is the top-level GraphQL response wrapper.
type PsnGraphQLResponse struct {
	Data struct {
		PurchasedTitlesRetrieve struct {
			Games    []PsnGame   `json:"games"`
			PageInfo PsnPageInfo `json:"pageInfo"`
		} `json:"purchasedTitlesRetrieve"`
	} `json:"data"`
}

// PsnGame represents a single game from the PSN library.
type PsnGame struct {
	TitleId  string `json:"titleId"`
	Name     string `json:"name"`
	Platform string `json:"platform"`
	ImageUrl string `json:"imageUrl"`
}

// PsnPageInfo contains pagination metadata from the GraphQL response.
type PsnPageInfo struct {
	IsLast     bool `json:"isLast"`
	TotalCount int  `json:"totalCount"`
}

// Flag variable
var psnCookies string

// The PSN GraphQL endpoint with the persisted query hash.
// This matches the user's Python implementation exactly.
const psnGraphQLBaseURL = "https://web.np.playstation.com/api/graphql/v1/op" +
	"?operationName=getPurchasedGameList" +
	"&variables={\"isActive\":true,\"platform\":[\"ps3\",\"ps4\",\"ps5\"],\"start\":%d,\"size\":%d,\"subscriptionService\":\"NONE\"}" +
	"&extensions={\"persistedQuery\":{\"version\":1,\"sha256Hash\":\"2c045408b0a4d0264bb5a3edfed4efd49fb4749cf8d216be9043768adff905e2\"}}"

var ingestPsnCmd = &cobra.Command{
	Use:   "psn",
	Short: "Fetch library from PlayStation Network and push to GameHub Ingest",
	RunE: func(cmd *cobra.Command, args []string) error {
		// Resolve credentials: flags override config
		cookies := GetConfigString("psn.cookies", psnCookies)

		if cookies == "" {
			return fmt.Errorf("PSN cookies are required (use --cookies flag or config file)")
		}

		fmt.Println("🚀 Starting PSN ingest...")

		// Parse the cookies JSON string into a map
		var cookieMap map[string]string
		if err := json.Unmarshal([]byte(cookies), &cookieMap); err != nil {
			return fmt.Errorf("invalid cookies JSON: %w (expected format: '{\"name\":\"value\"}')", err)
		}

		// Create an HTTP client with a cookie jar (like Python's RequestsCookieJar)
		jar, err := cookiejar.New(nil)
		if err != nil {
			return fmt.Errorf("failed to create cookie jar: %w", err)
		}

		// Set all cookies on the playstation.com domain
		psnURL, _ := url.Parse("https://web.np.playstation.com")
		var httpCookies []*http.Cookie
		for name, value := range cookieMap {
			httpCookies = append(httpCookies, &http.Cookie{
				Name:   name,
				Value:  value,
				Domain: "playstation.com",
				Path:   "/",
			})
		}
		jar.SetCookies(psnURL, httpCookies)

		client := &http.Client{
			Timeout: 30 * time.Second,
			Jar:     jar,
		}

		// Fetch all games across all pages
		allGames, err := psnFetchAllGames(client)
		if err != nil {
			return err
		}

		fmt.Printf("✅ Found %d games on PSN. Sending to backend...\n", len(allGames))

		// Send to the Ingest service
		return PostToBackend(client, "psn", allGames)
	},
}

// psnFetchAllGames fetches all pages of the user's PSN library.
// Uses the same pagination logic as the Python implementation:
// start at offset 0, fetch 100 per page, loop until pageInfo.isLast is true.
func psnFetchAllGames(client *http.Client) ([]PsnGame, error) {
	var allGames []PsnGame
	pageOffset := 0
	pageSize := 100

	for {
		// Build the GraphQL URL with pagination parameters
		graphqlURL := fmt.Sprintf(psnGraphQLBaseURL, pageOffset, pageSize)

		// Create request with Content-Type header (required by PSN)
		req, err := http.NewRequest("GET", graphqlURL, nil)
		if err != nil {
			return nil, fmt.Errorf("failed to create request: %w", err)
		}
		req.Header.Set("Content-Type", "application/json")

		resp, err := client.Do(req)
		if err != nil {
			return nil, fmt.Errorf("network error at offset %d: %w", pageOffset, err)
		}
		defer resp.Body.Close()

		if resp.StatusCode != 200 {
			return nil, fmt.Errorf("PSN API returned status %s (are your cookies valid?)", resp.Status)
		}

		var result PsnGraphQLResponse
		if err := json.NewDecoder(resp.Body).Decode(&result); err != nil {
			return nil, fmt.Errorf("JSON parse error at offset %d: %w", pageOffset, err)
		}

		games := result.Data.PurchasedTitlesRetrieve.Games
		allGames = append(allGames, games...)

		totalCount := result.Data.PurchasedTitlesRetrieve.PageInfo.TotalCount
		fmt.Printf("   📄 Fetched %d/%d games\n", len(allGames), totalCount)

		// Check if this is the last page (matches Python's isLast check)
		if result.Data.PurchasedTitlesRetrieve.PageInfo.IsLast {
			break
		}

		pageOffset += pageSize
	}

	return allGames, nil
}

func init() {
	ingestCmd.AddCommand(ingestPsnCmd)

	ingestPsnCmd.Flags().StringVarP(&psnCookies, "cookies", "c", "", "PSN cookies as JSON string")
}
