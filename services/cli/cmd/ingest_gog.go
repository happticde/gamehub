package cmd

import (
	"fmt"
	"net/http"
	"net/http/cookiejar"

	"github.com/spf13/cobra"
)

type GogResponse struct {
    Page     int      `json:"page"`
    Limit    int      `json:"limit"`
    Pages    int      `json:"pages"`
    Total    int      `json:"total"`
    Links    GogLinks    `json:"_links"`
    Embedded Embedded `json:"_embedded"`
}

type GogLinks struct {
    Self  GogLink `json:"self"`
    First GogLink `json:"first"`
    Last  GogLink `json:"last"`
    Next  GogLink `json:"next"`
}

type GogLink struct {
    Href string `json:"href"`
}

type Embedded struct {
    Items []GogItem `json:"items"`
}

type GogItem struct {
    Game  GogGame                          `json:"game"`
    Stats map[string]PlaySessionSummary `json:"stats"`
}

type GogGame struct {
	Id    int    `json:"id"`
	Title string `json:"title"`
	Url   string `json:"url"`
	Image string `json:"image"`
}

type PlaySessionSummary struct {
    Playtime    int    `json:"playtime"`
    LastSession string `json:"lastSession"`
}

var (
	gogToken string
	gogUser  string
)

var ingestGogCmd = &cobra.Command{
	Use:   "gog",
	Short: "Fetch library from GOG API and push to GameHub Core",
	Run: func(cmd *cobra.Command, args []string) {
		gogCookieJar, err = cookiejar.New(nil)

		client := &http.Client {
			Jar: gogCookieJar,
		}

		http.Cookie{
			Name: "gog-al",
			Value: gogToken,
		}

		client.s

		fmt.Printf("🚀 Starting GOG Ingest for user: %s\n", gogUser)

		url := "https://www.gog.com/u/%s/games/stats?page=%s"

		for {
			page := 1
			resp, err := http.Get(fmt.Sprintf(url, gogUser, page))

			if err != nil {
				fmt.Printf("❌ Network Error: %v\n", err)
				return
			}

			defer resp.Body.Close()

			if resp.StatusCode != http.StatusOK {
				fmt.Printf("❌ API Error: Status Code %d\n", resp.StatusCode)
				return
			}

			var result 
		}
	},
}

func init() {
	ingestGogCmd.Flags().StringVarP(&gogToken, "token", "t", "", "GOG API Token")
	ingestGogCmd.Flags().StringVarP(&gogUser, "user", "u", "", "GOG Username")

	ingestGogCmd.MarkFlagRequired("token")
	ingestGogCmd.MarkFlagRequired("user")

	ingestCmd.AddCommand(ingestGogCmd)
}
