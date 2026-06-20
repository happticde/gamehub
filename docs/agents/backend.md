# Backend Agent Context

## Architecture Details
Two ASP.NET Core microservices:
- **Core (`services/core/`)**: Unified library, slug matching, enrichment (IGDB), sync orchestration.
- **Ingest (`services/ingest/`)**: Raw data fetching from Steam, GOG, PSN, IGDB.
- Ingest raw/audit rows are tenant-owned and filtered by the trusted authentication claim.
  Existing rows belong to the default tenant; arbitrary tenant IDs are never accepted in bodies.

## Design Patterns
- **IngestControllerBase<T>** — generic base with shared upsert/pagination; platform controllers only implement JSON key extraction.
- **Slug-based matching** — `UnificationService.GenerateSlug()` normalizes titles for cross-platform deduplication.
- **ManualLinks** — user overrides for merge/split that persist across syncs.
- **AuthenticationHandler<T>** — API key auth using ASP.NET's pipeline, not raw middleware.

## Configuration
- Auth: `Authentication:ApiKey` in appsettings.json or `Authentication__ApiKey` env var. Empty = legacy auth-disabled mode. `Authentication:TenantId` maps the API key to a trusted tenant claim. Setting `Authentication:Jwt:SigningKey` (minimum 32 UTF-8 bytes) enables JWT validation and token issuance. `Authentication:Jwt:RefreshTokenLifetimeDays` controls rotating opaque refresh sessions (default 30, range 1–90).
- Core→Ingest delegation: Core reads `Services:IngestServiceKey`; Ingest validates the matching `Authentication:ServiceKey`. Only this internal key may authenticate `X-GameHub-Tenant-Id`. Normal API keys cannot select a tenant through request headers.
- Platform credential vault: Ingest reads versioned Base64-encoded 32-byte AES keys from `Credentials:Keys:{version}` and selects the write key with `Credentials:ActiveKeyVersion`. Empty keys disable writes; invalid key-ring configuration fails startup. Vault endpoints require the internal tenant-delegation scheme and never return plaintext. Steam/GOG/PSN use strict schema-v1 payloads; Core triggers the service-key-only stored-retrieval route before exporting raw data. Epic stored retrieval is not supported yet.
- DB: `Database:Provider` (`Sqlite`/`Postgres`), connection strings in `ConnectionStrings` section.
- Core→Ingest URL: `Services:IngestUrl` (default `http://localhost:5000`).
- Provisioned tenants remain platform-sync disabled until Core/frontend credential management and
  the explicit activation policy are complete.
- Sync Scheduler: `Sync:EnableScheduler` (bool, default `false`), `Sync:IntervalMinutes` (int, default `360`).

## Conventions
- All controllers have `ILogger` injection with structured logging (`{Platform}`, `{Count}`, etc.).
- XML doc comments (`/// <summary>`) on all public members.
- Comments explain *why*, not *what*.
- Tests follow pattern: `MethodName_Scenario_ExpectedResult`.
