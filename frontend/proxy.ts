import { NextRequest, NextResponse } from "next/server";
import {
    ACCESS_TOKEN_COOKIE,
    REFRESH_TOKEN_COOKIE,
    clearAuthCookies,
    isAccessTokenFresh,
    isFrontendAuthEnabled,
    rotateRefreshToken,
    safeReturnPath,
    setAuthCookies,
} from "@/lib/auth-session";
import { getExternalRequestUrl } from "@/lib/external-request-url";

/**
 * Protects rendered pages before React Server Components run. When the short-lived access JWT
 * expires, the proxy rotates the HttpOnly refresh token and forwards fresh cookies into the
 * current request, so server-rendered Core fetches can succeed without a visible login flash.
 */
export async function proxy(request: NextRequest): Promise<NextResponse> {
    const isLoginPage = request.nextUrl.pathname === "/login";
    const externalRequestUrl = getExternalRequestUrl(request);

    // JWT remains opt-in across GameHub. In legacy mode Core accepts requests without an
    // authenticated principal, so the frontend must not redirect installations to a login
    // form whose token endpoint is intentionally disabled.
    if (!isFrontendAuthEnabled()) {
        return isLoginPage
            ? NextResponse.redirect(new URL("/library", externalRequestUrl))
            : NextResponse.next();
    }

    const accessToken = request.cookies.get(ACCESS_TOKEN_COOKIE)?.value;

    if (isAccessTokenFresh(accessToken)) {
        return isLoginPage
            ? NextResponse.redirect(new URL(
                safeReturnPath(request.nextUrl.searchParams.get("returnTo")),
                externalRequestUrl,
            ))
            : NextResponse.next();
    }

    const refreshToken = request.cookies.get(REFRESH_TOKEN_COOKIE)?.value;
    const rotatedTokens = await rotateRefreshToken(refreshToken);
    if (rotatedTokens) {
        if (isLoginPage) {
            const response = NextResponse.redirect(new URL(
                safeReturnPath(request.nextUrl.searchParams.get("returnTo")),
                externalRequestUrl,
            ));
            setAuthCookies(response, rotatedTokens);
            return response;
        }

        // Response cookies update the browser for later requests. The copied Cookie header is
        // equally important: it lets this same request's Server Components see the new access
        // token immediately instead of waiting for another navigation.
        const requestHeaders = new Headers(request.headers);
        const existingCookies = request.cookies
            .getAll()
            .filter(cookie =>
                cookie.name !== ACCESS_TOKEN_COOKIE && cookie.name !== REFRESH_TOKEN_COOKIE,
            )
            .map(cookie => `${cookie.name}=${cookie.value}`);
        existingCookies.push(`${ACCESS_TOKEN_COOKIE}=${rotatedTokens.accessToken}`);
        existingCookies.push(`${REFRESH_TOKEN_COOKIE}=${rotatedTokens.refreshToken}`);
        requestHeaders.set("cookie", existingCookies.join("; "));
        const response = NextResponse.next({ request: { headers: requestHeaders } });
        setAuthCookies(response, rotatedTokens);
        return response;
    }

    if (isLoginPage) {
        const response = NextResponse.next();
        clearAuthCookies(response);
        return response;
    }

    const loginUrl = new URL("/login", externalRequestUrl);
    loginUrl.searchParams.set("returnTo", `${request.nextUrl.pathname}${request.nextUrl.search}`);
    const response = NextResponse.redirect(loginUrl);
    clearAuthCookies(response);
    return response;
}

export const config = {
    matcher: ["/((?!api|_next/static|_next/image|favicon.ico|.*\\..*).*)"],
};
