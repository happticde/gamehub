// Parent command for all ingest subcommands.
// Each platform (steam, gog, psn) registers itself as a child of this command.
//
// Usage:
//
//	gamehub-cli ingest steam [flags]
//	gamehub-cli ingest gog [flags]
//	gamehub-cli ingest psn [flags]
package cmd

import "github.com/spf13/cobra"

var ingestCmd = &cobra.Command{
	Use:   "ingest",
	Short: "Ingest games from various platforms",
}

func init() {
	rootCmd.AddCommand(ingestCmd)
}
