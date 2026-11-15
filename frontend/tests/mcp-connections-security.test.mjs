import assert from "node:assert/strict";
import test from "node:test";
import { parseMcpConnections } from "../lib/mcp-connections.ts";

const connection = { id: "11111111-1111-4111-8111-111111111111", clientId: "gamehub-mcp-stdio",
    resource: "https://gamehub.example/api", scopes: ["library:read"],
    createdAtUtc: "2026-11-15T10:00:00Z", expiresAtUtc: "2026-12-15T10:00:00Z", revokedAtUtc: null };

test("connection state accepts empty, read-only and optional tracking consents", () => {
    assert.deepEqual(parseMcpConnections([]), []);
    assert.deepEqual(parseMcpConnections([connection]), [connection]);
    assert.deepEqual(parseMcpConnections([{ ...connection, scopes: ["library:read", "tracking:write"] }])[0].scopes,
        ["library:read", "tracking:write"]);
});

test("connection state rejects accidental secret exposure and malformed or ambiguous permissions", () => {
    for (const invalid of [null, {}, [{ ...connection, accessToken: "never-store" }],
        [{ ...connection, providerSessionId: "never-expose" }], [connection, connection],
        [{ ...connection, scopes: ["tracking:write"] }], [{ ...connection, scopes: ["library:read", "admin"] }],
        [{ ...connection, resource: "javascript:alert(1)" }], [{ ...connection, createdAtUtc: "2026-11-15T10:00:00" }],
        [{ ...connection, expiresAtUtc: "invalid" }], [{ ...connection, revokedAtUtc: "2026-11-16T10:00:00Z" }]]) {
        assert.throws(() => parseMcpConnections(invalid));
    }
});
