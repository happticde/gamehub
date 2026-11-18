import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, chmodSync, writeFileSync, rmSync, symlinkSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createServer } from "node:http";
import { once } from "node:events";
import { runInNewContext } from "node:vm";
import test from "node:test";
import ts from "typescript";
import { getManagedMcpConfig, readManagedMcpState, managedMcpHeaders } from "../lib/mcp-managed-config.mjs";

const origin = "https://games.example";
const state = { Version: 1, Enabled: true, PublicOrigin: origin, Generation: "a".repeat(32),
    TransportKey: "transport-fixture-".repeat(4), ConsentKey: "consent-fixture-".repeat(4) };
function fixture() {
    const root = mkdtempSync(join(tmpdir(), "gamehub-managed-test-"));
    const directory = join(root, "private");
    mkdirSync(directory, { mode: 0o700 });
    const file = join(directory, "state.json");
    writeFileSync(file, JSON.stringify(state), { mode: 0o600 });
    const env = { MCP_MANAGED_ENABLED: "true", MCP_MANAGED_DIRECTORY: directory, AUTH_ENABLED: "true",
        AUTH_COOKIE_SECURE: "true", OIDC_ENABLED: "true", PUBLIC_ORIGIN: origin,
        OIDC_CLIENT_ID: "browser-fixture", OIDC_COOKIE_KEY: Buffer.alloc(32, 1).toString("base64"),
        OIDC_BFF_SERVICE_KEY: "provider-fixture-independent-".repeat(3),
        OIDC_REDIRECT_URI: `${origin}/api/auth/oidc/callback`, OIDC_ISSUER: "https://provider.example", CORE_URL: "http://core:8080" };
    return { directory, file, env, dispose: () => rmSync(root, { recursive: true, force: true }) };
}
test("managed loader refuses unsafe/private-file faults and conflicting trust without leaking values", () => {
    const f = fixture();
    try {
        assert.equal(getManagedMcpConfig(f.env).instance, origin);
        for (const change of [{ MCP_DEVICE_ENABLED: "true" }, { PUBLIC_ORIGIN: "https://wrong.example" },
            { AUTH_COOKIE_SECURE: "false" }, { CORE_URL: "http://core:8080/path" }, { OIDC_BFF_SERVICE_KEY: state.ConsentKey }]) {
            assert.throws(() => getManagedMcpConfig({ ...f.env, ...change }));
        }
        chmodSync(f.file, 0o644);
        assert.throws(() => readManagedMcpState(f.directory), /^Error: managed_storage_invalid$/);
        chmodSync(f.file, 0o600);
        rmSync(f.file);
        symlinkSync("/does-not-exist", f.file);
        assert.throws(() => readManagedMcpState(f.directory), /^Error: managed_storage_invalid$/);
    } finally { f.dispose(); }
});

function loadTs(path, modules) {
    const exports = {};
    const source = readFileSync(new URL(`../${path}`, import.meta.url), "utf8");
    const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText;
    runInNewContext(code, { exports, require: name => {
        if (!(name in modules)) throw new Error(`Unexpected import ${name}`);
        return modules[name];
    }, URL, URLSearchParams, Headers, Request, Response, AbortSignal, fetch });
    return exports;
}

test("real native Node request uses internal Host and generated proof; route/header/redirect boundaries hold", async () => {
    const seen = [];
    const server = createServer(async (request, response) => {
        const chunks = [];
        for await (const chunk of request) chunks.push(chunk);
        seen.push({ headers: request.headers, path: request.url, body: Buffer.concat(chunks).toString() });
        if (request.url === "/connect/revoke") { response.writeHead(302, { location: "https://attacker.example" }).end(); return; }
        response.writeHead(400, { "content-type": "application/json", "set-cookie": "forbidden", "x-secret": "forbidden" });
        response.end('{"error":"authorization_pending"}');
    });
    server.listen(0, "127.0.0.1");
    await once(server, "listening");
    const port = server.address().port;
    const config = { instance: origin, core: new URL(`http://127.0.0.1:${port}`), key: state.ConsentKey,
        generation: state.Generation, transportKey: state.TransportKey };
    const modules = { "server-only": {}, "@/lib/mcp-managed-config.mjs": { getManagedMcpConfig: () => config, managedMcpHeaders } };
    const gateway = loadTs("lib/mcp-gateway.ts", modules);
    try {
        const request = (path, method = "POST", headers = {}) => new Request(origin + path, {
            method, headers: { host: "games.example", cookie: "browser=private", authorization: "Bearer browser-proof",
                "x-api-key": "operator-proof", "x-gamehub-tenant-id": "chosen", "x-forwarded-host": "evil.example",
                "x-gamehub-mcp-bff-key": "attacker", "content-type": "application/x-www-form-urlencoded", ...headers },
            ...(method === "GET" ? {} : { body: "fixture=value" }),
        });
        const result = await gateway.forwardMcpRequest(request("/connect/token"));
        assert.equal(result.status, 400);
        assert.equal(await result.text(), '{"error":"authorization_pending"}');
        assert.equal(result.headers.get("set-cookie"), null);
        assert.equal(result.headers.get("x-secret"), null);
        assert.equal(seen[0].headers.host, `127.0.0.1:${port}`);
        assert.equal(seen[0].headers["x-gamehub-mcp-transport-key"], state.TransportKey);
        for (const name of ["cookie", "authorization", "x-api-key", "x-gamehub-tenant-id", "x-forwarded-host", "x-gamehub-mcp-bff-key"]) {
            assert.equal(seen[0].headers[name], undefined);
        }
        assert.equal((await gateway.forwardMcpRequest(request("/connect/revoke"))).status, 502);
        assert.equal(seen.length, 2);
        for (const path of ["/connect/verify", "/connect/verify/", "/connect/authorize/consent", "/connect/register", "/api/library/1/delete"]) {
            assert.equal((await gateway.forwardMcpRequest(request(path))).status, 404);
        }
        assert.equal((await gateway.forwardMcpRequest(request("/connect/device", "POST", { host: "evil.example" }))).status, 403);
        assert.equal((await gateway.forwardMcpRequest(request("/connect/device", "POST", { origin: "https://evil.example" }))).status, 403);
        assert.equal(seen.length, 2);
        await gateway.forwardMcpRequest(request("/api/library?search=fixture", "GET"));
        assert.equal(seen[2].headers.authorization, "Bearer browser-proof");
        const consent = loadTs("lib/mcp-consent.ts", { ...modules,
            "@/lib/oidc": { getOidcConfig: () => ({}) }, "@/lib/browser-origin-security": {} });
        assert.equal(consent.getMcpConsentConfig().destination.href, `http://127.0.0.1:${port}/connect/verify`);
        await consent.sendMcpConsent({ action: "preview", userCode: "TEST" }, "signed-owner-fixture");
        assert.equal(seen[3].headers.host, `127.0.0.1:${port}`);
        assert.equal(seen[3].headers.authorization, "Bearer signed-owner-fixture");
        assert.equal(seen[3].headers["x-gamehub-mcp-bff-key"], state.ConsentKey);
    } finally { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
});

test("private HTTP transport forwards bounded protocol headers and cancels an upstream event stream", async () => {
    let upstreamHeaders;
    let upstreamPath;
    let reportClosed;
    const upstreamClosed = new Promise(resolve => { reportClosed = resolve; });
    const server = createServer(async (request, response) => {
        upstreamHeaders = request.headers;
        upstreamPath = request.url;
        for await (const chunk of request) assert.ok(chunk.length > 0);
        response.on("close", reportClosed);
        response.writeHead(200, { "content-type": "text/event-stream", "set-cookie": "forbidden" });
        response.write("data: first\n\n");
    });
    server.listen(0, "127.0.0.1");
    await once(server, "listening");
    const config = { instance: origin, core: new URL(`http://127.0.0.1:${server.address().port}`),
        generation: state.Generation, transportKey: state.TransportKey };
    const modules = { "server-only": {}, "@/lib/mcp-managed-config.mjs": { getManagedMcpConfig: () => config, managedMcpHeaders } };
    const { forwardMcpRequest } = loadTs("lib/mcp-gateway.ts", modules);
    const abort = new AbortController();
    const request = (path = "/mcp", headers = {}) => new Request(origin + path, {
        method: "POST", body: "{}", signal: abort.signal, headers: {
            host: "games.example", origin, authorization: "Bearer assistant-fixture",
            "content-type": "application/json", accept: "application/json, text/event-stream",
            "mcp-protocol-version": "2026-07-28", "mcp-method": "tools/call", "mcp-name": "search_library",
            "mcp-session-id": "untrusted", "x-gamehub-mcp-transport-key": "untrusted",
            "x-gamehub-tenant-id": "untrusted", "x-gamehub-mcp-bff-key": "untrusted",
            "x-forwarded-host": "untrusted", cookie: "browser=private", ...headers,
        },
    });
    try {
        assert.equal((await forwardMcpRequest(request("/mcp", { host: "other.example" }))).status, 403);
        assert.equal((await forwardMcpRequest(request("/mcp", { origin: "https://other.example" }))).status, 403);
        assert.equal((await forwardMcpRequest(request("/mcp?extra=1"))).status, 400);
        assert.equal((await forwardMcpRequest(request("/mcp/sse"))).status, 404);
        assert.equal(upstreamPath, undefined);
        const response = await forwardMcpRequest(request());
        assert.equal(response.status, 200);
        assert.equal(response.headers.get("set-cookie"), null);
        assert.equal(upstreamPath, "/mcp");
        assert.equal(upstreamHeaders.origin, origin);
        assert.equal(upstreamHeaders.authorization, "Bearer assistant-fixture");
        assert.equal(upstreamHeaders["mcp-protocol-version"], "2026-07-28");
        assert.equal(upstreamHeaders["mcp-method"], "tools/call");
        assert.equal(upstreamHeaders["mcp-name"], "search_library");
        assert.equal(upstreamHeaders["x-gamehub-mcp-transport-key"], state.TransportKey);
        for (const name of ["mcp-session-id", "x-gamehub-tenant-id", "x-gamehub-mcp-bff-key", "x-forwarded-host", "cookie"]) {
            assert.equal(upstreamHeaders[name], undefined);
        }
        const reader = response.body.getReader();
        assert.equal(new TextDecoder().decode((await reader.read()).value), "data: first\n\n");
        abort.abort();
        await assert.rejects(reader.read(), { name: "AbortError" });
        await Promise.race([upstreamClosed, once(AbortSignal.timeout(5_000), "abort").then(() => {
            throw new Error("The upstream stream remained open after the client disconnected.");
        })]);
    } finally {
        abort.abort();
        server.closeAllConnections();
        await new Promise(resolve => server.close(resolve));
    }
});
