import assert from "node:assert/strict";
import { registerHooks } from "node:module";
import { fileURLToPath } from "node:url";
import { readFileSync } from "node:fs";
import test from "node:test";
import ts from "typescript";

// Exercise actual route handlers under Node without weakening Next's server-only build boundary.
registerHooks({
    resolve(specifier, context, nextResolve) {
        if (specifier === "server-only") return { url: "data:text/javascript,export%20%7B%7D", shortCircuit: true };
        if (specifier === "next/server") specifier = "next/server.js";
        if (specifier.startsWith("@/")) specifier = fileURLToPath(new URL(`../${specifier.slice(2)}.ts`, import.meta.url));
        return nextResolve(specifier, context);
    },
    load(url, context, nextLoad) {
        if (url.startsWith(new URL("../", import.meta.url).href) && url.endsWith(".ts")) {
            const source = ts.transpileModule(readFileSync(new URL(url), "utf8"), {
                compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
            }).outputText;
            return { format: "module", source, shortCircuit: true };
        }
        return nextLoad(url, context);
    },
});

const { NextRequest } = await import("next/server.js");
const core = await import("../app/api/core/[...path]/route.ts");
const browserPosts = await Promise.all([
    import("../app/api/auth/login/route.ts"),
    import("../app/api/auth/logout/route.ts"),
    import("../app/api/platform-credentials/epic/connect/start/route.ts"),
    import("../app/api/platform-credentials/epic/connect/complete/route.ts"),
]);

test("real browser routes reject sibling-origin writes before using cookies or fetching upstream", async () => {
    const previousOrigin = process.env.PUBLIC_ORIGIN;
    const previousAuth = process.env.AUTH_ENABLED;
    const previousFetch = globalThis.fetch;
    process.env.PUBLIC_ORIGIN = "https://gamehub.example";
    process.env.AUTH_ENABLED = "true";
    let calls = 0;
    globalThis.fetch = async () => { calls++; return new Response(null, { status: 204 }); };
    try {
        for (const route of [core, ...browserPosts]) {
            const request = new NextRequest("https://gamehub.example/api/core/auth/revoke-all", {
                method: "POST", headers: { origin: "https://untrusted.gamehub.example", "sec-fetch-site": "same-site", cookie: "gamehub_access_token=synthetic-token" }, body: "",
            });
            const response = await route.POST(request, { params: Promise.resolve({ path: ["auth", "revoke-all"] }) });
            assert.equal(response.status, 403);
            assert.equal(response.headers.get("set-cookie"), null);
        }
        assert.equal(calls, 0);
        const trusted = new NextRequest("https://gamehub.example/api/core/auth/revoke-all", {
            method: "POST", headers: { origin: "https://gamehub.example", "sec-fetch-site": "same-origin", cookie: "gamehub_access_token=synthetic-token" }, body: "",
        });
        const response = await core.POST(trusted, { params: Promise.resolve({ path: ["auth", "revoke-all"] }) });
        assert.equal(response.status, 204);
        assert.equal(calls, 1);
    } finally {
        if (previousOrigin === undefined) delete process.env.PUBLIC_ORIGIN; else process.env.PUBLIC_ORIGIN = previousOrigin;
        if (previousAuth === undefined) delete process.env.AUTH_ENABLED; else process.env.AUTH_ENABLED = previousAuth;
        globalThis.fetch = previousFetch;
    }
});
