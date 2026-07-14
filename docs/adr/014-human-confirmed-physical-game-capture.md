# ADR-014: Human-Confirmed Manual IGDB Capture for Physical Games

**Date:** 2026-07-14
**Status:** Accepted
**Deciders:** @jankahnt

## Context

Physical Collection Phase 1 introduced independently identified, tenant-owned PS3, PS4, and PS5
copies without extending digital `LibraryItem`, sync, or `ManualLink` semantics. Phase 2A added a
global capture funnel, optional local barcode decoding, canonical GTIN storage, tenant-local GTIN
lookup, explicit manual-game creation, and Backup V3. Phase 2B added a provider-neutral product
catalogue boundary and benchmark harness, but no production catalogue provider passed the usage,
coverage, and representative-data gate.

The assisted barcode flow made the primary task harder without supplying trustworthy release data.
A barcode decoder yields a trade-item identifier, not a game title, platform, edition, or region.
The disabled catalogue could not turn unknown codes into useful results, while scanner permission,
manual GTIN validation, local mappings, external hints, tenant search, manual creation, platform
selection, and confirmation all competed in one screen.

GameHub already has a proven provider-neutral IGDB title search for the Fix Match dialog. It returns
an explicit provider ID with title, cover, release date, summary, and platform metadata, and it can
verify a chosen result by ID on the server. Reusing that boundary provides a substantially simpler
manual capture path without claiming that IGDB models physical editions or regions.

## Decision Drivers

- The normal path must be understandable without camera access, product-catalogue availability, or
  prior digital ownership.
- A search result is a candidate until the user explicitly selects and confirms it.
- PS3, PS4, or PS5 must always be chosen deliberately; provider platform data is only a hint.
- Existing tenant-local games should be reused only through a strong identity, not title similarity.
- No search, provider result, or navigation action may write a game or copy.
- Multiple copies of the same game and physical platform remain valid.
- Provider credentials, payloads, and raw errors remain server-side.
- Previously delivered GTIN, backup, and provider-neutral infrastructure should remain available
  for later evidence-led experiments without burdening the active UI.

## Decision

### Manual first via IGDB

The global "Add physical game" UI uses one explicit three-step workflow:

1. Search IGDB by title through the existing authenticated Core/BFF enrichment search endpoint.
2. Explicitly select one result; no first result or exact title match is selected automatically.
3. Explicitly choose PS3, PS4, or PS5. IGDB platforms may be displayed as a non-authoritative hint
   and never initialize or change the physical-platform field.
4. Review a final summary containing the selected title, IGDB ID, and physical platform.
5. Create or reuse the game and add the copy only from the final confirmation action.

The search is delayed to avoid a request per keystroke. Each request has cancellation plus a
monotonic generation guard, so a late obsolete response cannot replace newer results even when a
transport does not honor cancellation. The UI distinguishes loading, no-result, and sanitized
temporary-failure states. Back and cancel actions only change local navigation state.

The result presentation and provider-neutral `GameMetadata` response are shared with Fix Match.
Title, cover, release year, summary, provider ID, and optional platform hints come from the existing
`IGameMetadataProvider.SearchManyAsync` implementation. No parallel IGDB client or browser-held
credential is introduced.

### Confirmed provider identity is the write authority

The browser sends only the explicitly selected provider ID, explicitly selected physical platform,
and a confirmation flag. Core verifies the provider ID again with
`IGameMetadataProvider.GetByIdAsync` before any database transaction begins. A missing or failed
verification produces a safe response and no write.

Inside a serializable transaction, Core searches the authenticated tenant's filtered
`UnifiedGames` for the exact pair `(MetadataSource, MetadataProviderId)`:

- if found, that tenant-local game receives a new independently identified physical copy;
- if absent, Core creates a new tenant-owned game from the verified metadata and its first physical
  copy atomically, using the existing tenant-unique slug allocation rules.

The lookup never ignores query filters, never accepts a tenant ID from the body, and never reuses a
different tenant's provider identity. Title or slug equality alone is not sufficient linkage proof.
Creation applies the same provider-neutral metadata and automatic-genre rules used by enrichment.

This workflow does not create or alter digital `LibraryItem` rows, `ManualLink` rows, sync state, or
provider ingestion. Repeated captures intentionally create repeated physical-copy rows.

### Safe failures and non-writes

IGDB search and selected-ID verification return bounded user-facing error messages. Provider
credentials, response bodies, transport diagnostics, and raw errors do not enter browser JSON.
Search success, no-result, failure, selection, platform changes, back, and cancel are write-free.
The server still rejects requests that omit final confirmation.

The active UI no longer exposes free-title creation. The older server-side manual-title capture
branch remains covered for compatibility but is not presented as a user workflow. Reintroducing a
free-title fallback requires a complete, independently usable UI and direct tests rather than a
partial escape hatch.

### Barcode, GTIN, and product catalogue are inactive UI experiments

Barcode scanning, typed GTIN entry, tenant-local GTIN suggestions, and external product-catalogue
hints are removed from the visible global capture flow. The now-unused camera component, decoder
abstraction, decoder tests, and ZXing dependency are removed from the frontend.

The following infrastructure remains intentionally unchanged:

- nullable, non-unique `PhysicalGameCopy.NormalizedGtin` and tenant-local lookup index;
- canonical GTIN-12/13/14 normalization and validation;
- Backup V3 optional `normalizedGtin`, V1/V2 compatibility, and CSV output;
- the provider-neutral, default-disabled product-catalogue adapter and benchmark harness; and
- existing physical-copy APIs that accept an optional GTIN.

These components preserve prior data and create an evidence-gathering boundary, but they are not an
active product promise. Barcode/OCR work and a possible UPC Database title-only experiment are
uncommitted backlog ideas with no defined next milestone. No OCR or cover recognition is added.

## Tenant Isolation and Security

- The controller remains authenticated and browser requests continue through `/api/core/*`.
- Provider search is stateless and persists no tenant or search state.
- Exact provider-identity reuse queries only the active tenant's filtered `UnifiedGames`.
- Newly created games, tags, and physical copies receive ownership from the trusted tenant context.
- Composite tenant/game relationships continue to protect physical-copy writes below query filters.
- No global or cross-tenant IGDB mapping is introduced.
- No provider result creates anything until the user reaches and activates final confirmation.
- No authentication, cookies, provider credentials, digital sync, or `.env` handling changes.

## Portability

Backup V3 remains the current format. Existing canonical GTIN values continue to export and restore,
repeated values remain valid, and V1/V2 remain importable. The IGDB-first UI changes no database
schema or backup shape.

## Consequences

### Positive

- The common task has a short, predictable search → select → platform → confirm flow.
- Physical-only games can be created with verified provider identity and useful metadata.
- Exact tenant-local provider identity avoids cross-tenant reuse and unsafe title guesses.
- The implementation reuses the established Fix Match provider contract and result presentation.
- Camera and decoder bundle/permission complexity leave the active product without discarding
  durable GTIN data or evaluation infrastructure.

### Costs and limitations

- IGDB availability is required for the active add flow; failures remain understandable and
  write-free, but there is no active free-title fallback.
- IGDB represents games and platform metadata, not guaranteed physical editions, packaging,
  regions, product codes, or special releases.
- Exact provider-ID reuse will not automatically attach an older tenant game that lacks that ID;
  title similarity is deliberately insufficient evidence.
- Barcode, OCR, regional/edition metadata, `PhysicalRelease`, and production catalogue selection
  remain unresolved and are not scheduled next.

## Implementation Status (2026-07-14)

Implemented and fixture-tested:

- shared Fix Match/physical result cards and the existing enrichment search endpoint;
- debounced search with cancellation and stale-response generation protection;
- explicit result and empty physical-platform state;
- final-confirmation-only provider capture;
- exact tenant-local provider reuse, provider-backed atomic creation, duplicate copies, and
  cross-tenant isolation;
- safe search/verification failures with no writes; and
- removal of the visible barcode/catalogue flow, camera decoder code, and ZXing dependency.

No live IGDB call was executed as part of automated verification. Tests use fakes and fixtures.

## References

- [ADR-008: Data Portability Format](008-data-portability-format.md)
- [Core Tenant-Isolation Audit](../tenant-isolation-audit.md)
- [Physical Product Catalogue Evaluation](../physical-product-catalog-evaluation.md)
- [MobyGames API](https://www.mobygames.com/info/api/)
- [GS1 Global Trade Item Number](https://www.gs1.org/standards/id-keys/gtin)
