import assert from "node:assert/strict";
import { readdir, readFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import {
    SESSION_ENDED_HEADER,
    SESSION_ENDED_HEADER_VALUE,
    createSingleNavigation,
    isConfirmedSessionEnded,
} from "../lib/session-ended.ts";

const readFrontendFile = (relativePath) =>
    readFile(new URL(`../${relativePath}`, import.meta.url), "utf8");

test("only a BFF-marked 401 confirms that the browser session ended", () => {
    const confirmed = new Response(null, {
        status: 401,
        headers: { [SESSION_ENDED_HEADER]: SESSION_ENDED_HEADER_VALUE },
    });
    assert.equal(isConfirmedSessionEnded(confirmed), true);

    for (const status of [200, 204, 403, 404, 429, 500, 502, 503]) {
        const response = new Response(null, {
            status,
            headers: { [SESSION_ENDED_HEADER]: SESSION_ENDED_HEADER_VALUE },
        });
        assert.equal(isConfirmedSessionEnded(response), false);
    }

    assert.equal(isConfirmedSessionEnded(new Response(null, { status: 401 })), false);
});

test("parallel confirmed 401 responses start only one cleanup navigation", () => {
    let navigations = 0;
    const navigate = createSingleNavigation(() => { navigations += 1; });

    navigate();
    navigate();
    navigate();

    assert.equal(navigations, 1);
});

test("session cleanup is idempotent, fixed-destination, local-only, and clears every auth cookie", async () => {
    const [route, authSession, sessionEnded] = await Promise.all([
        readFrontendFile("app/api/auth/session-ended/route.ts"),
        readFrontendFile("lib/auth-session.ts"),
        readFrontendFile("lib/session-ended.ts"),
    ]);

    assert.match(route, /export function GET\(\)/);
    assert.match(route, /status:\s*303/);
    assert.match(route, /Location:\s*SESSION_ENDED_LOGIN_PATH/);
    assert.match(route, /clearAuthCookies\(response\)/);
    assert.doesNotMatch(route, /\brequest\.|searchParams|returnTo|fetch\s*\(|getOidc/);
    assert.match(sessionEnded, /SESSION_ENDED_LOGIN_PATH\s*=\s*"\/login\?sessionEnded=true"/);

    assert.match(authSession, /response\.cookies\.set\(ACCESS_TOKEN_COOKIE/);
    assert.match(authSession, /response\.cookies\.set\(REFRESH_TOKEN_COOKIE/);
    assert.match(authSession, /response\.cookies\.set\(OIDC_TRANSACTION_COOKIE/);
});

test("page navigation live-validates with Core and refreshes before ending the session", async () => {
    const [proxy, coreApi, homePage, libraryPage, detailPage] = await Promise.all([
        readFrontendFile("proxy.ts"),
        readFrontendFile("lib/core-api.ts"),
        readFrontendFile("app/page.tsx"),
        readFrontendFile("app/library/page.tsx"),
        readFrontendFile("app/library/[id]/page.tsx"),
    ]);

    assert.match(proxy, /validateAccessTokenWithCore\(accessToken\)/);
    assert.match(proxy, /rotateRefreshToken\(refreshToken\)/);
    assert.match(proxy, /rotation\.kind === "unavailable"/);
    assert.match(proxy, /SESSION_ENDED_ROUTE/);
    assert.match(proxy, /headers\.delete\(SESSION_VALIDATED_REQUEST_HEADER\)/);

    assert.match(coreApi, /response\.status === 401/);
    assert.match(coreApi, /SESSION_VALIDATED_REQUEST_HEADER/);
    assert.match(coreApi, /redirect\(SESSION_ENDED_ROUTE\)/);
    assert.match(
        await readFrontendFile("lib/auth-session.ts"),
        /response\.status === 401\) return "unauthorized"/,
    );
    for (const page of [homePage, libraryPage, detailPage]) {
        assert.match(page, /rethrowCoreApiNavigation\(/);
    }
});

test("Core BFF marks 401 only after the existing refresh path is exhausted", async () => {
    const [route, authSession] = await Promise.all([
        readFrontendFile("app/api/core/[...path]/route.ts"),
        readFrontendFile("lib/auth-session.ts"),
    ]);

    assert.match(route, /upstream\.status === 401 && refreshToken && !refreshAttempted/);
    assert.match(route, /rotation\.kind === "rotated"/);
    assert.match(route, /rotation\.kind === "unavailable"/);
    assert.match(route, /if \(upstream\.status === 401\)/);
    assert.match(route, /createSessionEndedApiResponse\(\)/);
    assert.match(route, /createAuthenticationUnavailableResponse\(rotation\.status\)/);

    assert.match(authSession, /response\.status === 401.*kind:\s*"rejected"/s);
    assert.match(authSession, /catch \{\s*return \{ kind: "unavailable", status: 503 \}/s);
    assert.match(authSession, /status === 403/);
    assert.match(authSession, /status === 404/);
    assert.match(authSession, /status === 429/);
});

test("every browser-side Core call uses the shared top-level navigation handler", async () => {
    const frontendRoot = path.resolve(new URL("..", import.meta.url).pathname);
    const sourceFiles = await collectSourceFiles(frontendRoot, ["app", "components", "hooks"]);
    const directCoreFetch = /\bfetch\s*\(\s*["'`]\/api\/core/;
    let authenticatedCallSites = 0;

    for (const filename of sourceFiles) {
        const source = await readFile(filename, "utf8");
        if (!source.includes('"use client"') || !source.includes("/api/core")) continue;

        assert.doesNotMatch(source, directCoreFetch, filename);
        assert.match(source, /fetchAuthenticated/, filename);
        authenticatedCallSites += 1;
    }

    assert.ok(authenticatedCallSites >= 15);

    const authenticatedFetch = await readFrontendFile("lib/authenticated-fetch.ts");
    assert.match(authenticatedFetch, /window\.location\.replace\(SESSION_ENDED_ROUTE\)/);
    assert.match(authenticatedFetch, /new Promise<Response>/);
    assert.doesNotMatch(authenticatedFetch, /localStorage|accessToken|refreshToken/);
});

test("login displays a neutral ended-session message and public auth routes stay outside interception", async () => {
    const [loginPage, loginForm, loginRoute, logoutRoute, proxy] = await Promise.all([
        readFrontendFile("app/login/page.tsx"),
        readFrontendFile("components/LoginForm.tsx"),
        readFrontendFile("app/api/auth/login/route.ts"),
        readFrontendFile("app/api/auth/logout/route.ts"),
        readFrontendFile("proxy.ts"),
    ]);

    assert.match(loginPage, /sessionEnded=\{params\.sessionEnded === "true"\}/);
    assert.match(
        loginForm,
        /Diese GameHub-Sitzung wurde beendet\. Bitte melde dich erneut an\./,
    );
    assert.doesNotMatch(`${loginRoute}\n${logoutRoute}`, /fetchAuthenticated/);
    assert.match(proxy, /const isLoginPage = request\.nextUrl\.pathname === "\/login"/);
    assert.match(proxy, /matcher:\s*\["\/\(\(\?!api/);
});

async function collectSourceFiles(root, directories) {
    const files = [];
    for (const directory of directories) {
        await walk(path.join(root, directory), files);
    }
    return files;
}

async function walk(directory, files) {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
        const absolutePath = path.join(directory, entry.name);
        if (entry.isDirectory()) {
            await walk(absolutePath, files);
        } else if (/\.(?:ts|tsx)$/.test(entry.name)) {
            files.push(absolutePath);
        }
    }
}
