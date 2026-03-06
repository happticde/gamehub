// This file sets up Viper-based configuration so credentials
// can be stored in ~/.gamehub.yaml instead of passing flags every time.
//
// Config file format (~/.gamehub.yaml):
//
//	backend-url: http://localhost:5000
//	api-key: "YOUR_API_KEY"
//	steam:
//	  key: "YOUR_STEAM_API_KEY"
//	  id: "YOUR_STEAM_ID_64"
//	gog:
//	  token: "YOUR_GOG_REFRESH_TOKEN"
//	psn:
//	  cookies: '{"cookie_name": "cookie_value", ...}'
//
// Flags always override config file values.
package cmd

import (
	"fmt"
	"os"
	"path/filepath"

	"github.com/spf13/viper"
)

// initConfig loads the configuration file from ~/.gamehub.yaml.
// Called once during CLI startup in root.go's init().
func initConfig() {
	// Look for config in the user's home directory
	home, err := os.UserHomeDir()
	if err != nil {
		// Not fatal — user can still pass flags
		return
	}

	configPath := filepath.Join(home, ".gamehub.yaml")

	// Only load if the file exists — don't error if it's missing
	if _, err := os.Stat(configPath); os.IsNotExist(err) {
		return
	}

	viper.SetConfigFile(configPath)
	if err := viper.ReadInConfig(); err != nil {
		fmt.Fprintf(os.Stderr, "⚠️  Warning: could not read config file: %v\n", err)
	}
}

// GetConfigString returns a config value, checking flags first, then config file.
// This allows flags to override config file values.
//
// Usage:
//
//	key := GetConfigString("steam.key", steamKey)
//
// If steamKey (the flag value) is non-empty, it's returned.
// Otherwise, the value from the config file at the given key path is returned.
func GetConfigString(viperKey string, flagValue string) string {
	if flagValue != "" {
		return flagValue
	}
	return viper.GetString(viperKey)
}
