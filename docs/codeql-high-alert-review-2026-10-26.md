# CodeQL High alert review

Review date: 2026-10-26. Baseline: `71581640b98dfc3ba6a0641fe6ba7a197b5900d5` on `origin/main`.
The three alert pages and their expanded paths were read again in GitHub.
This review does not dismiss alerts or authorize a merge.

| Alert | Rule | Assessment of the reported path |
| --- | --- | --- |
| [#1](https://github.com/happticde/gamehub/security/code-scanning/1) | `cs/web/xss` | False positive for the reported sink. The text is sent to IGDB as an outbound plain-text query, not written to a browser HTML response. |
| [#15](https://github.com/happticde/gamehub/security/code-scanning/15) | `cs/user-controlled-bypass` | False positive for GameHub authentication bypass. Platform selection skips preparation specific to another provider, after mandatory internal authorization. |
| [#16](https://github.com/happticde/gamehub/security/code-scanning/16) | `cs/user-controlled-bypass` | False positive for an accepted schema bypass. Unsupported versions already failed unconditional validation before persistence. This change moves that rejection earlier. |

These are conclusions about the shown paths. They are not a claim that all application
authentication, provider input, or browser rendering paths are safe.

## Alert #1: actual output context

The 17-step path starts at `LibraryController.SearchEnrichment(title)`. It passes through
`IgdbClient.SearchManyAsync`, `EscapeSearchText`, and `QueryGamesAsync`. The reported sink is
`StringContent(query, Encoding.UTF8, "text/plain")`.

That content belongs to an `HttpRequestMessage`. `HttpClient.SendAsync` sends it with POST to
the fixed `https://api.igdb.com/v4/games` URI. The constructor does not create an MVC HTML
response. The provider response is deserialized into game records, mapped to metadata, and
returned by the controller through `Ok(results)` and the JSON formatter.

The separate provider query-language boundary still matters. `EscapeSearchText` escapes
backslashes before quotes. HTML encoding would change the search title without protecting
the reported output context.

`IgdbClientTests.Search_HtmlAndQuerySyntax_UsesFixedPlainTextProviderRequest` exercises both
search methods with script tags, an event-handler attribute, quotes, backslashes, and a
query-clause escape attempt. It asserts the exact URI, POST, plain-text content type, UTF-8,
one provider request, and the requested result limit. It parses the wire string literal
independently and checks that the original title round-trips. The existing special-character
tests remain in place. No encoding or query-syntax production change was needed.

## Alert #15: provider preparation is not application authorization

The six-step path starts at the platform route parameter in Ingest's
`PlatformCredentialsController.Upsert`. It reaches `BuildPlaintextAsync` through
`PlatformCredentialService.UpsertAsync`. The flagged condition is `platform != Platform.Epic`.
GitHub references both `PlatformCredentialPayloadCodec.ReadEpicAuthorizationCode` and
`IEpicAuthenticationClient.ExchangeAuthorizationCodeAsync` as sensitive actions. The first
reads a JSON field. The second exchanges provider credentials. Neither authorizes a GameHub
caller; that boundary has already run in the HTTP authentication middleware.

The controller requires the `TenantDelegation` scheme. Its handler checks the internal
service key and exactly one non-empty tenant GUID before issuing the authenticated tenant
claim. `AuthenticatedIngestTenantContext` reads that claim. The database query filter and save
guards then scope the vault row. A valid internal service can select a delegated tenant by
design; the tenant header alone is not authority.

For Steam, GOG, and PSN, skipping the Epic code reader is required behavior. For Epic, an
absent code selects the supported refresh-token input. A supplied code takes precedence,
including when a refresh token is also present. It is exchanged through the Epic client.
An accepted refresh-token input means it was stored in the encrypted vault, not that Epic
has authenticated it. Provider retrieval must still obtain a valid provider session.

Before encrypting or saving, every prepared payload passes
`PlatformCredentialPayloadCodec.Validate`. Status responses contain no plaintext.

`PlatformCredentialAuthorizationTests` exercises MVC endpoint authorization with the real
API-key and tenant-delegation handlers and authentication/authorization middleware:

- Ten invalid authentication/header configurations each try Steam, Epic code, Epic refresh,
  and mixed Epic inputs. All 40 requests must return 401. The byte-for-byte vault snapshot
  must stay unchanged, and no Epic exchange may run.
- Six valid input variants update only the delegated tenant. A conflicting tenant ID in the
  body cannot select the owner. The other tenant's encrypted rows must remain identical.
- Code exchange rejection and an invalid exchanged credential are tested with empty and
  existing vaults. Both use a mixed input to prove that failure cannot fall back to the
  supplied refresh token. No partial write, response secret, or logged secret is accepted.

The HTTP harness uses an isolated test host, SQLite, and real controllers and handlers. It does not
launch the complete production entry point or test live Core-to-Ingest networking. The
production scheme selection was reviewed as source; it forwards either internal header to
tenant delegation and does not loosen the controller's explicit scheme requirement.

## Alert #16: unsupported schemas could not reach persistence

The six-step path starts at `UpsertPlatformCredentialRequest.SchemaVersion`. It reaches the
old `schemaVersion != 1` branch in `BuildPlaintextAsync`. That branch serialized the request
without reading an Epic code. The resulting bytes then passed unconditional codec validation
in `UpsertAsync`. The codec rejected every version other than 1 before the database lookup,
encryption, or save. Thus, skipping the code reader did not produce an accepted credential.

The implementation now calls the same schema validator during initial request validation.
The unused schema argument and unsupported-version serialization branch are removed from
preparation. The final payload validator remains unconditional. This is earlier rejection,
not a new authorization check or a query suppression. The public input range remains bounded
by `MaximumSchemaVersion`; accepted credential schemas are still only version 1.

`PlatformCredentialServiceTests.UnsupportedSchemas` covers all four providers and both Epic
input forms with versions `int.MinValue`, -1, 0, 2, 100, 101, and `int.MaxValue`. Each of the
35 cases checks both a new record and replacement of an existing record. It verifies no code
exchange, no accepted new row, and unchanged rows for both tenants.

The new Epic shape tests also found a separate validation defect: number, boolean, object,
and array values for `authorizationCode` raised `InvalidOperationException` at `GetString`.
They now produce a fixed 400 validation response. Null and empty codes retain their required
field error. All six tests include a valid refresh-token fallback to ensure that an invalid
supplied code cannot silently select it. This defect was not evidence of the reported
authentication bypass.

## Verification and limits

- Core and Ingest builds passed in Docker with zero warnings and errors.
- The full Docker .NET suite passed: 591 tests, zero failed or skipped.
- Existing tenant-delegation, credential isolation, and stored-retrieval tests also ran.
- Epic client rejection tests cover HTTP 400, 401, and 403 with a secret-bearing response body.
  Exceptions and captured logs must not contain the submitted code or provider response text.
- All provider interactions are controlled test responses. No live provider credentials,
  exchange, or external game API request was used.

No authentication scheme, tenant policy, dependency, migration, or production configuration
was changed. No CodeQL rule was disabled or broadly suppressed. No alert was manually closed
or dismissed, and no alert-management mutation was called. The original High alerts may remain
visible after analysis. Their presence alone does not replace the data-flow assessment and
regression evidence above.
