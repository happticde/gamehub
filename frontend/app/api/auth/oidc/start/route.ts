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

/** Starts a provider-neutral Authorization Code + PKCE transaction. */
export async function GET(request: NextRequest): Promise<NextResponse> {
    try {
        const config = getOidcConfig();
        const discovery = await getOidcDiscovery(config);
        const intent: OidcIntent = request.nextUrl.searchParams.get("intent") === "link"
            ? "link"
            : "login";
        if (intent === "link" && !request.cookies.get(ACCESS_TOKEN_COOKIE)?.value) {
            return NextResponse.json(
                { code: "authentication_required", message: "Sign in before linking an identity." },
                { status: 401 },
            );
        }

        const transaction = createOidcTransaction(
            config.redirectUri,
            safeReturnPath(request.nextUrl.searchParams.get("returnTo")),
            intent,
        );
        const authorizationUrl = new URL(discovery.authorization_endpoint);
        authorizationUrl.searchParams.set("response_type", "code");
        authorizationUrl.searchParams.set("client_id", config.clientId);
        authorizationUrl.searchParams.set("redirect_uri", config.redirectUri);
        authorizationUrl.searchParams.set("scope", config.scopes);
        authorizationUrl.searchParams.set("state", transaction.state);
        authorizationUrl.searchParams.set("nonce", transaction.nonce);
        authorizationUrl.searchParams.set("code_challenge", transaction.codeChallenge);
        authorizationUrl.searchParams.set("code_challenge_method", "S256");

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
        return NextResponse.json(
            { code: "oidc_unavailable", message: "External authentication is unavailable." },
            { status: 503, headers: { "Cache-Control": "no-store" } },
        );
    }
}
