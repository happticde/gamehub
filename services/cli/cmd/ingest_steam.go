package cmd

import (
	"bytes"
	"encoding/json"
	"fmt"
	"net/http"
	"time"

	"github.com/spf13/cobra"
)

// 1. Data Models (Structs)
// In Go, you define the "shape" of your JSON with structs.
// The `json:"name"` tags are like [JsonProperty("name")] in C#.
type SteamGame struct {
	AppId           int    `json:"appid"`
	Name            string `json:"name"`
	PlaytimeForever int    `json:"playtime_forever"`
}

type SteamResponse struct {
	Response struct {
		GameCount int         `json:"game_count"`
		Games     []SteamGame `json:"games"`
	} `json:"response"`
}

// 2. Variables for Flags
// These will hold the values passed via CLI (e.g. --key "123").
var (
	steamKey string
	steamId  string
)

// 3. The Command Definition
// This struct defines what happens when you type `gamehub ingest steam`
var ingestSteamCmd = &cobra.Command{
	Use:   "steam",
	Short: "Fetch library from Steam API and push to GameHub Core",
	Run: func(cmd *cobra.Command, args []string) {

		// A. Log start
		fmt.Printf("🚀 Starting Steam Ingest for ID: %s\n", steamId)

		// B. Build the URL
		url := fmt.Sprintf(
			"https://api.steampowered.com/IPlayerService/GetOwnedGames/v0001/?key=%s&steamid=%s&format=json&include_appinfo=1",
			steamKey, steamId,
		)

		// C. Create HTTP Client with Timeout (Best Practice)
		// Go's default http.Get has no timeout! Always define one for production.
		client := &http.Client{Timeout: 10 * time.Second}

		// D. Make the Request
		resp, err := client.Get(url)
		if err != nil {
			fmt.Printf("❌ Network Error: %v\n", err)
			return
		}
		// E. The 'defer' keyword
		// This ensures the connection closes when the function exits.
		// Think of this as C#'s `using` block, but function-scoped.
		defer resp.Body.Close()

		if resp.StatusCode != 200 {
			fmt.Printf("❌ Steam API returned status: %s\n", resp.Status)
			return
		}

		// F. Decode JSON
		// We stream the body directly into our struct (Memory Efficient).
		var result SteamResponse
		if err := json.NewDecoder(resp.Body).Decode(&result); err != nil {
			fmt.Printf("❌ JSON Parse Error: %v\n", err)
			return
		}

		fmt.Printf("✅ Found %d games on Steam. Sending to backend...\n", len(result.Response.Games))

		// G. Prepare Payload for Backend
		// Marshal our struct back to JSON bytes to send to your Core service.
		payloadBytes, err := json.Marshal(result.Response.Games)
		if err != nil {
			fmt.Printf("❌ Serialization Error: %v\n", err)
			return
		}

		// H. Post to Core Service
		ingestEndpoint := fmt.Sprintf("%s/api/ingest/steam", backendUrl)
		postResp, err := client.Post(ingestEndpoint, "application/json", bytes.NewBuffer(payloadBytes))
		if err != nil {
			fmt.Printf("❌ Failed to send to backend: %v\n", err)
			return
		}
		defer postResp.Body.Close()

		if postResp.StatusCode >= 200 && postResp.StatusCode < 300 {
			fmt.Println("🎉 Ingest Complete!")
		} else {
			fmt.Printf("⚠️ Backend rejected data: %s\n", postResp.Status)
		}
	},
}

// 4. Initialization (Constructor)
// This runs once on startup to register the command and flags.
func init() {
	// Add 'steam' as a subcommand to 'ingest'
	ingestCmd.AddCommand(ingestSteamCmd)

	// Define flags
	// &steamKey -> Pointer to the variable where the value will be stored
	// "key" -> The flag name (--key)
	// "" -> Default value
	// "Help text" -> Description
	ingestSteamCmd.Flags().StringVarP(&steamKey, "key", "k", "", "Steam Web API Key")
	ingestSteamCmd.Flags().StringVarP(&steamId, "id", "i", "", "Steam ID 64")

	// Mark flags as required
	ingestSteamCmd.MarkFlagRequired("key")
	ingestSteamCmd.MarkFlagRequired("id")
}
