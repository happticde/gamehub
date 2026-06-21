import { NextRequest, NextResponse } from "next/server";
import {
    ACCESS_TOKEN_COOKIE,
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

/** Completes the code exchange and delegates ID-token validation/mapping to Core. */
export async function GET(request: NextRequest): Promise<NextResponse> {
    let response: NextResponse;
    try {
        const config = getOidcConfig();
        const sealedTransaction = request.cookies.get(OIDC_TRANSACTION_COOKIE)?.value;
        if (!sealedTransaction) throw new Error("missing_oidc_transaction");
        const transaction = openOidcTransaction(sealedTransaction, config.cookieKey);
        const callbackUrl = new URL(request.url);
        callbackUrl.search = "";
        validateOidcCallback(
            transaction,
            request.nextUrl.searchParams.get("state"),
            callbackUrl.toString(),
        );
        if (request.nextUrl.searchParams.has("error")) throw new Error("provider_rejected_login");
        const code = request.nextUrl.searchParams.get("code");
        if (!code) throw new Error("missing_authorization_code");

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
        const providerPayload = (await providerResponse.json().catch(() => null)) as
            | { id_token?: unknown }
            | null;
        if (!providerResponse.ok || typeof providerPayload?.id_token !== "string") {
            throw new Error("provider_token_exchange_failed");
        }

        const corePath = transaction.intent === "link" ? "/auth/oidc/link" : "/auth/oidc/token";
        const coreHeaders = new Headers({ "Content-Type": "application/json" });
        coreHeaders.set("X-GameHub-Oidc-Bff-Key", config.bffServiceKey);
        if (transaction.intent === "link") {
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
            if (!coreResponse.ok) throw new Error("identity_link_failed");
            const destination = new URL(transaction.returnTo, request.url);
            destination.searchParams.set("oidc", "linked");
            response = NextResponse.redirect(destination);
        } else {
            const payload = (await coreResponse.json().catch(() => null)) as
                | CoreTokenResponse
                | CoreAuthError
                | null;
            if (!coreResponse.ok || !isTokenResponse(payload)) {
                throw new Error("oidc_session_failed");
            }
            response = NextResponse.redirect(new URL(transaction.returnTo, request.url));
            setAuthCookies(response, payload);
        }
    } catch {
        const loginUrl = new URL("/login", request.url);
        loginUrl.searchParams.set("oidcError", "authentication_failed");
        response = NextResponse.redirect(loginUrl);
    }

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
    return new URLSearchParams({ value }).toString().slice("value=".length);
}
