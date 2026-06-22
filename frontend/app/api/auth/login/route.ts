import { NextRequest, NextResponse } from "next/server";
import {
    CoreAuthError,
    CoreOidcLinkTokenResponse,
    CoreTokenResponse,
    getCoreApiUrl,
    isFrontendAuthEnabled,
    isOidcLinkTokenResponse,
    isTokenResponse,
    setAuthCookies,
    setOidcLinkCookie,
} from "@/lib/auth-session";
import { validateFrontendMultiUserConfiguration } from "@/lib/multi-user";

/** Browser-facing login endpoint. Core verifies the password; Next.js owns the cookies. */
export async function POST(request: NextRequest): Promise<NextResponse> {
    try {
        validateFrontendMultiUserConfiguration();
    } catch {
        return NextResponse.json(
            { code: "authentication_unavailable", message: "Authentication is unavailable." },
            { status: 503, headers: { "Cache-Control": "no-store" } },
        );
    }
    if (!isFrontendAuthEnabled()) {
        return NextResponse.json(
            { code: "frontend_auth_disabled", message: "Frontend authentication is disabled." },
            { status: 503 },
        );
    }

    let credentials: { email?: unknown; password?: unknown };
    try {
        credentials = (await request.json()) as { email?: unknown; password?: unknown };
    } catch {
        return NextResponse.json(
            { code: "invalid_request", message: "Email and password are required." },
            { status: 400 },
        );
    }

    if (typeof credentials.email !== "string" || typeof credentials.password !== "string") {
        return NextResponse.json(
            { code: "invalid_request", message: "Email and password are required." },
            { status: 400 },
        );
    }

    let coreResponse: Response;
    try {
        coreResponse = await fetch(getCoreApiUrl("/auth/token"), {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                email: credentials.email,
                password: credentials.password,
            }),
            cache: "no-store",
        });
    } catch {
        return NextResponse.json(
            { code: "core_unavailable", message: "Authentication service is unavailable." },
            { status: 503 },
        );
    }

    const payload = (await coreResponse.json().catch(() => null)) as
        | CoreTokenResponse
        | CoreOidcLinkTokenResponse
        | CoreAuthError
        | null;
    if (!coreResponse.ok) {
        const error =
            payload && "code" in payload && "message" in payload
                ? payload
                : { code: "login_failed", message: "Sign in failed." };
        return NextResponse.json(error, { status: coreResponse.status });
    }
    if (isOidcLinkTokenResponse(payload)) {
        // Core has authenticated a provisioned account but has granted only the first explicit
        // issuer/subject link. The browser receives no token JSON; it is sent directly into the
        // server-side OIDC flow with a short-lived HttpOnly access cookie.
        const response = NextResponse.json({ authenticated: false, oidcLinkRequired: true });
        setOidcLinkCookie(response, payload);
        return response;
    }
    if (!isTokenResponse(payload)) {
        return NextResponse.json(
            { code: "invalid_token_response", message: "Authentication service returned invalid data." },
            { status: 502 },
        );
    }

    // The JSON sent back to browser code contains no token. Set-Cookie headers are processed
    // by the browser, while HttpOnly keeps both values invisible to React and other scripts.
    const response = NextResponse.json({ authenticated: true });
    setAuthCookies(response, payload);
    return response;
}
