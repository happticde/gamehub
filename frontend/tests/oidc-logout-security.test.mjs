import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import {
    createOidcLogoutUrl,
    createPostLogoutRedirectUri,
    resolveLogoutLocation,
} from "../lib/oidc-logout-security.ts";

const configuredIssuer = "https://auth.example.test/realms/gamehub";
const redirectUri = "https://games.example.test/api/auth/oidc/callback";
const endSessionEndpoint = `${configuredIssuer}/protocol/openid-connect/logout`;
const readFrontendFile = (path) => readFile(new URL(`../${path}`, import.meta.url), "utf8");

test("post-logout URI is exactly /login on the configured callback origin", () => {
    assert.equal(
        createPostLogoutRedirectUri(`${redirectUri}?ignored=callback`),
        "https://games.example.test/login",
    );
});

test("logout URL uses discovered HTTPS endpoint, server client ID, and exact redirect", () => {
    const logoutUrl = new URL(createOidcLogoutUrl({
        configuredIssuer,
        clientId: "gamehub",
        redirectUri,
        metadata: {
            issuer: configuredIssuer,
            end_session_endpoint: `${endSessionEndpoint}?id_token_hint=forbidden&theme=gamehub`,
        },
    }));

    assert.equal(logoutUrl.origin + logoutUrl.pathname, endSessionEndpoint);
    assert.equal(logoutUrl.searchParams.get("client_id"), "gamehub");
    assert.equal(
        logoutUrl.searchParams.get("post_logout_redirect_uri"),
        "https://games.example.test/login",
    );
    assert.equal(logoutUrl.searchParams.has("id_token_hint"), false);
    assert.equal(logoutUrl.searchParams.get("theme"), "gamehub");
});

test("malformed, non-HTTPS, credentialed, and fragment logout endpoints are rejected", () => {
    for (const endpoint of [
        "not a URL",
        "http://auth.example.test/logout",
        "https://user:password@auth.example.test/logout",
        "https://auth.example.test/logout#fragment",
    ]) {
        assert.throws(
            () => createOidcLogoutUrl({
                configuredIssuer,
                clientId: "gamehub",
                redirectUri,
                metadata: { issuer: configuredIssuer, end_session_endpoint: endpoint },
            }),
            /invalid_end_session_endpoint/,
        );
    }
});

test("missing logout metadata returns a local-only result without weakening issuer pinning", () => {
    assert.equal(createOidcLogoutUrl({
        configuredIssuer,
        clientId: "gamehub",
        redirectUri,
        metadata: { issuer: configuredIssuer },
    }), null);

    assert.throws(
        () => createOidcLogoutUrl({
            configuredIssuer,
            clientId: "gamehub",
            redirectUri,
            metadata: { issuer: "https://attacker.example.test", end_session_endpoint: endSessionEndpoint },
        }),
        /oidc_issuer_mismatch/,
    );
});

test("local revocation precedes provider redirect and provider failure falls back safely", async () => {
    const events = [];
    const location = await resolveLogoutLocation(
        "gamehub-refresh-token",
        async () => events.push("revoke"),
        async () => {
            events.push("discover");
            throw new Error("provider unavailable");
        },
    );

    assert.deepEqual(events, ["revoke", "discover"]);
    assert.equal(location, "/login");
});

test("Core outage cannot prevent provider SSO termination", async () => {
    const location = await resolveLogoutLocation(
        "gamehub-refresh-token",
        async () => { throw new Error("core unavailable"); },
        async () => endSessionEndpoint,
    );

    assert.equal(location, endSessionEndpoint);
});

test("disabled OIDC, failed discovery, and missing logout metadata all remain local-only", async () => {
    const unavailableResolvers = [
        async () => { throw new Error("oidc_disabled"); },
        async () => { throw new Error("oidc_discovery_unavailable"); },
        async () => null,
    ];

    for (const resolveProvider of unavailableResolvers) {
        let revoked = false;
        const location = await resolveLogoutLocation(
            "gamehub-refresh-token",
            async () => { revoked = true; },
            resolveProvider,
        );
        assert.equal(revoked, true);
        assert.equal(location, "/login");
    }
});

test("request without refresh cookie cannot trigger a provider logout redirect", async () => {
    let callbackCount = 0;
    const location = await resolveLogoutLocation(
        undefined,
        async () => { callbackCount += 1; },
        async () => {
            callbackCount += 1;
            return endSessionEndpoint;
        },
    );

    assert.equal(location, "/login");
    assert.equal(callbackCount, 0);
});

test("logout is a same-origin POST form followed by a 303, never a provider fetch", async () => {
    const [component, route] = await Promise.all([
        readFrontendFile("components/LogoutButton.tsx"),
        readFrontendFile("app/api/auth/logout/route.ts"),
    ]);

    assert.match(component, /<form action="\/api\/auth\/logout" method="post"/);
    assert.doesNotMatch(component, /fetch\s*\(/);
    assert.match(route, /status:\s*303/);
    assert.match(route, /Location:\s*location/);
    assert.match(route, /clearAuthCookies\(response\)/);
    assert.match(route, /getCoreApiUrl\("\/auth\/revoke"\)/);
    assert.doesNotMatch(route, /request\.(?:nextUrl|url|headers)|request\.(?:json|formData)\s*\(/);
});

test("logout metadata remains optional for normal login and no provider token is retained", async () => {
    const [oidc, logoutRoute, logoutSecurity, component, authSession] = await Promise.all([
        readFrontendFile("lib/oidc.ts"),
        readFrontendFile("app/api/auth/logout/route.ts"),
        readFrontendFile("lib/oidc-logout-security.ts"),
        readFrontendFile("components/LogoutButton.tsx"),
        readFrontendFile("lib/auth-session.ts"),
    ]);

    assert.match(oidc, /end_session_endpoint\?: string/);
    assert.doesNotMatch(
        `${logoutRoute}\n${logoutSecurity}\n${component}`,
        /access_token|localStorage|provider.*refresh/i,
    );
    assert.match(authSession, /response\.cookies\.set\(ACCESS_TOKEN_COOKIE/);
    assert.match(authSession, /response\.cookies\.set\(REFRESH_TOKEN_COOKIE/);
    assert.match(authSession, /OIDC_TRANSACTION_COOKIE/);
    assert.match(authSession, /path:\s*"\/api\/auth\/oidc"/);
});
