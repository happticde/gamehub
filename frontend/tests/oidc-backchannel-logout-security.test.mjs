import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const routePath = new URL(
    "../app/api/auth/oidc/backchannel-logout/route.ts",
    import.meta.url,
);

test("back-channel route accepts only one bounded form logout token", async () => {
    const route = await readFile(routePath, "utf8");

    assert.match(route, /application\/x-www-form-urlencoded/);
    assert.match(route, /request\.formData\(\)\)\.getAll\("logout_token"\)/);
    assert.match(route, /submittedTokens\.length !== 1/);
    assert.match(route, /MAXIMUM_LOGOUT_TOKEN_LENGTH/);
    assert.doesNotMatch(route, /request\.json\s*\(/);
});

test("back-channel route delegates to Core through the server-only BFF trust channel", async () => {
    const route = await readFile(routePath, "utf8");

    assert.match(route, /getCoreApiUrl\("\/auth\/oidc\/backchannel-logout"\)/);
    assert.match(route, /"X-GameHub-Oidc-Bff-Key": config\.bffServiceKey/);
    assert.match(route, /JSON\.stringify\(\{ logoutToken: submittedTokens\[0\] \}\)/);
    assert.match(route, /cache: "no-store"/);
    assert.match(route, /redirect: "error"/);
    assert.doesNotMatch(route, /cookies|getOidcDiscovery|localStorage|subject|tenantId/i);
});

test("provider responses remain bodyless and non-enumerating", async () => {
    const route = await readFile(routePath, "utf8");

    assert.match(route, /new NextResponse\(null/);
    assert.match(route, /coreResponse\.ok \? 200/);
    assert.match(route, /coreResponse\.status === 400 \? 400 : 503/);
    assert.match(route, /"Cache-Control": "no-store"/);
});
