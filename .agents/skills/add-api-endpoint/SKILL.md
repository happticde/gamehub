---
name: Add API Endpoint
description: How to add a new endpoint to the Core or Ingest service following project conventions
---

# Add API Endpoint

## 1. Choose the Right Controller

| Service | Controller | When to use |
|---------|-----------|-------------|
| Core (:5001) | `LibraryController.cs` | Unified library, enrichment, sync, stats |
| Ingest (:5000) | `RetrieveController.cs` | Platform fetching / ingestion |
| Ingest (:5000) | `ExportController.cs` | Data export for Core sync |
| Ingest (:5000) | `*IngestController.cs` | Raw data CRUD per platform |

## 2. Endpoint Structure

Every endpoint must follow this pattern:

```csharp
/// <summary>
/// HTTP_METHOD: api/library/endpoint-name
/// One-line description of what this endpoint does.
/// </summary>
[HttpGet("endpoint-name")]  // or HttpPost, HttpPut, HttpDelete
public async Task<IActionResult> MethodName(
    [FromQuery] string? optionalParam = null)
{
    _logger.LogInformation("Descriptive message {@Param}", optionalParam);

    // Business logic using _db (CoreContext/IngestContext)

    return Ok(new { Property = value });
}
```

### Conventions Checklist

- [ ] **XML doc comment** (`/// <summary>`) with HTTP method and route
- [ ] **ILogger** — use structured logging with `{NamedParams}`, not string interpolation
- [ ] **Async** — all database calls must be `async`/`await`
- [ ] **Return types** — use anonymous objects for simple responses, DTOs for complex ones
- [ ] **Query params** — use `[FromQuery]` with nullable defaults
- [ ] **Body params** — use a record or DTO class with `[FromBody]`

## 3. Add TypeScript Type (Frontend)

If the endpoint will be consumed by the frontend, add a matching interface in `frontend/types/game.ts`:

```typescript
/** Brief description — from GET /api/library/endpoint-name */
export interface EndpointResponse {
    propertyName: string;
    count: number;
}
```

**Naming**: Use PascalCase interface names. Use camelCase property names (C# auto-converts PascalCase to camelCase in JSON).

## 4. Frontend API Proxy

The Next.js app proxies API requests via `next.config.ts` rewrites:
- `/api/core/*` → `http://host.docker.internal:5001/api/*`
- `/api/ingest/*` → `http://host.docker.internal:5000/api/*`

Client-side fetch calls should use the proxy paths:
```typescript
const res = await fetch("/api/core/library/endpoint-name");
```

## 5. Test Pattern

Add a test in `tests/GameHub.Tests/` following the naming convention:

```
MethodName_Scenario_ExpectedResult
```

Example: `GetStats_WithEnrichedGames_ReturnsCorrectCounts`

## 6. Verify

```bash
# Build backend
docker exec -w /workspaces/gamehub gamehub_devcontainer-app-1 \
  dotnet build services/core/GameHub.Core.csproj --verbosity quiet

# Run tests
docker exec -w /workspaces/gamehub gamehub_devcontainer-app-1 \
  dotnet test --verbosity quiet

# Build frontend (if types were added)
docker exec -w /workspaces/gamehub/frontend gamehub_devcontainer-app-1 \
  npm run build
```
