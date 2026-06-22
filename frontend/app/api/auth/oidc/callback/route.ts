import { NextRequest, NextResponse } from "next/server";
import {
    ACCESS_TOKEN_COOKIE,
    clearAuthCookies,
    CoreAuthError,
    CoreTokenResponse,
    getCoreApiUrl,
    isTokenResponse,
    setAuthCookies,
} from "@/lib/auth-session";
import { getOidcConfig, getOidcDiscovery } from "@/lib/oidc";
import {
    OIDC_TRANSACTION_COOKIE,
    openOidcTransaction,
    validateOidcCallback,
} from "@/lib/oidc-security";

export const runtime = "nodejs";

/**
 * Completes the provider callback, then asks Core to create or link a GameHub session.
 *
 * The route deliberately performs two validation layers:
 * 1. The BFF proves that this browser callback belongs to the transaction it started.
 * 2. Core independently proves the ID token and resolves its stable identity mapping.
 *
 * Neither layer substitutes for the other: protocol correlation belongs at the redirect-facing
 * BFF, while account/tenant authorization and durable identity ownership belong in Core.
 */
export async function GET(request: NextRequest): Promise<NextResponse> {
    let response: NextResponse;
    try {
        // Step 1: configuration must still be valid at callback time. This catches deployments
        // changed or partially restarted between the authorization redirect and the callback.
        const config = getOidcConfig();
        const sealedTransaction = request.cookies.get(OIDC_TRANSACTION_COOKIE)?.value;
        if (!sealedTransaction) throw new Error("missing_oidc_transaction");

        // Step 2: authenticated decryption recovers the server-generated state, nonce, PKCE
        // verifier, intent, redirect URI, and safe return path. Browser modifications fail here.
        const transaction = openOidcTransaction(sealedTransaction, config.cookieKey);
        const callbackUrl = new URL(request.url);
        // Query parameters contain the provider's code/state and are not part of the registered
        // redirect URI. Removing the complete query gives an exact scheme/host/path comparison.
        callbackUrl.search = "";
        validateOidcCallback(
            transaction,
            request.nextUrl.searchParams.get("state"),
            callbackUrl.toString(),
        );
        // Provider-declared errors and missing codes never proceed to token exchange.
        if (request.nextUrl.searchParams.has("error")) throw new Error("provider_rejected_login");
        const code = request.nextUrl.searchParams.get("code");
        if (!code) throw new Error("missing_authorization_code");

        // Step 3: exchange the short-lived, one-time authorization code from the server. Repeating
        // the exact redirect URI and supplying the secret verifier lets the provider enforce PKCE.
        const discovery = await getOidcDiscovery(config);
        const tokenBody = new URLSearchParams({
            grant_type: "authorization_code",
            code,
            redirect_uri: config.redirectUri,
            client_id: config.clientId,
            code_verifier: transaction.codeVerifier,
        });
        const headers = new Headers({
            Accept: "application/json",
            "Content-Type": "application/x-www-form-urlencoded",
        });
        if (config.clientSecret) {
            // Confidential clients authenticate with client_secret_basic. Public clients omit this
            // header but still use PKCE; the two mechanisms protect different trust assumptions.
            headers.set(
                "Authorization",
                `Basic ${Buffer.from(
                    `${encodeClientCredential(config.clientId)}:${encodeClientCredential(config.clientSecret)}`,
                    "utf8",
                ).toString("base64")}`,
            );
            tokenBody.delete("client_id");
        }
        const providerResponse = await fetch(discovery.token_endpoint, {
            method: "POST",
            headers,
            body: tokenBody,
            cache: "no-store",
            redirect: "error",
            signal: AbortSignal.timeout(10_000),
        });
        // The provider access token is intentionally ignored: GameHub needs the OIDC ID token to
        // authenticate the user, then issues its own tenant-scoped authorization/session tokens.
        const providerPayload = (await providerResponse.json().catch(() => null)) as
            | { id_token?: unknown }
            | null;
        if (!providerResponse.ok || typeof providerPayload?.id_token !== "string") {
            throw new Error("provider_token_exchange_failed");
        }

        // Step 4: delegate proof validation and mapping to Core. The independent service key makes
        // these endpoints BFF-only, so a direct caller cannot skip state/redirect/PKCE correlation.
        const corePath = transaction.intent === "link" ? "/auth/oidc/link" : "/auth/oidc/token";
        const coreHeaders = new Headers({ "Content-Type": "application/json" });
        coreHeaders.set("X-GameHub-Oidc-Bff-Key", config.bffServiceKey);
        if (transaction.intent === "link") {
            // Linking additionally sends the current GameHub JWT. Core takes account and tenant
            // only from this signed token—not from the provider token or callback parameters.
            const accessToken = request.cookies.get(ACCESS_TOKEN_COOKIE)?.value;
            if (!accessToken) throw new Error("link_session_missing");
            coreHeaders.set("Authorization", `Bearer ${accessToken}`);
        }
        const coreResponse = await fetch(getCoreApiUrl(corePath), {
            method: "POST",
            headers: coreHeaders,
            body: JSON.stringify({ idToken: providerPayload.id_token, nonce: transaction.nonce }),
            cache: "no-store",
            signal: AbortSignal.timeout(10_000),
        });
        if (transaction.intent === "link") {
            // A link operation changes only the durable mapping; the existing browser session
            // remains in place and no new tokens are exposed to browser JavaScript.
            if (!coreResponse.ok) throw new Error("identity_link_failed");
            // Linking never upgrades the restricted local bootstrap token into a full session.
            // Clear every GameHub cookie and require a fresh provider login, preserving the
            // original local destination only as a sanitized returnTo value.
            const destination = new URL("/login", request.url);
            destination.searchParams.set("oidcLinked", "true");
            destination.searchParams.set("returnTo", transaction.returnTo);
            response = NextResponse.redirect(destination);
            clearAuthCookies(response);
        } else {
            // A login operation receives GameHub's own access/refresh pair. Runtime shape checking
            // prevents malformed upstream JSON from reaching security-sensitive cookies.
            const payload = (await coreResponse.json().catch(() => null)) as
                | CoreTokenResponse
                | CoreAuthError
                | null;
            if (!coreResponse.ok || !isTokenResponse(payload)) {
                throw new Error("oidc_session_failed");
            }
            response = NextResponse.redirect(new URL(transaction.returnTo, request.url));
            // `setAuthCookies` writes HttpOnly cookies; React never sees either token value.
            setAuthCookies(response, payload);
        }
    } catch {
        // All callback failures converge on one public result. This avoids revealing whether a
        // subject is linked, which protocol check failed, or whether provider/Core was unavailable.
        const loginUrl = new URL("/login", request.url);
        loginUrl.searchParams.set("oidcError", "authentication_failed");
        response = NextResponse.redirect(loginUrl);
    }

    // A transaction is single-use regardless of success or failure. Expiring the correlation
    // cookie prevents a browser from replaying the same callback context.
    response.cookies.set(OIDC_TRANSACTION_COOKIE, "", {
        httpOnly: true,
        secure: true,
        sameSite: "lax",
        path: "/api/auth/oidc",
        expires: new Date(0),
    });
    response.headers.set("Cache-Control", "no-store");
    return response;
}

function encodeClientCredential(value: string): string {
    // OAuth client_secret_basic applies application/x-www-form-urlencoded encoding to each
    // credential component before joining them with ':' and Base64-encoding the result.
    return new URLSearchParams({ value }).toString().slice("value=".length);
}
