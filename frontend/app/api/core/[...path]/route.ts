import { NextRequest, NextResponse } from "next/server";
import {
    ACCESS_TOKEN_COOKIE,
    REFRESH_TOKEN_COOKIE,
    CoreTokenResponse,
    clearAuthCookies,
    getCoreApiUrl,
    isFrontendAuthEnabled,
    rotateRefreshToken,
    setAuthCookies,
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

    // Preserve the project's deliberate auth-disabled compatibility mode. Core decides
    // whether an anonymous request is allowed; the BFF does not invent a tenant identity.
    if (!isFrontendAuthEnabled()) {
        return copyCoreResponse(await sendToCore(request, corePath, undefined, body));
    }

    // An expired access cookie normally disappears automatically. Rotate before the first
    // upstream request when only the longer-lived refresh cookie remains.
    if (!accessToken && refreshToken) {
        rotatedTokens = await rotateRefreshToken(refreshToken);
        accessToken = rotatedTokens?.accessToken;
    }

    if (!accessToken) {
        const response = NextResponse.json(
            { code: "authentication_required", message: "Sign in is required." },
            { status: 401 },
        );
        clearAuthCookies(response);
        return response;
    }

    let upstream = await sendToCore(request, corePath, accessToken, body);
    // A signature/configuration failure can occur before the cookie's advertised expiry.
    // Core remains authoritative: one rotation attempt is allowed, then the session is cleared.
    if (upstream.status === 401 && refreshToken && !rotatedTokens) {
        rotatedTokens = await rotateRefreshToken(refreshToken);
        if (rotatedTokens) {
            accessToken = rotatedTokens.accessToken;
            upstream = await sendToCore(request, corePath, accessToken, body);
        }
    }

    const response = copyCoreResponse(upstream);
    if (rotatedTokens) {
        setAuthCookies(response, rotatedTokens);
    } else if (upstream.status === 401) {
        clearAuthCookies(response);
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
