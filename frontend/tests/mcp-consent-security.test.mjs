import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { runInNewContext } from "node:vm";
import ts from "typescript";
import { NextRequest, NextResponse } from "next/server.js";
import { parseMcpConsentInput, parseMcpConsentPreview } from "../lib/mcp-consent-contract.ts";
import { isTrustedBrowserMutation } from "../lib/browser-origin-security.ts";
import { safeReturnPath } from "../lib/auth-return-path.ts";

const origin = "https://issuer.example";
const display = () => ({ confirmation: "opaque-confirmation", instance: origin, resource: `${origin}/api`,
    account: "Owner", accountReference: "11111111-1111-1111-1111-111111111111", trackingWriteRequested: true,
    renewalRequested: true, expiresAtUtc: new Date(Date.now() + 60_000).toISOString() });

test("browser input cannot choose ownership, scope or protocol destination", () => {
    assert.deepEqual(parseMcpConsentInput({ action: "preview", userCode: "ABCD-EFGH" }), { action: "preview", userCode: "ABCD-EFGH" });
    for (const extra of ["tenantId", "accountId", "scope", "resource", "provider", "url"]) {
        assert.throws(() => parseMcpConsentInput({ action: "preview", userCode: "ABCD", [extra]: "chosen" }));
    }
    for (const invalid of [null, [], { action: "approve", userCode: "ABCD" },
        { action: "preview", userCode: "ABCD", allowTrackingWrite: true },
        { action: "approve", userCode: "ABCD", confirmation: "valid", allowTrackingWrite: "true" }]) {
        assert.throws(() => parseMcpConsentInput(invalid));
    }
});

test("display shape pins the instance/resource, rejects expiry and drops unknown upstream data", () => {
    const preview = display();
    assert.deepEqual(parseMcpConsentPreview({ ...preview, accessToken: "must-not-forward" }, origin), preview);
    for (const change of [{ instance: "https://other.example" }, { resource: `${origin}/mcp` },
        { expiresAtUtc: "invalid" }, { expiresAtUtc: new Date(0).toISOString() }, { trackingWriteRequested: "true" }]) {
        assert.throws(() => parseMcpConsentPreview({ ...preview, ...change }, origin));
    }
    assert.equal(safeReturnPath("/mcp/verify"), "/mcp/verify");
});

async function loadTs(path, modules, globals = {}) {
    const source = await readFile(new URL(`../${path}`, import.meta.url), "utf8");
    const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText;
    const exports = {};
    runInNewContext(code, { exports, require: name => {
        if (!(name in modules)) throw new Error(`Unexpected dependency ${name}`);
        return modules[name];
    }, Buffer, URL, URLSearchParams, AbortSignal, ...globals });
    return exports;
}

async function routeHarness(options = {}) {
    const calls = [];
    const auth = {
        ACCESS_TOKEN_COOKIE: "access", REFRESH_TOKEN_COOKIE: "refresh",
        isAccessTokenFresh: token => token === "browser-ticket",
        rotateRefreshToken: async () => { calls.push("rotate"); return options.rotation ?? { kind: "rotated", tokens: { accessToken: "rotated-browser-ticket" } }; },
        setAuthCookies: () => calls.push("setCookies"),
        createSessionEndedApiResponse: () => NextResponse.json({ code: "session-ended" }, { status: 401 }),
        createAuthenticationUnavailableResponse: status => NextResponse.json({ code: "unavailable" }, { status }),
    };
    const route = await loadTs("app/api/mcp/consent/route.ts", {
        "next/server": { NextResponse },
        "@/lib/browser-request": { rejectUnsafeBrowserRequest: request => isTrustedBrowserMutation(request.headers, origin) ? null : NextResponse.json({}, { status: 403 }) },
        "@/lib/auth-session": auth,
        "@/lib/mcp-consent-contract": { parseMcpConsentInput, parseMcpConsentPreview },
        "@/lib/mcp-consent": { getMcpConsentConfig: () => ({ instance: origin }), sendMcpConsent: async (input, token) => {
            calls.push({ input, token });
            if (options.throwUpstream) throw new Error("network uncertain");
            return options.upstream ?? new Response(JSON.stringify(display()), { status: 200 });
        } },
    });
    const request = (body, headers = {}) => new NextRequest(`${origin}/api/mcp/consent`, {
        method: "POST", headers: { origin, "content-type": "application/json", cookie: "access=browser-ticket; refresh=browser-refresh", ...headers },
        body: typeof body === "string" ? body : JSON.stringify(body),
    });
    return { POST: route.POST, calls, request };
}

test("cross-origin and sibling requests fail before body parsing, cookies or upstream work", async () => {
    for (const headers of [{ origin: "https://evil.example" }, { "sec-fetch-site": "cross-site" }, { origin: "https://sibling.issuer.example" }]) {
        const h = await routeHarness();
        assert.equal((await h.POST(h.request("invalid-json", headers))).status, 403);
        assert.deepEqual(h.calls, []);
    }
});

test("invalid and oversized browser requests never rotate or contact Core", async () => {
    for (const body of ["{", "x".repeat(4097), { action: "approve", userCode: "code", accountId: "chosen" }]) {
        const h = await routeHarness();
        assert.ok([400, 413].includes((await h.POST(h.request(body))).status));
        assert.deepEqual(h.calls, []);
    }
});

test("valid preview returns only the fixed display shape; browser Authorization is ignored", async () => {
    const h = await routeHarness();
    const response = await h.POST(h.request({ action: "preview", userCode: "ABCD" }, { authorization: "Bearer attacker" }));
    assert.equal(response.status, 200);
    assert.equal(response.headers.get("cache-control"), "no-store");
    assert.equal(h.calls[0].token, "browser-ticket");
    const body = await response.json();
    assert.equal(body.account, "Owner");
    assert.equal(body.accessToken, undefined);
});

test("expired cookie rotates once before the decision and preserves rotated cookies", async () => {
    const h = await routeHarness({ upstream: new Response(null, { status: 204 }) });
    const response = await h.POST(h.request({ action: "approve", userCode: "ABCD", confirmation: "shown" }, { cookie: "refresh=browser-refresh" }));
    assert.equal(response.status, 204);
    assert.equal(await response.text(), "");
    assert.equal(h.calls[0], "rotate");
    assert.equal(h.calls[1].token, "rotated-browser-ticket");
    assert.equal(h.calls[2], "setCookies");
});

test("uncertain decisions and rejected sessions never replay the authenticated mutation", async () => {
    for (const options of [{ throwUpstream: true }, { upstream: new Response("sensitive-error-description", { status: 400 }) },
        { upstream: new Response(null, { status: 401 }) }]) {
        const h = await routeHarness(options);
        const response = await h.POST(h.request({ action: "approve", userCode: "ABCD", confirmation: "shown" }));
        assert.ok([400, 401, 503].includes(response.status));
        assert.equal(h.calls.length, 1);
        assert.doesNotMatch(await response.text(), /sensitive-error-description|browser-ticket|browser-refresh/);
    }
});

test("dedicated server adapter fixes destination and trust headers without forwarding browser state", async () => {
    const calls = [];
    const adapter = await loadTs("lib/mcp-consent.ts", {
        "server-only": {},
        "@/lib/oidc": { getOidcConfig: () => ({ issuer: "https://provider.example", redirectUri: `${origin}/api/auth/oidc/callback`, bffServiceKey: "different-oidc-key" }) },
        "@/lib/browser-origin-security": { trustedBrowserOrigin: value => value },
        "@/lib/mcp-managed-config.mjs": { getManagedMcpConfig: () => null },
    }, {
        process: { env: { MCP_DEVICE_ENABLED: "true", PUBLIC_ORIGIN: origin, MCP_ISSUER: `${origin}/`, MCP_BFF_SERVICE_KEY: "independent-mcp-fixture-service-key", MCP_CORE_ORIGIN: "https://internal.example/" } },
        fetch: async (...args) => { calls.push(args); return new Response(null, { status: 204 }); },
    });
    await adapter.sendMcpConsent({ action: "preview", userCode: "ABCD" }, "browser-ticket");
    assert.equal(calls[0][0].href, "https://internal.example/connect/verify");
    const init = calls[0][1];
    assert.equal(init.headers.Host, "issuer.example");
    assert.equal(init.headers["X-GameHub-Mcp-Bff-Key"], "independent-mcp-fixture-service-key");
    assert.equal(init.redirect, "error");
    assert.equal(init.cache, "no-store");
    assert.equal(init.headers.Origin, undefined);
    assert.equal(init.headers.Cookie, undefined);
});
