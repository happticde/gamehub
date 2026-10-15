import { NextRequest, NextResponse } from "next/server";
import { rejectUnsafeBrowserRequest } from "@/lib/browser-request";
import {
    REFRESH_TOKEN_COOKIE,
    clearAuthCookies,
    getCoreApiUrl,
} from "@/lib/auth-session";
import { createOidcLogoutUrl, resolveLogoutLocation } from "@/lib/oidc-logout-security";
import { getOidcConfig, getOidcDiscovery } from "@/lib/oidc";

export const runtime = "nodejs";

/** Revokes locally, clears every GameHub auth cookie, then optionally ends provider SSO. */
export async function POST(request: NextRequest): Promise<NextResponse> {
    const rejection = rejectUnsafeBrowserRequest(request);
    if (rejection) return rejection;
    const refreshToken = request.cookies.get(REFRESH_TOKEN_COOKIE)?.value;
    const location = await resolveLogoutLocation(
        refreshToken,
        async (token) => {
            const response = await fetch(getCoreApiUrl("/auth/revoke"), {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ refreshToken: token }),
                cache: "no-store",
                signal: AbortSignal.timeout(10_000),
            });
            if (!response.ok) throw new Error("refresh_session_revocation_failed");
        },
        async () => {
            // Every value below is server-owned. No request body, query, header, or browser URL can
            // choose the provider endpoint or the post-logout destination.
            const config = getOidcConfig();
            const discovery = await getOidcDiscovery(config);
            return createOidcLogoutUrl({
                configuredIssuer: config.issuer,
                clientId: config.clientId,
                redirectUri: config.redirectUri,
                metadata: discovery,
            });
        },
    );

    // A native form submission plus 303 keeps both the provider hop and fallback as top-level
    // browser navigations. A relative fallback avoids trusting browser-controlled host headers.
    const response = new NextResponse(null, {
        status: 303,
        headers: {
            "Cache-Control": "no-store",
            Location: location,
        },
    });
    clearAuthCookies(response);
    return response;
}
