# ADR-014: Human-Confirmed Hybrid Capture for Physical Games

**Date:** 2026-07-14
**Status:** Accepted
**Deciders:** @jankahnt

## Context

Physical Collection Phase 1 added tenant-owned PS3, PS4, and PS5 copies to an existing
`UnifiedGame`. The current detail-page flow is intentionally manual: the game is already known and
the user selects only the physical platform.

The original follow-up idea was to add camera-based UPC/EAN scanning. That framing combines three
different problems:

1. reading a machine-readable value from a camera frame;
2. resolving that value to a trustworthy game release; and
3. selecting or creating the tenant-owned `UnifiedGame` to which a copy belongs.

Only the first problem is solved by a barcode decoder. A GTIN identifies a class of trade item, not
an individual owned copy, and the encoded number does not itself contain a game title or platform.
Product metadata therefore requires a separate catalogue. Coverage, regional variants, special
editions, licensing, rate limits, and provider longevity become part of the feature's reliability.

Barcode scanning also provides little value inside the existing game detail page: the game is
already selected and choosing PS3, PS4, or PS5 is faster than granting camera access and aiming at a
case. Scanning becomes valuable only as an entry point from outside a game detail page, especially
while processing several cases.

OCR and whole-cover recognition can work when a barcode is absent or damaged, but they return
probabilistic title/platform hints rather than stable product identifiers. Stylized logos,
localized covers, subtitles, age-rating marks, promotional text, glare, perspective, and spine-only
photos make automatic assignment unsafe.

The capture strategy must also handle physical-only games that are not yet present in the tenant's
digitally synchronized library. Automatically creating or linking games from an external or
probabilistic result would weaken the existing unification and tenant-isolation invariants.

## Decision Drivers

- A correct tenant-owned association matters more than a zero-confirmation scan.
- Every supported browser and every case must retain a usable path without camera or catalogue
  access.
- Camera frames and cover images should remain on the device whenever practical.
- External provider credentials must remain server-side and provider failure must degrade to local
  search rather than block collection management.
- Multiple copies and repeated GTINs are valid; a retail identifier is not a copy identifier.
- Confirmations from one tenant must not become an implicit shared catalogue for another tenant.
- Durable barcode data must participate in backup and restore with an explicit format version.
- The first implementation should not commit GameHub to a catalogue or OCR dependency before its
  real coverage, latency, bundle cost, and operational behavior are measured.

## Options Considered

| Approach | Main strength | Critical limitation | Decision |
|---|---|---|---|
| Existing-game manual selection | Deterministic, private, and independent of external services | Slower for a large collection and cannot by itself represent a missing game | Required baseline |
| Barcode scan or typed GTIN | Fast, checksum-verifiable capture of a retail identifier | Needs a separate product catalogue; the same code is shared by copies and may vary by release/region | Primary optional accelerator |
| Cover/spine OCR | Works without a readable barcode and can seed title/platform search | Probabilistic on stylized or localized packaging; needs image processing and language data | Secondary experiment and fallback hint |
| Whole-cover visual recognition or embeddings | Can use artwork when text and barcode fail | Highest model, privacy, hosting, and false-match complexity | Deferred |
| CSV or catalogue import | Efficient for users who already have structured collection data | Not a camera workflow and depends on an existing source file | Separate future bulk-import path |
| Barcode-only automatic assignment | Very few taps on a successful lookup | Silent wrong matches, catalogue lock-in, and no useful fallback | Rejected |

## Decision

### One global assisted-capture funnel

Phase 2 is defined as **assisted physical-game capture**, not barcode scanning alone. GameHub will
add a global, mobile-first "Add physical game" entry point while retaining the Phase 1 quick-add
action on an existing game detail page.

The global funnel follows this order:

1. Start with tenant-scoped game search as the universal path.
2. Offer barcode scan and manual GTIN entry as optional accelerators.
3. Resolve a valid GTIN through tenant-local confirmed mappings and, when configured, a
   provider-neutral server-side product-lookup adapter.
4. Present candidate game and platform information to the user.
5. Require explicit confirmation of the target `UnifiedGame` and physical platform before writing
   a copy.
6. If no existing game matches, offer an explicit manual-game creation path when that implementation
   slice is available. Until then, report that the game is missing and retain manual search; never
   create it silently.
7. Evaluate on-device OCR only for the failed/no-barcode branch. OCR output seeds the same
   tenant-scoped search and confirmation UI; it never writes a game or copy directly.

The flow must remain usable when camera permission is denied, no camera exists, native detection is
unavailable, a decoder fails, the product provider has no result, or the provider is unavailable.

### Barcode capture is capability-based

Camera access starts only after an explicit user action and only in a secure context. The UI must
handle pending, denied, unavailable, and interrupted camera states and stop every media track when
the capture surface closes or succeeds.

The frontend will use a decoder abstraction rather than bind product behavior directly to the
browser's `BarcodeDetector`. Native detection may be used as a progressive enhancement after a
runtime capability and supported-format check. It cannot be the only implementation because the API
is still experimental and not available in all widely used browsers. A JavaScript/Wasm fallback may
be selected in the implementation phase only after the normal dependency/version review.

The initial retail formats are GS1 GTIN representations relevant to game packaging. Scanner and
manual input use the same normalization and check-digit validation. The canonical value is stored
as a string so leading zeroes are never lost. Non-retail QR codes and unrelated symbologies are not
treated as product identifiers.

### Barcode values are hints, not copy identity

Once the user confirms the association, `PhysicalGameCopy` may gain a nullable `NormalizedGtin`
field containing the canonical GTIN string. The observed barcode symbology is capture provenance,
not collection-domain data, and remains transient unless a later requirement demonstrates value in
persisting it.

No uniqueness constraint is added for the GTIN, whether global, per tenant, per game, or per
platform. Multiple owned copies of one release must remain representable. Manual copies without a
barcode remain valid.

A GTIN semantically identifies a release/trade-item class rather than an owned instance. Storing it
on each copy is an intentional, minimal denormalization for the current model. GameHub will introduce
a separate `PhysicalRelease` entity only when edition, region, release-level metadata, or shared
release maintenance creates a demonstrated need. It will not create that abstraction solely in
anticipation of future fields.

### Matching remains tenant-scoped and human-confirmed

Previously confirmed GTIN associations may accelerate later scans only within the active tenant.
Conflicting local associations produce candidates rather than a guessed winner. A tenant's
confirmation is user-owned data and must not train, populate, or modify a cross-tenant shared mapping
without a separately designed moderation and provenance model.

External product results are untrusted suggestions. Provider keys remain in Core server
configuration, calls happen server-side, and browser requests continue through `/api/core/*`.
Provider payloads do not select a tenant, a game ID, or a physical-copy ID. Title similarity, slug
equality, platform claims, and provider confidence may rank candidates but cannot bypass the final
confirmation.

No lookup provider is selected by this ADR. Before choosing one, a representative PS3/PS4/PS5 set
covering European and North American standard, localized, and special releases must measure:

- camera decode success;
- catalogue hit rate;
- correct title/platform/release candidate rate;
- lookup latency and rate-limit behavior;
- licensing, key handling, cost, and self-hosting implications; and
- the time from capture start to confirmed copy compared with manual search.

The adapter must degrade to manual tenant search and must not make adding or viewing existing
copies dependent on provider availability.

### OCR is a bounded fallback, not an authority

OCR is not part of the first production barcode slice. A later prototype may process a user-selected
camera frame locally and send only user-reviewed candidate text into tenant-scoped search. Raw frames
and cover images are not uploaded, persisted, cached, included in backup, analytics, or logs by
default.

An external or server-side OCR service, whole-cover recognition, or image retention requires a
separate privacy, dependency, cost, and operations decision. OCR title/platform guesses always use
the same confirmation screen as barcode and manual search.

### Explicit manual game creation

A complete physical collection cannot require prior digital ownership. The assisted-capture funnel
therefore needs an explicit way to create a tenant-owned manual `UnifiedGame` when no candidate
exists. That action is user-confirmed and separate from scan recognition.

Its implementation must preserve tenant-scoped slug rules, enrichment behavior, later digital sync
and unification, merge/split semantics, and duplicate handling. It must have direct tests before the
UI exposes it. This ADR does not authorize automatic creation from a barcode provider, OCR text, or
image model.

### Security, privacy, and observability

- Camera permission is requested only from a visible user gesture and camera activity ends promptly.
- Raw frames, OCR input, GTINs, OCR text, and provider payloads are excluded from operational logs.
- Safe outcome categories may be recorded without identifiers or captured content.
- Product-provider credentials, quotas, and raw errors never reach browser JavaScript.
- All persisted copy fields, searches, mappings, writes, merges, deletes, and restores remain scoped
  to the authenticated tenant and retain the existing composite tenant/game relationship.
- No request body may select tenant ownership.
- Provider and decoder failures must not change existing copies or digital sync state.

### Portability

Backup V2 defines each physical copy as a platform-only entry and its parser deliberately rejects
unknown shape changes. Persisting barcode data therefore requires Backup V3 rather than silently
extending V2.

V3 will add an optional normalized GTIN field to physical-copy entries. Restore must continue to
accept V1 as no physical data and V2 as physical copies without barcode data. Restored ownership
always comes from the authenticated tenant context, and repeated GTINs remain valid.

No backup version change occurs merely from accepting this ADR; V3 is introduced only with durable
barcode storage.

## Phase 2A Implementation Status (2026-07-14)

The deterministic global capture and barcode-foundation slice is implemented:

- `PhysicalGameCopy` retains its independent tenant-owned ID and now has only one additional
  nullable `NormalizedGtin` string. The non-unique `(TenantId, NormalizedGtin)` index supports local
  lookup while repeated values remain valid globally, across tenants, across games, and across
  multiple copies of one game.
- One server-side normalizer accepts checksum-valid GTIN-12, GTIN-13, and GTIN-14 plus single ASCII
  spaces or hyphens between digit groups, then stores the zero-padded GTIN-14 representation.
  Eight-digit input is deliberately rejected: GS1 documents that EAN-8 encodes GTIN-8 while UPC-E
  carries a compressed GTIN-12 in the same displayed length, so a raw untrusted value cannot safely
  choose the meaning. UPC-E is not silently treated as GTIN-8.
- The global mobile-first UI searches tenant-owned games, lists every tenant-local GTIN candidate,
  requires explicit selection when mappings conflict, supports deliberate manual-game creation,
  and shows title, existing/new record, PS3/PS4/PS5 platform, and optional canonical GTIN before the
  confirmed write. The detail-page quick-add remains available and accepts copies without a GTIN.
- Existing-game capture is tenant-filtered. Manual game plus physical copy is one serializable
  transaction; the existing slug generator is reused, collisions receive deterministic
  `-manual-N` suffixes without merging, and the unsuffixed normal case remains discoverable by later
  digital synchronization.
- Camera capture starts only from a visible action, requests video without audio with an
  environment-facing preference, and stops every track on success, cancellation, close, navigation,
  or unmount. Native `BarcodeDetector` is used only after runtime format checks; the fallback is
  dynamically loaded and frames never leave the device.
- Backup V3 is implemented with optional per-copy `normalizedGtin`. New exports use V3; validation
  rejects invalid or non-canonical values before writes; repeated values are valid; V1 and V2 remain
  importable; restore derives ownership from the authenticated tenant; CSV renders the GTIN as text.

### Decoder dependency decision

The reviewed fallback is `@zxing/browser` 0.2.1, published 2026-07-06 under MIT. Its required
`@zxing/library` 0.23.0 peer is Apache-2.0 and describes itself as maintenance-mode-only, although it
was updated in April 2026. Registry metadata reports approximately 5.8 MB and 11.9 MB unpacked
installation size respectively; dynamic import keeps this code outside the initial capture page
path. The libraries support browser video decoding plus EAN-13 and UPC-A. No OCR, image-recognition,
or product-catalogue dependency was added; the runtime reader is constrained to EAN-13 and UPC-A
and does not enable QR capture.

### Deliberate remaining scope

External product resolution, provider selection and benchmarking, OCR/cover recognition, GTIN-8 or
UPC-E expansion with trusted provenance, regional/edition metadata, and a `PhysicalRelease` entity
remain open. A first-time unknown barcode therefore continues to tenant search or explicit manual
creation, and no scan or provider data can create or select a game automatically.

## Rollout Plan

1. **Completed in Phase 2A:** global deterministic intake with tenant-scoped search, explicit
   platform selection, tested manual game creation, local barcode decoding/manual GTIN entry,
   tenant-local confirmed mappings, nullable copy storage, migrations, and Backup V3.
2. **Deferred from Phase 2A:** benchmark and select a provider-neutral external product adapter only
   if it improves first-time unknown-code capture without weakening confirmation or availability.
3. **Provider selection:** choose and configure a production provider only after the representative
   coverage and operational benchmark.
4. **OCR experiment:** compare the failed/no-barcode fallback with manual search on real cases; ship
   only if it materially improves successful confirmation time without weakening privacy or
   correctness.
5. **Later options:** consider structured bulk import or visual recognition from measured user need,
   each with its own data and privacy review.

## Consequences

### Positive

- The common path remains useful without camera, browser support, provider access, or readable
  packaging.
- Barcode scanning can be very fast without becoming an automatic source of truth.
- Physical-only games have a deliberate path into the unified library.
- On-device capture minimizes image disclosure and server cost.
- Provider and decoder choices remain replaceable and measurable.
- Existing duplicate-copy, digital-provider, merge/split, tenant, and backup invariants stay
  explicit.

### Costs and limitations

- Every automated suggestion still requires one confirmation step.
- Barcode value depends on catalogue coverage for a useful first-time suggestion.
- Manual game creation adds unification and duplicate-management test scope.
- Nullable copy-level GTIN storage duplicates release-level data until a release entity is justified.
- OCR is deferred from the first production slice and may never outperform manual search reliably
  enough to ship.
- Backup V3 and provider-specific operational work are required when durable barcode capture is
  implemented.

## References

- [GS1 Global Trade Item Number](https://www.gs1.org/standards/id-keys/gtin)
- [GS1 System Architecture](https://www.gs1.org/standards/gs1-system-architecture-document/current-standard)
- [WICG Shape Detection API](https://wicg.github.io/shape-detection-api/#barcode-detection-api)
- [MDN Barcode Detection API](https://developer.mozilla.org/en-US/docs/Web/API/Barcode_Detection_API)
- [MDN `getUserMedia()`](https://developer.mozilla.org/en-US/docs/Web/API/MediaDevices/getUserMedia)
- [ZXing for JS browser layer](https://github.com/zxing-js/browser)
- [Tesseract User Manual](https://tesseract-ocr.github.io/tessdoc/)
- [Tesseract.js](https://github.com/naptha/tesseract.js)
- [MobyGames API](https://www.mobygames.com/info/api/)
- [MobyGames release and product-code standards](https://www.mobygames.com/info/standards/)
- [ADR-008: Data Portability Format](008-data-portability-format.md)
- [Core Tenant-Isolation Audit](../tenant-isolation-audit.md)
