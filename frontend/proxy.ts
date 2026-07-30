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
    validateAccessTokenWithCore,
} from "@/lib/auth-session";
import { getExternalRequestUrl } from "@/lib/external-request-url";
import {
    SESSION_ENDED_ROUTE,
    SESSION_VALIDATED_REQUEST_HEADER,
} from "@/lib/session-ended";

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
            : continueRequest(request, false);
    }

    const accessToken = request.cookies.get(ACCESS_TOKEN_COOKIE)?.value;
    const refreshToken = request.cookies.get(REFRESH_TOKEN_COOKIE)?.value;

    if (!isLoginPage && !accessToken && !refreshToken) {
        const loginUrl = new URL("/login", externalRequestUrl);
        loginUrl.searchParams.set("returnTo", `${request.nextUrl.pathname}${request.nextUrl.search}`);
        const response = NextResponse.redirect(loginUrl);
        clearAuthCookies(response);
        return response;
    }

    if (accessToken && isAccessTokenFresh(accessToken)) {
        const validation = await validateAccessTokenWithCore(accessToken);
        if (validation === "valid") {
            return isLoginPage
                ? NextResponse.redirect(new URL(
                    safeReturnPath(request.nextUrl.searchParams.get("returnTo")),
                    externalRequestUrl,
                ))
                : continueRequest(request, true);
        }
        if (validation === "unavailable") {
            // A network error or any non-401 Core result is inconclusive. Preserve the cookies and
            // let the requested page retain its existing safe infrastructure-error behavior.
            return continueRequest(request, false);
        }
    }

    const rotation = await rotateRefreshToken(refreshToken);
    if (rotation.kind === "rotated") {
        if (isLoginPage) {
            const response = NextResponse.redirect(new URL(
                safeReturnPath(request.nextUrl.searchParams.get("returnTo")),
                externalRequestUrl,
            ));
            setAuthCookies(response, rotation.tokens);
            return response;
        }

        return continueWithRotatedSession(request, rotation.tokens);
    }

    if (rotation.kind === "unavailable") {
        // Refresh infrastructure did not prove that the credentials were revoked. Keeping the
        // request unmarked prevents a downstream 401 from being misclassified as confirmed.
        return continueRequest(request, false);
    }

    if (isLoginPage) {
        const response = continueRequest(request, false);
        clearAuthCookies(response);
        return response;
    }

    return NextResponse.redirect(new URL(SESSION_ENDED_ROUTE, externalRequestUrl));
}

function continueWithRotatedSession(
    request: NextRequest,
    tokens: Parameters<typeof setAuthCookies>[1],
): NextResponse {
    // Response cookies update the browser for later requests. The copied Cookie header is equally
    // important: it lets this same request's Server Components use the new pair immediately.
    const requestHeaders = createForwardedRequestHeaders(request, true);
    const existingCookies = request.cookies
        .getAll()
        .filter(cookie =>
            cookie.name !== ACCESS_TOKEN_COOKIE && cookie.name !== REFRESH_TOKEN_COOKIE,
        )
        .map(cookie => `${cookie.name}=${cookie.value}`);
    existingCookies.push(`${ACCESS_TOKEN_COOKIE}=${tokens.accessToken}`);
    existingCookies.push(`${REFRESH_TOKEN_COOKIE}=${tokens.refreshToken}`);
    requestHeaders.set("cookie", existingCookies.join("; "));

    const response = NextResponse.next({ request: { headers: requestHeaders } });
    setAuthCookies(response, tokens);
    return response;
}

function continueRequest(request: NextRequest, validated: boolean): NextResponse {
    return NextResponse.next({
        request: { headers: createForwardedRequestHeaders(request, validated) },
    });
}

function createForwardedRequestHeaders(request: NextRequest, validated: boolean): Headers {
    const headers = new Headers(request.headers);
    // Never trust a browser-supplied copy of this internal middleware-to-RSC signal.
    headers.delete(SESSION_VALIDATED_REQUEST_HEADER);
    if (validated) headers.set(SESSION_VALIDATED_REQUEST_HEADER, "1");
    return headers;
}

export const config = {
    matcher: ["/((?!api|_next/static|_next/image|favicon.ico|.*\\..*).*)"],
};
