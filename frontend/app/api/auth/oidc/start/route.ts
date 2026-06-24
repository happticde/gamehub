import { NextRequest, NextResponse } from "next/server";
import { ACCESS_TOKEN_COOKIE, safeReturnPath } from "@/lib/auth-session";
import { getOidcConfig, getOidcDiscovery } from "@/lib/oidc";
import {
    OIDC_TRANSACTION_COOKIE,
    OIDC_TRANSACTION_MAX_AGE_SECONDS,
    OidcIntent,
    createOidcTransaction,
    sealOidcTransaction,
} from "@/lib/oidc-security";

export const runtime = "nodejs";

/**
 * Starts a provider-neutral Authorization Code + PKCE transaction.
 *
 * This is a BFF endpoint, not a browser-side OAuth client. React navigates here, but every
 * sensitive operation—configuration loading, random-value generation, discovery, transaction
 * encryption, and redirect construction—runs on the Next.js server.
 */
export async function GET(request: NextRequest): Promise<NextResponse> {
    try {
        // Step 1: load the complete fail-closed configuration and provider endpoints. No
        // authorization URL is constructed from browser-supplied provider information.
        const config = getOidcConfig();
        const discovery = await getOidcDiscovery(config);

        // Step 2: select the server-defined operation. Unknown intent values deliberately become
        // normal login; callers cannot invent another callback behavior.
        const requestedIntent = request.nextUrl.searchParams.get("intent");
        const intent: OidcIntent = requestedIntent === "link"
            || requestedIntent === "delete"
            || requestedIntent === "delete-retry"
            ? requestedIntent
            : "login";

        // Linking changes a durable identity mapping, so it starts only when a GameHub session
        // cookie exists. Core later validates that JWT and remains authoritative; this early check
        // merely avoids sending an unauthenticated user through the provider unnecessarily.
        if ((intent === "link" || intent === "delete")
            && !request.cookies.get(ACCESS_TOKEN_COOKIE)?.value) {
            return NextResponse.json(
                { code: "authentication_required", message: "A current GameHub session is required." },
                { status: 401 },
            );
        }

        // Step 3: create one-time correlation material. `safeReturnPath` accepts only local paths,
        // preventing the post-login redirect from becoming an open redirect.
        const transaction = createOidcTransaction(
            config.redirectUri,
            safeReturnPath(request.nextUrl.searchParams.get("returnTo")),
            intent,
        );
        // Step 4: build the standard authorization request. The provider sees the public challenge
        // but never the verifier. `nonce` will be echoed in the signed ID token; `state` will be
        // echoed in the browser callback.
        const authorizationUrl = new URL(discovery.authorization_endpoint);
        authorizationUrl.searchParams.set("response_type", "code");
        authorizationUrl.searchParams.set("client_id", config.clientId);
        authorizationUrl.searchParams.set("redirect_uri", config.redirectUri);
        authorizationUrl.searchParams.set("scope", config.scopes);
        authorizationUrl.searchParams.set("state", transaction.state);
        authorizationUrl.searchParams.set("nonce", transaction.nonce);
        authorizationUrl.searchParams.set("code_challenge", transaction.codeChallenge);
        authorizationUrl.searchParams.set("code_challenge_method", "S256");
        if (intent === "delete" || intent === "delete-retry") {
            // Force a new provider authentication ceremony. Core independently requires a recent
            // signed auth_time before it can issue the deletion-only GameHub token.
            authorizationUrl.searchParams.set("prompt", "login");
            authorizationUrl.searchParams.set("max_age", "0");
        }

        // Step 5: store the encrypted transaction in a narrow, short-lived browser cookie and
        // redirect. HttpOnly hides it from JavaScript, Secure restricts it to HTTPS, SameSite=Lax
        // permits the top-level provider callback, and the path exposes it only to OIDC routes.
        const response = NextResponse.redirect(authorizationUrl);
        response.cookies.set(
            OIDC_TRANSACTION_COOKIE,
            sealOidcTransaction(transaction, config.cookieKey),
            {
                httpOnly: true,
                secure: true,
                sameSite: "lax",
                path: "/api/auth/oidc",
                maxAge: OIDC_TRANSACTION_MAX_AGE_SECONDS,
            },
        );
        response.headers.set("Cache-Control", "no-store");
        return response;
    } catch {
        // Configuration and discovery failures are intentionally indistinguishable to the browser.
        // Detailed infrastructure errors stay in server diagnostics, while authentication closes.
        return NextResponse.json(
            { code: "oidc_unavailable", message: "External authentication is unavailable." },
            { status: 503, headers: { "Cache-Control": "no-store" } },
        );
    }
}
