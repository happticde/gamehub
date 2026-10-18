# Physical Product Catalogue Evaluation

**Date:** 2026-07-14
**Scope:** ADR-014 Physical Collection Phase 2B
**Decision:** No production provider selected; neutral adapter and benchmark harness implemented

## Decision summary

No reviewed provider can yet be selected responsibly. PriceCharting has the closest documented
game-specific shape, including direct UPC lookup and separate PlayStation/PAL console catalogues,
but its published Price Data terms prohibit use in software accessible to third parties without
express permission and do not clearly grant a metadata-only adapter. MobyGames models platform,
country, edition, and product-code provenance well, but its
documented REST endpoints do not provide a direct GTIN/product-code lookup and product-code access
is listed only in the Silver plan. Barcode Lookup and UPCitemdb provide direct generic GTIN lookup,
but neither publishes PS3/PS4/PS5 or EU/US coverage. Verified by GS1 verifies identifiers and basic
product/company data rather than acting as a game-release catalogue.

There is also no repository-owned, representative set of verified European and North American
PS3, PS4, and PS5 standard, localized, and special releases, and no paid provider access was
available for this evaluation. Consequently, no hit rate, title/platform accuracy, latency
percentile, or availability percentage is claimed. Phase 2B keeps the production provider set to
`None` and ships a provider-neutral adapter plus a benchmark harness for the missing evidence.

## Official-source comparison

| Provider | PS3/PS4/PS5 and EU/US evidence | GTIN/UPC/EAN lookup | License and use boundary | Authentication, current published cost, and limits | Latency, availability, maintenance |
|---|---|---|---|---|---|
| PriceCharting | Its official API console table separately lists PlayStation 3/4/5, PAL PlayStation 3/4/5, and Asian/Japanese variants. This demonstrates a region-aware catalogue structure, not barcode coverage or accuracy for GameHub's target set. | `/api/product` accepts `upc` and returns `product-name` plus `console-name`. The docs do not describe EAN/GTIN-14 semantics separately. | The published Price Data terms allow internal business use with a current Legendary subscription but prohibit Price Data use in third-party-accessible software without express written permission. They do not clearly grant the metadata-only title/platform use proposed here, so written permission remains necessary. | Static 40-character query token; Legendary is published at USD 49/month; API limit is one call/second and persistent excess can revoke access. | No SLA or target latency is published in the reviewed API/pricing/terms pages. The product is game-specific and structurally the closest fit, but permission, access, and measured EU/US coverage remain blockers. |
| MobyGames | Official platform pages cover PS3, PS4, and PS5. Standards explicitly bind product codes to release country and distinguish UK/European/localized/special releases from US releases. This is strong provenance guidance, not measured code completeness. | Product codes include UPC-A and EAN-13, but the documented REST endpoint list is game/platform oriented and does not expose a direct product-code lookup. The subscription page lists product codes only in Silver. | Attribution to MobyGames is required; direct competition, repackaging/resale, and claiming ownership are prohibited. Commercial use is listed for paid plans. | Private query API key. Bronze: USD 99.99/month and one request/second; Silver with product codes: USD 499.99/month and four requests/second. Non-commercial access is documented at 720 requests/hour with a maximum of one/second. | No SLA or target latency is published in the reviewed pages. Long-running game-domain curation is attractive, but a reverse GTIN endpoint and measured product-code coverage are not established. |
| Barcode Lookup | No official PS3/PS4/PS5, game-category, or EU/US release-coverage figures are published. The `geo` filter limits store results (`us`, `gb`, `ca`, `eu`); it is not documented as product-release provenance. | Direct `barcode` query accepts 7, 8, 10, 11, 12, 13, or 14 digits and returns at most one product when data exists. | Subscription terms provide returned product data but explicitly make no guarantee that a lookup returns product data. | API key in the query string. Published plans start at USD 99/month for 5,000 calls; terms cap normal use at 100 calls/minute and a rate-limit endpoint reports monthly/minute allowance. | No SLA or target latency is published. Query-string credentials require extra care in HTTP logging. Generic catalogue maintenance and target-game coverage are unproven. |
| UPCitemdb | No official PS3/PS4/PS5 or EU/US release-coverage figures are published. Responses expose generic title/category/brand/offer data rather than a PlayStation release model. | Direct lookup accepts UPC, EAN, GTIN, or ISBN. | Paid service grants a limited, non-exclusive, non-transferable, terminable license for customer operations. Data is provided as-is with no availability or accuracy guarantee, and the service may be modified or discontinued. | Free trial needs no key: 100 combined requests/day, six lookups/minute, sustainable one request/10 seconds. DEV uses header credentials, costs USD 99/month, permits 20,000 lookups/day, 15 lookups/30 seconds, and a sustainable one/2 seconds. PRO is USD 699/month. | No SLA or target latency is published. The documented API-gateway 5xx possibility and mutable limits are benchmark cases; game-specific maintenance remains unknown. |
| Verified by GS1 | Global GTIN/company/product verification is documented, but no PS3/PS4/PS5 platform taxonomy, release region, or game-title completeness is promised. | Lookup verifies GTIN/UPC/EAN identity. Enterprise API access is arranged through a GS1 member organization. | Use is governed by Verified by GS1 terms and member-organization access; this evaluation found no public game-catalogue redistribution grant. | Public manual lookup exists; advanced API access is through a member organization. Public global API price and rate-limit figures were not documented in the reviewed pages. | GS1 is the authoritative identifier system and useful for validation/provenance, but it does not document the title/platform hints required by this feature. No applicable SLA was found. |

## Official references

- PriceCharting: [API documentation](https://www.pricecharting.com/api-documentation),
  [Legendary pricing](https://www.pricecharting.com/pricecharting-pro?f=home), and
  [terms of service](https://www.pricecharting.com/page/terms-of-service).
- MobyGames: [API documentation](https://www.mobygames.com/info/api/),
  [subscription plans](https://www.mobygames.com/api/subscribe/),
  [product-code standards](https://www.mobygames.com/info/product-codes/), and
  [release/product-code standards](https://www.mobygames.com/info/standards/).
- Barcode Lookup: [API documentation](https://www.barcodelookup.com/api-documentation),
  [pricing](https://www.barcodelookup.com/api), and
  [terms and conditions](https://www.barcodelookup.com/terms-and-conditions).
- UPCitemdb: [API overview](https://devs.upcitemdb.com/),
  [plan and rate-limit comparison](https://www.upcitemdb.com/wp/docs/main/development/plan/), and
  [terms](https://www.upcitemdb.com/terms).
- GS1: [Verified by GS1](https://www.gs1.org/services/verified-by-gs1) and
  [service explanation/API access boundary](https://support.gs1.org/support/solutions/articles/43000734077-what-is-verified-by-gs1-).

## Production decision gate

A provider can be proposed only after all of the following are available:

1. Written terms or permission compatible with GameHub's intended self-hosted use and display of
   title/platform hints.
2. A server-side credential and a non-production provider account with cost and quota approval.
3. An operator-curated dataset with balanced PS3/PS4/PS5 and EU/US standard, localized, and special
   releases. Each case must have a verified GTIN, exact expected title, expected platform, region,
   and provenance retained outside benchmark output.
4. Repeated benchmark runs recording actual hit/no-result, correct title/platform, p50/p95 latency,
   429 behavior, malformed responses, and temporary failures without logging raw identifiers or
   payloads.
5. A maintenance review covering provider change notices, API versioning, support channel, SLA (or
   explicit absence), credential rotation, cancellation, and a tested `None` rollback.

## Benchmark harness

The harness is `tools/product-catalog-benchmark`. It calls the same neutral adapter and timeout/
payload boundary as Core. It processes cases sequentially to keep provider throttling observable.
Reports contain case IDs, region/platform expectations, safe outcome categories, measured latency,
and measured hit/outcome/title/platform counts and rates. Reports deliberately omit GTINs, returned
titles, raw payloads, credentials, and provider errors.

Dataset shape:

```json
{
  "datasetName": "operator-curated-ps3-ps5-eu-us-v1",
  "cases": [
    {
      "caseId": "eu-ps4-001",
      "gtin": "<verified GTIN-12, GTIN-13, or GTIN-14>",
      "region": "EU",
      "expectedTitle": "<verified cover title>",
      "expectedPlatform": "PS4",
      "expectedResult": true
    }
  ]
}
```

Run only in the devcontainer:

```bash
docker exec -w /workspaces/gamehub \
  gamehub_devcontainer-app-1 \
  dotnet run --project tools/product-catalog-benchmark/GameHub.ProductCatalogBenchmark.csproj -- \
  --dataset /path/to/operator-owned-dataset.json \
  --output /path/to/sanitized-report.json
```

With the shipped `ProductCatalog__Provider=None`, the harness returns exit code `2` and labels the
output as a harness check, not a live benchmark. No live benchmark was run for Phase 2B.

## Configuration and secret boundary

The committed safe defaults are `ProductCatalog:Provider=None` and a 1,500 ms timeout. Equivalent
optional server variables are `ProductCatalog__Provider=None` and
`ProductCatalog__TimeoutMilliseconds=1500` (allowed range 250–5,000). No provider credential
variable exists or is required in this phase. A future adapter must introduce a provider-specific
server-only credential variable only after the decision gate; it must never enter frontend code,
browser JSON, logs, benchmark reports, or version-controlled configuration.
