// Root command for the gamehub-cli.
// This is the entry point that Cobra uses to build the command tree.
// All subcommands (ingest, etc.) register themselves via init() functions
// in their respective files.
package cmd

import (
	"os"

	"github.com/spf13/cobra"
)

// backendUrl holds the base URL for the GameHub Ingest service.
// Set via --backend-url flag or config file.
var backendUrl string

var rootCmd = &cobra.Command{
	Use:   "gamehub-cli",
	Short: "CLI for GameHub Ingestion and Management",
}

func init() {
	// Load config file before any command runs
	cobra.OnInitialize(initConfig)

	// Global flag — available to all subcommands
	rootCmd.PersistentFlags().StringVar(&backendUrl, "backend-url", "http://localhost:5000", "Base URL for GameHub Ingest API")
}

func Execute() {
	if err := rootCmd.Execute(); err != nil {
		os.Exit(1)
	}
}
