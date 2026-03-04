// GOG ingest command — fetches the user's GOG library and pushes it
// to the GameHub Ingest service.
//
// This is a Go port of the user's working Python GogClient implementation.
// It uses cookie-based auth with the gog-al cookie and the public
// GOG user stats endpoint.
//
// Usage:
//
//	gamehub-cli ingest gog --user YOUR_GOG_USERNAME --token YOUR_GOG_AL_COOKIE
//
// Or with config file (~/.gamehub.yaml):
//
//	gog:
//	  user: "your_gog_username"
//	  token: "your_gog_al_cookie_value"
//
// How to get the gog-al cookie:
//  1. Log into GOG.com in your browser
//  2. Open Developer Tools → Application → Cookies → gog.com
//  3. Copy the value of the "gog-al" cookie
//
// The GOG API returns paginated JSON like:
//
//	{
//	  "page": 1,
//	  "pages": 3,
//	  "_embedded": {
//	    "items": [
//	      { "game": { "id": 123, "title": "Game Name", "url": "...", "image": "..." }, ... },
//	      ...
//	    ]
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

// --- GOG API Response Structs ---
// These match the JSON shape from gog.com/u/{user}/games/stats

// GogStatsResponse is the paginated response from the GOG stats endpoint.
type GogStatsResponse struct {
	Page     int         `json:"page"`
	Pages    int         `json:"pages"`
	Embedded GogEmbedded `json:"_embedded"`
}

// GogEmbedded contains the list of game items.
type GogEmbedded struct {
	Items []GogItem `json:"items"`
}

// GogItem wraps a game entry with optional play stats.
type GogItem struct {
	Game GogGame `json:"game"`
}

// GogGame represents a single game in the GOG library.
type GogGame struct {
	Id    int    `json:"id"`
	Title string `json:"title"`
	Url   string `json:"url"`
	Image string `json:"image"`
}

// Flag variables
var (
	gogToken string
	gogUser  string
)

var ingestGogCmd = &cobra.Command{
	Use:   "gog",
	Short: "Fetch library from GOG and push to GameHub Ingest",
	RunE: func(cmd *cobra.Command, args []string) error {
		// Resolve credentials: flags override config
		token := GetConfigString("gog.token", gogToken)
		user := GetConfigString("gog.user", gogUser)

		if token == "" || user == "" {
			return fmt.Errorf("GOG token and username are required (use --token/--user flags or config file)")
		}

		fmt.Printf("🚀 Starting GOG ingest for user: %s\n", user)

		// Create an HTTP client with a cookie jar, just like Python's requests.Session()
		jar, err := cookiejar.New(nil)
		if err != nil {
			return fmt.Errorf("failed to create cookie jar: %w", err)
		}

		// Set the gog-al cookie on the gog.com domain
		// This is the authentication cookie — equivalent to:
		//   session.cookies.set("gog-al", cookie_value)
		gogURL, _ := url.Parse("https://www.gog.com")
		jar.SetCookies(gogURL, []*http.Cookie{
			{
				Name:   "gog-al",
				Value:  token,
				Domain: ".gog.com",
				Path:   "/",
			},
		})

		client := &http.Client{
			Timeout: 30 * time.Second,
			Jar:     jar,
		}

		// Fetch all games across all pages
		allGames, err := gogFetchAllGames(client, user)
		if err != nil {
			return err
		}

		fmt.Printf("✅ Found %d games on GOG. Sending to backend...\n", len(allGames))

		// Send to the Ingest service
		return PostToBackend(client, "gog", allGames)
	},
}

// gogFetchAllGames fetches all pages of the user's GOG library.
// Matches the Python implementation's pagination logic:
// loop pages starting at 1, stop when page == pages.
func gogFetchAllGames(client *http.Client, user string) ([]GogGame, error) {
	var allGames []GogGame
	page := 1

	for {
		// Build the URL — matches Python's:
		//   url = "https://www.gog.com/u/{user_id}/games/stats?page={page}"
		apiURL := fmt.Sprintf("https://www.gog.com/u/%s/games/stats?page=%d", user, page)

		resp, err := client.Get(apiURL)
		if err != nil {
			return nil, fmt.Errorf("network error on page %d: %w", page, err)
		}
		defer resp.Body.Close()

		if resp.StatusCode != 200 {
			return nil, fmt.Errorf("GOG API returned status %s on page %d (is your gog-al cookie valid?)", resp.Status, page)
		}

		var result GogStatsResponse
		if err := json.NewDecoder(resp.Body).Decode(&result); err != nil {
			return nil, fmt.Errorf("JSON parse error on page %d: %w", page, err)
		}

		// Extract game data from the embedded items
		// Matches Python's: games += json_response["_embedded"]["items"]
		for _, item := range result.Embedded.Items {
			allGames = append(allGames, item.Game)
		}

		fmt.Printf("   📄 Fetched page %d/%d (%d games)\n", page, result.Pages, len(result.Embedded.Items))

		// Stop when we've reached the last page
		// Matches Python's: if json_response["pages"] == page: break
		if page >= result.Pages {
			break
		}

		page++
	}

	return allGames, nil
}

func init() {
	ingestCmd.AddCommand(ingestGogCmd)

	ingestGogCmd.Flags().StringVarP(&gogToken, "token", "t", "", "GOG gog-al cookie value")
	ingestGogCmd.Flags().StringVarP(&gogUser, "user", "u", "", "GOG username")
}
