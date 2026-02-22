package cmd

import "github.com/spf13/cobra"

var ingestCmd = &cobra.Command{
	Use:   "ingest",
	Short: "Ingest games from various platforms",
}

func init() {
	rootCmd.AddCommand(ingestCmd)
}
