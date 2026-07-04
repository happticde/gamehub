import assert from "node:assert/strict";
import test from "node:test";
import {
    parseGogConnectPayload,
    parseHandoffUrl,
    parseHelperLaunchStatus,
    parsePsnConnectPayload,
} from "../lib/platform-connect.ts";

test("GOG helper payload accepts current contract aliases", () => {
    const result = parseGogConnectPayload(JSON.stringify({
        provider: "gog",
        user: "gog-user",
        cookies: { "gog-al": "secret-cookie" },
        capturedAtUtc: "2026-07-03T12:00:00Z",
    }));

    assert.equal(result.provider, "gog");
    assert.equal(result.user, "gog-user");
    assert.equal(result.cookie, "secret-cookie");
    assert.equal(result.expired, false);
});

test("PSN helper payload maps cookies to the vault credential shape", () => {
    const result = parsePsnConnectPayload(JSON.stringify({
        provider: "psn",
        cookies: {
            pdccws_p: "secret-pdccws",
            npsso: "secret-npsso",
            "sb-cdn.session": "secret-session",
        },
        capturedAtUtc: "2026-07-03T12:00:00Z",
    }));

    assert.equal(result.provider, "psn");
    assert.deepEqual(result.cookies, {
        pdccws_p: "secret-pdccws",
        npsso: "secret-npsso",
        "sb-cdn.session": "secret-session",
    });
    assert.equal(result.expired, false);
});

test("PSN helper payload rejects missing cookies", () => {
    const result = parsePsnConnectPayload(JSON.stringify({
        provider: "psn",
        cookies: {},
    }));

    assert.match(result.message, /missing the cookies object/);
});

test("local handoff URL must match the selected provider path", () => {
    const parsed = parseHandoffUrl(
        "http://127.0.0.1:49152/connect/psn/result?nonce=nonce-value",
        "psn",
        "http://localhost:3005",
    );

    assert.equal(
        parsed,
        "http://127.0.0.1:49152/connect/psn/result?nonce=nonce-value&origin=http%3A%2F%2Flocalhost%3A3005",
    );
    assert.match(
        parseHandoffUrl(
            "http://127.0.0.1:49152/connect/gog/result?nonce=nonce-value",
            "psn",
            "http://localhost:3005",
        ).message,
        /PlayStation loopback result endpoint/,
    );
});

test("launch status parsing exposes only status and handoff URL", () => {
    const result = parseHelperLaunchStatus({
        status: "handoff_ready",
        handoffUrl: "http://127.0.0.1:49152/connect/psn/result?nonce=nonce-value",
        npsso: "secret-cookie",
    });

    assert.deepEqual(result, {
        status: "handoff_ready",
        handoffUrl: "http://127.0.0.1:49152/connect/psn/result?nonce=nonce-value",
        message: undefined,
    });
});
