package cmd

import (
	"os"

	"github.com/spf13/cobra"
)

var backendUrl string

var rootCmd = &cobra.Command{
	Use:   "gamehub-cli",
	Short: "CLI for GameHub Ingestion and Management",
}

func init() {
	// Global Flags (available for all subcommands)
	rootCmd.PersistentFlags().StringVar(&backendUrl, "backend-url", "http://localhost:5000", "Base URL for GameHub Core API")
}

func Execute() {
	if err := rootCmd.Execute(); err != nil {
		os.Exit(1)
	}
}
