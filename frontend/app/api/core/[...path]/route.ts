import { NextRequest, NextResponse } from "next/server";
import {
    ACCESS_TOKEN_COOKIE,
    REFRESH_TOKEN_COOKIE,
    clearAuthCookies,
    createAuthenticationUnavailableResponse,
    createSessionEndedApiResponse,
    getCoreApiUrl,
    isFrontendAuthEnabled,
    rotateRefreshToken,
    setAuthCookies,
    type CoreTokenResponse,
} from "@/lib/auth-session";

type CoreRouteContext = { params: Promise<{ path: string[] }> };

/**
 * BFF proxy for browser-side Core calls. The browser sends only same-origin HttpOnly cookies;
 * this server route translates the access cookie into the Authorization header Core expects.
 */
async function proxyCore(request: NextRequest, context: CoreRouteContext): Promise<NextResponse> {
    const { path } = await context.params;
    const corePath = `/${path.map(encodeURIComponent).join("/")}${request.nextUrl.search}`;
    const body = request.method === "GET" || request.method === "HEAD"
        ? undefined
        : await request.arrayBuffer();

    let accessToken = request.cookies.get(ACCESS_TOKEN_COOKIE)?.value;
    const refreshToken = request.cookies.get(REFRESH_TOKEN_COOKIE)?.value;
    let rotatedTokens: CoreTokenResponse | null = null;
    let refreshAttempted = false;

    // Preserve the project's deliberate auth-disabled compatibility mode. Core decides
    // whether an anonymous request is allowed; the BFF does not invent a tenant identity.
    if (!isFrontendAuthEnabled()) {
        return copyCoreResponse(await sendToCore(request, corePath, undefined, body));
    }

    // An expired access cookie normally disappears automatically. Rotate before the first
    // upstream request when only the longer-lived refresh cookie remains.
    if (!accessToken && refreshToken) {
        refreshAttempted = true;
        const rotation = await rotateRefreshToken(refreshToken);
        if (rotation.kind === "rotated") {
            rotatedTokens = rotation.tokens;
            accessToken = rotation.tokens.accessToken;
        } else if (rotation.kind === "unavailable") {
            return createAuthenticationUnavailableResponse(rotation.status);
        } else {
            return createSessionEndedApiResponse();
        }
    }

    if (!accessToken) {
        return createSessionEndedApiResponse();
    }

    let upstream: Response;
    try {
        upstream = await sendToCore(request, corePath, accessToken, body);
    } catch {
        const response = createAuthenticationUnavailableResponse(503);
        if (rotatedTokens) setAuthCookies(response, rotatedTokens);
        return response;
    }

    // A signature/configuration failure can occur before the cookie's advertised expiry.
    // Core remains authoritative: one rotation attempt is allowed, then the session is cleared.
    if (upstream.status === 401 && refreshToken && !refreshAttempted) {
        refreshAttempted = true;
        const rotation = await rotateRefreshToken(refreshToken);
        if (rotation.kind === "rotated") {
            rotatedTokens = rotation.tokens;
            accessToken = rotation.tokens.accessToken;
            try {
                upstream = await sendToCore(request, corePath, accessToken, body);
            } catch {
                const response = createAuthenticationUnavailableResponse(503);
                setAuthCookies(response, rotatedTokens);
                return response;
            }
        } else if (rotation.kind === "unavailable") {
            return createAuthenticationUnavailableResponse(rotation.status);
        } else {
            return createSessionEndedApiResponse();
        }
    }

    if (upstream.status === 401) {
        return createSessionEndedApiResponse();
    }

    const response = copyCoreResponse(upstream);
    const completedAccountLifecycleAction = upstream.ok && (
        corePath === "/auth/password" ||
        corePath === "/auth/revoke-all" ||
        corePath === "/auth/account"
    );
    if (completedAccountLifecycleAction) {
        // Core has invalidated every refresh session (or deleted the account). Remove both
        // cookies immediately so the browser cannot keep presenting the still-unexpired JWT.
        clearAuthCookies(response);
    } else if (rotatedTokens) {
        setAuthCookies(response, rotatedTokens);
    }
    return response;
}

async function sendToCore(
    request: NextRequest,
    path: string,
    accessToken: string | undefined,
    body: ArrayBuffer | undefined,
): Promise<Response> {
    const headers = new Headers();
    const contentType = request.headers.get("content-type");
    const accept = request.headers.get("accept");
    if (contentType) headers.set("Content-Type", contentType);
    if (accept) headers.set("Accept", accept);
    if (accessToken) headers.set("Authorization", `Bearer ${accessToken}`);

    return fetch(getCoreApiUrl(path), {
        method: request.method,
        headers,
        body,
        cache: "no-store",
        redirect: "manual",
    });
}

function copyCoreResponse(upstream: Response): NextResponse {
    const headers = new Headers();
    for (const name of ["content-type", "content-disposition", "cache-control"]) {
        const value = upstream.headers.get(name);
        if (value) headers.set(name, value);
    }
    return new NextResponse(upstream.status === 204 ? null : upstream.body, {
        status: upstream.status,
        headers,
    });
}

export const GET = proxyCore;
export const POST = proxyCore;
export const PUT = proxyCore;
export const PATCH = proxyCore;
export const DELETE = proxyCore;
