// Steam ingest command — fetches the user's Steam library via the
// Steam Web API and pushes it to the GameHub Ingest service.
//
// Usage:
//
//	gamehub-cli ingest steam --key YOUR_KEY --id YOUR_STEAM_ID_64
//
// Or with config file (~/.gamehub.yaml):
//
//	steam:
//	  key: "YOUR_STEAM_API_KEY"
//	  id: "YOUR_STEAM_ID_64"
//
// Then just: gamehub-cli ingest steam
//
// The Steam Web API returns JSON like:
//
//	{
//	  "response": {
//	    "game_count": 42,
//	    "games": [
//	      { "appid": 730, "name": "Counter-Strike 2", "playtime_forever": 1234 },
//	      ...
//	    ]
//	  }
//	}
package cmd

import (
	"encoding/json"
	"fmt"

	"github.com/spf13/cobra"
)

// SteamGame represents a single game entry from the Steam API response.
type SteamGame struct {
	AppId           int    `json:"appid"`
	Name            string `json:"name"`
	PlaytimeForever int    `json:"playtime_forever"`
}

// SteamResponse is the top-level wrapper for the Steam GetOwnedGames API.
type SteamResponse struct {
	Response struct {
		GameCount int         `json:"game_count"`
		Games     []SteamGame `json:"games"`
	} `json:"response"`
}

// Flag variables — set by CLI flags or config file
var (
	steamKey string
	steamId  string
)

var ingestSteamCmd = &cobra.Command{
	Use:   "steam",
	Short: "Fetch library from Steam API and push to GameHub Ingest",
	RunE: func(cmd *cobra.Command, args []string) error {
		// Resolve credentials: flags override config file
		key := GetConfigString("steam.key", steamKey)
		id := GetConfigString("steam.id", steamId)

		if key == "" || id == "" {
			return fmt.Errorf("steam API key and Steam ID are required (use --key/--id flags or config file)")
		}

		fmt.Printf("🚀 Starting Steam ingest for Steam ID: %s\n", id)

		// Build the Steam Web API URL
		// include_appinfo=1 gives us game names, not just IDs
		url := fmt.Sprintf(
			"https://api.steampowered.com/IPlayerService/GetOwnedGames/v0001/?key=%s&steamid=%s&format=json&include_appinfo=1",
			key, id,
		)

		client := NewHTTPClient()

		// Fetch the library from Steam
		resp, err := client.Get(url)
		if err != nil {
			return fmt.Errorf("network error: %w", err)
		}
		defer resp.Body.Close()

		if resp.StatusCode != 200 {
			return fmt.Errorf("Steam API returned status: %s", resp.Status)
		}

		// Decode the JSON response directly from the body stream
		var result SteamResponse
		if err := json.NewDecoder(resp.Body).Decode(&result); err != nil {
			return fmt.Errorf("JSON parse error: %w", err)
		}

		fmt.Printf("✅ Found %d games on Steam. Sending to backend...\n", len(result.Response.Games))

		// Send to the Ingest service
		return PostToBackend(client, "steam", result.Response.Games)
	},
}

func init() {
	ingestCmd.AddCommand(ingestSteamCmd)

	// Flags are optional if config file is used
	ingestSteamCmd.Flags().StringVarP(&steamKey, "key", "k", "", "Steam Web API Key")
	ingestSteamCmd.Flags().StringVarP(&steamId, "id", "i", "", "Steam ID 64")
}
