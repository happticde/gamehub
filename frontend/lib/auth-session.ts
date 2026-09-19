import "server-only";

import { NextResponse } from "next/server";
import { OIDC_TRANSACTION_COOKIE } from "@/lib/oidc-security";
export { safeReturnPath } from "@/lib/auth-return-path";
import {
    SESSION_ENDED_HEADER,
    SESSION_ENDED_HEADER_VALUE,
} from "@/lib/session-ended";
export {
    isAccountDeletionTokenResponse,
    type CoreAccountDeletionTokenResponse,
} from "@/lib/auth-token-security";

export const ACCESS_TOKEN_COOKIE = "gamehub_access_token";
export const REFRESH_TOKEN_COOKIE = "gamehub_refresh_token";

/** Keeps the existing auth-disabled local mode working until an operator opts into JWT login. */
export function isFrontendAuthEnabled(): boolean {
    return process.env.AUTH_ENABLED?.toLowerCase() === "true";
}

/** Token pair returned by the Core authentication API. */
export interface CoreTokenResponse {
    accessToken: string;
    tokenType: "Bearer";
    expiresAtUtc: string;
    refreshToken: string;
    refreshTokenExpiresAtUtc: string;
    sessionPurpose: "full";
}

/** Short-lived Core response that can authorize only the first explicit OIDC link. */
export interface CoreOidcLinkTokenResponse {
    accessToken: string;
    tokenType: "Bearer";
    expiresAtUtc: string;
    refreshToken: null;
    refreshTokenExpiresAtUtc: null;
    sessionPurpose: "oidc_link_only";
}


/** Structured error returned by Core authentication endpoints. */
export interface CoreAuthError {
    code: string;
    message: string;
}

export type RefreshTokenRotationResult =
    | { kind: "rotated"; tokens: CoreTokenResponse }
    | { kind: "missing" }
    | { kind: "rejected" }
    | { kind: "unavailable"; status: number };

export type AccessTokenValidationResult = "valid" | "unauthorized" | "unavailable";

/** Internal Core URL used only by the Next.js server, never shipped to browser JavaScript. */
export function getCoreApiUrl(path: string): string {
    const coreUrl = process.env.CORE_URL || "http://host.docker.internal:5001";
    const normalizedPath = path.startsWith("/") ? path : `/${path}`;
    return `${coreUrl.replace(/\/$/, "")}/api${normalizedPath}`;
}

/**
 * Stores both secrets in HttpOnly cookies. HttpOnly prevents client-side JavaScript—including
 * injected scripts—from reading them. SameSite=Lax blocks most cross-site form/fetch requests,
 * while Secure is enabled explicitly for deployments served through HTTPS.
 */
export function setAuthCookies(response: NextResponse, tokens: CoreTokenResponse): void {
    setTokenCookie(response, ACCESS_TOKEN_COOKIE, tokens.accessToken, tokens.expiresAtUtc);
    setTokenCookie(
        response,
        REFRESH_TOKEN_COOKIE,
        tokens.refreshToken,
        tokens.refreshTokenExpiresAtUtc,
    );
}

/**
 * Stores only the restricted access JWT used for an immediate OIDC link. Core issues no refresh
 * secret, and the cookie is cleared after the callback regardless of success or failure.
 */
export function setOidcLinkCookie(
    response: NextResponse,
    token: CoreOidcLinkTokenResponse,
): void {
    setTokenCookie(response, ACCESS_TOKEN_COOKIE, token.accessToken, token.expiresAtUtc);
    response.cookies.set(REFRESH_TOKEN_COOKIE, "", expiredCookieOptions());
}

/** Deletes every browser-side GameHub authentication cookie after logout or failed refresh. */
export function clearAuthCookies(response: NextResponse): void {
    response.cookies.set(ACCESS_TOKEN_COOKIE, "", expiredCookieOptions());
    response.cookies.set(REFRESH_TOKEN_COOKIE, "", expiredCookieOptions());
    response.cookies.set(OIDC_TRANSACTION_COOKIE, "", {
        ...expiredCookieOptions(),
        // OIDC transactions are issued only with this deliberately narrower cookie path.
        path: "/api/auth/oidc",
        secure: true,
    });
}

/**
 * Decodes only the JWT expiry to decide whether a refresh is due. This is not authentication:
 * the BFF always sends the token to Core, whose JwtBearer handler verifies its signature,
 * issuer, audience, and lifetime before trusting any claim.
 */
export function isAccessTokenFresh(token: string | undefined, skewSeconds = 15): boolean {
    if (!token) return false;

    try {
        const segments = token.split(".");
        if (segments.length !== 3) return false;

        const payload = JSON.parse(Buffer.from(segments[1], "base64url").toString("utf8")) as {
            exp?: number;
        };
        return typeof payload.exp === "number" && payload.exp > Date.now() / 1000 + skewSeconds;
    } catch {
        return false;
    }
}

/**
 * Rotates a refresh secret directly against Core from the trusted Next.js server.
 *
 * A rejected credential is deliberately distinct from an unavailable refresh service. Only the
 * former confirms that the local browser session has ended; network and non-401 failures must
 * preserve the cookies so a temporary outage cannot sign the user out.
 */
export async function rotateRefreshToken(
    refreshToken: string | undefined,
): Promise<RefreshTokenRotationResult> {
    if (!refreshToken) return { kind: "missing" };

    try {
        const response = await fetch(getCoreApiUrl("/auth/refresh"), {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ refreshToken }),
            cache: "no-store",
        });
        if (response.status === 401) return { kind: "rejected" };
        if (!response.ok) {
            return {
                kind: "unavailable",
                status: safeAuthenticationFailureStatus(response.status),
            };
        }

        const tokens = (await response.json()) as CoreTokenResponse;
        return isTokenResponse(tokens)
            ? { kind: "rotated", tokens }
            : { kind: "unavailable", status: 502 };
    } catch {
        return { kind: "unavailable", status: 503 };
    }
}

/**
 * Performs a live, read-only Core authorization check for protected page navigation. The existing
 * session-list endpoint derives all identity from the bearer token and exposes no additional
 * trust boundary. Only its exact 401 result can initiate the refresh/session-ended flow.
 */
export async function validateAccessTokenWithCore(
    accessToken: string,
): Promise<AccessTokenValidationResult> {
    try {
        const response = await fetch(getCoreApiUrl("/auth/sessions"), {
            headers: { Authorization: `Bearer ${accessToken}` },
            cache: "no-store",
            signal: AbortSignal.timeout(10_000),
        });
        if (response.status === 401) return "unauthorized";
        return response.ok ? "valid" : "unavailable";
    } catch {
        return "unavailable";
    }
}

/** Returns the sole browser-visible 401 that means refresh has conclusively failed. */
export function createSessionEndedApiResponse(): NextResponse {
    const response = NextResponse.json(
        {
            code: "authentication_required",
            message: "The GameHub session has ended.",
        },
        {
            status: 401,
            headers: {
                "Cache-Control": "no-store",
                [SESSION_ENDED_HEADER]: SESSION_ENDED_HEADER_VALUE,
            },
        },
    );
    clearAuthCookies(response);
    return response;
}

/** Converts an inconclusive refresh failure into a safe non-401 response without clearing cookies. */
export function createAuthenticationUnavailableResponse(status: number): NextResponse {
    return NextResponse.json(
        {
            code: "authentication_unavailable",
            message: "GameHub authentication is temporarily unavailable.",
        },
        {
            status: safeAuthenticationFailureStatus(status),
            headers: { "Cache-Control": "no-store" },
        },
    );
}

/** Runtime validation keeps malformed upstream JSON out of security-sensitive cookies. */
export function isTokenResponse(value: unknown): value is CoreTokenResponse {
    if (!value || typeof value !== "object") return false;
    const token = value as Partial<CoreTokenResponse>;
    return (
        typeof token.accessToken === "string" &&
        token.tokenType === "Bearer" &&
        typeof token.expiresAtUtc === "string" &&
        typeof token.refreshToken === "string" &&
        typeof token.refreshTokenExpiresAtUtc === "string" &&
        (token.sessionPurpose === undefined || token.sessionPurpose === "full")
    );
}

/** Runtime validation for the deliberately non-refreshable OIDC-link bootstrap response. */
export function isOidcLinkTokenResponse(value: unknown): value is CoreOidcLinkTokenResponse {
    if (!value || typeof value !== "object") return false;
    const token = value as Partial<CoreOidcLinkTokenResponse>;
    return typeof token.accessToken === "string"
        && token.tokenType === "Bearer"
        && typeof token.expiresAtUtc === "string"
        && token.refreshToken === null
        && token.refreshTokenExpiresAtUtc === null
        && token.sessionPurpose === "oidc_link_only";
}

function setTokenCookie(
    response: NextResponse,
    name: string,
    value: string,
    expiresAtUtc: string,
): void {
    const expires = new Date(expiresAtUtc);
    response.cookies.set(name, value, {
        httpOnly: true,
        secure: shouldUseSecureCookies(),
        sameSite: "lax",
        path: "/",
        expires,
    });
}

function expiredCookieOptions() {
    return {
        httpOnly: true,
        secure: shouldUseSecureCookies(),
        sameSite: "lax" as const,
        path: "/",
        expires: new Date(0),
    };
}

function shouldUseSecureCookies(): boolean {
    // The stock Compose setup serves plain HTTP on localhost, where browsers reject Secure
    // cookies. Reverse-proxy deployments should explicitly enable this after terminating TLS.
    return process.env.AUTH_COOKIE_SECURE?.toLowerCase() === "true";
}

function safeAuthenticationFailureStatus(status: number): number {
    if (
        status === 403
        || status === 404
        || status === 429
        || (status >= 500 && status <= 599)
    ) {
        return status;
    }
    return 502;
}
