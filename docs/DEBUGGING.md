# Debugging Guide

Practical guide for diagnosing issues with the GameHub Backend services.

---

## Viewing Logs

### Docker Compose (production)
```bash
# Follow logs from both services
docker compose logs -f

# Follow logs from one service
docker compose logs -f ingest
docker compose logs -f core
```

### Local development (devcontainer)
```bash
# Run with verbose logging
dotnet run --project services/ingest/GameHub.Ingest.csproj

# Logs print directly to the terminal
```

---

## Log Levels

Configured in `appsettings.json` / `appsettings.Development.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "GameHub": "Debug"
    }
  }
}
```

| Level | When to use |
|-------|-------------|
| `Debug` | Pagination progress, HTTP request URLs, raw JSON snippets |
| `Information` | Fetch start/complete with item counts, sync events |
| `Warning` | Missing data (no games array, missing JSON properties) |
| `Error` | API failures, invalid responses, exceptions |

To see pagination details and request URLs, set `GameHub` to `Debug`.

---

## Common Errors by Platform

### Steam
| Error | Cause | Fix |
|-------|-------|-----|
| `403 Forbidden` | Invalid API key | Regenerate at [steamcommunity.com/dev/apikey](https://steamcommunity.com/dev/apikey) |
| `401 Unauthorized` | API key doesn't match Steam account | Verify the key belongs to you |
| Empty games array | Profile is private | Set game details to "Public" in Steam privacy settings |
| `429 Too Many Requests` | Rate limit | Wait a few minutes and retry |

### GOG
| Error | Cause | Fix |
|-------|-------|-----|
| `401` / `403` | `gog-al` cookie expired | Re-extract the cookie from your browser |
| JSON parse error | GOG returned HTML (login page) | Cookie invalid — you're getting redirected to login |
| Missing `_embedded.items` | Empty library or wrong username | Verify the username in the URL |

**Preferred GOG capture path:**
1. From a host terminal, run `cd services/cli && go run . connect gog`
2. Sign in with GOG in the visible browser window
3. Paste the helper JSON into the Admin credential UI

**Manual fallback for the `gog-al` cookie:**
1. Log in to [gog.com](https://www.gog.com)
2. Open DevTools → Application → Cookies → `gog.com`
3. Copy the `gog-al` value

### PSN
| Error | Cause | Fix |
|-------|-------|-----|
| `401` / `403` | Cookies expired | Re-extract from [store.playstation.com](https://store.playstation.com) |
| Unexpected response structure | API version changed | Check if the GraphQL hash needs updating |
| Timeout | PSN API is slow | Retry, or increase the timeout in `PsnFetcher` |

**Preferred PlayStation capture path:**
1. Install the local helper protocol handler once:
   ```bash
   cd services/cli && go run . helper install-protocol-handler
   ```
2. From the Admin credential UI, use **Connect PlayStation**.
3. The helper opens or attaches to your normal Chrome profile with local debugging enabled. Sign in
   with Sony/PlayStation in that Chrome window if needed, including MFA or captcha.

If the protocol handler is not installed, or if Chrome was already running without the debug port,
use the manual fallback:
1. Start your regular Chrome profile with local debugging enabled:
   ```bash
   /Applications/Google\ Chrome.app/Contents/MacOS/Google\ Chrome --remote-debugging-address=127.0.0.1 --remote-debugging-port=49333 https://store.playstation.com/
   ```
2. Run the helper against that Chrome endpoint:
   ```bash
   cd services/cli && go run . connect psn --remote-debugging-url http://127.0.0.1:49333 --handoff-server --origin http://localhost:3005
   ```
3. Paste the loopback handoff URL into the Admin credential UI.

If Chrome was already open and `http://127.0.0.1:49333/json/version` does not answer, quit all
Chrome windows and start it again with the command above. GameHub never asks for the PSN password;
the helper reads local session cookies only for a one-time handoff into the encrypted vault. The
validated cookie for the current PlayStation flow is `pdccws_p`.

**Diagnostic-only helper-launched browser:**
```bash
cd services/cli && go run . connect psn --handoff-server --origin http://localhost:3005 --diagnostics-file /tmp/gamehub-psn-login.jsonl
```
This fresh-profile path is known to be provider-fragile: Sony can allow OAuth, passkey, telemetry,
and fingerprinting requests but still return `429 Too Many Requests` for
`ca.account.sony.com/api/v1/ssocookie`, preventing the final `pdccws_p` cookie from being minted.

**Manual cookie fallback:**
1. Log in to [store.playstation.com](https://store.playstation.com)
2. Open DevTools → Network → find any API request
3. Copy the required PlayStation cookies from the request headers

---

## Testing Endpoints with curl

### Ingest — batch upsert
```bash
curl -X POST http://localhost:5000/api/ingest/steam \
  -H "Content-Type: application/json" \
  -d '[{"appid": 730, "name": "Counter-Strike 2"}]'
```

### Retrieve — fetch + ingest
```bash
# Steam
curl -X POST http://localhost:5000/api/retrieve/steam \
  -H "Content-Type: application/json" \
  -d '{"apiKey": "YOUR_KEY", "steamId": "YOUR_ID"}'

# GOG
curl -X POST http://localhost:5000/api/retrieve/gog \
  -H "Content-Type: application/json" \
  -d '{"cookie": "YOUR_GOG_AL", "user": "YOUR_USERNAME"}'
```

### Core — sync
```bash
curl -X POST http://localhost:5001/api/library/sync
```

### Health check
```bash
curl http://localhost:5000/health
curl http://localhost:5001/health
```

---

## Developer Exception Page

In development mode, both services return full stack traces in error responses. This means a `500 Internal Server Error` from curl or Swagger will show:
- The exception type and message
- The full stack trace
- The request path and query

This is **automatically disabled in production** for security.

To force development mode in Docker:
```yaml
environment:
  - ASPNETCORE_ENVIRONMENT=Development
```

---

## Database Debugging

### Check migration status
```bash
dotnet ef migrations list --project services/ingest/GameHub.Ingest.csproj
dotnet ef migrations list --project services/core/GameHub.Core.csproj
```

### Inspect SQLite databases
```bash
# With the sqlite3 CLI
sqlite3 data/gamehub.db ".tables"
sqlite3 data/gamehub.db "SELECT * FROM __EFMigrationsHistory"
sqlite3 data/gamehub.db "SELECT COUNT(*) FROM SteamRawItems"
```

### Reset the database
```bash
rm data/*.db
# Restart the service — Migrate() will recreate everything
```
