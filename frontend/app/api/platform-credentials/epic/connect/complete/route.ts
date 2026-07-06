import { NextRequest, NextResponse } from "next/server";
import {
    ACCESS_TOKEN_COOKIE,
    REFRESH_TOKEN_COOKIE,
    clearAuthCookies,
    getCoreApiUrl,
    isFrontendAuthEnabled,
    rotateRefreshToken,
    setAuthCookies,
    type CoreTokenResponse,
} from "@/lib/auth-session";
import { getEpicConnectCookieKey, shouldUseSecureCookies } from "@/lib/epic-connect-server";
import {
    EPIC_CONNECT_TRANSACTION_COOKIE,
    openEpicConnectTransaction,
    validateEpicConnectState,
} from "@/lib/epic-connect-security";

export const runtime = "nodejs";

type EpicConnectCompleteRequest = {
    authorizationCode?: unknown;
    state?: unknown;
};

export async function POST(request: NextRequest): Promise<NextResponse> {
    let response: NextResponse;
    try {
        validateSameOrigin(request);

        const sealedTransaction = request.cookies.get(EPIC_CONNECT_TRANSACTION_COOKIE)?.value;
        if (!sealedTransaction) throw new Error("missing_epic_connect_transaction");

        const body = await request.json().catch(() => null) as EpicConnectCompleteRequest | null;
        const authorizationCode = readAuthorizationCode(body?.authorizationCode);
        const state = typeof body?.state === "string" ? body.state : null;
        validateEpicConnectState(
            openEpicConnectTransaction(sealedTransaction, getEpicConnectCookieKey()),
            state,
        );

        response = await storeEpicCredential(request, authorizationCode);
    } catch {
        response = NextResponse.json(
            { code: "invalid_epic_connect", message: "Epic Games connect could not be completed." },
            { status: 400, headers: { "Cache-Control": "no-store" } },
        );
    }

    response.cookies.set(EPIC_CONNECT_TRANSACTION_COOKIE, "", {
        httpOnly: true,
        secure: shouldUseSecureCookies(),
        sameSite: "lax",
        path: "/api/platform-credentials/epic/connect",
        expires: new Date(0),
    });
    response.headers.set("Cache-Control", "no-store");
    return response;
}

async function storeEpicCredential(
    request: NextRequest,
    authorizationCode: string,
): Promise<NextResponse> {
    let accessToken = request.cookies.get(ACCESS_TOKEN_COOKIE)?.value;
    const refreshToken = request.cookies.get(REFRESH_TOKEN_COOKIE)?.value;
    let rotatedTokens: CoreTokenResponse | null = null;

    if (isFrontendAuthEnabled()) {
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
    }

    let upstream = await sendCredentialToCore(accessToken, authorizationCode);
    if (upstream.status === 401 && refreshToken && !rotatedTokens) {
        rotatedTokens = await rotateRefreshToken(refreshToken);
        if (rotatedTokens) {
            accessToken = rotatedTokens.accessToken;
            upstream = await sendCredentialToCore(accessToken, authorizationCode);
        }
    }

    const response = await copyCoreJsonResponse(upstream);
    if (rotatedTokens) {
        setAuthCookies(response, rotatedTokens);
    } else if (upstream.status === 401) {
        clearAuthCookies(response);
    }
    return response;
}

async function sendCredentialToCore(
    accessToken: string | undefined,
    authorizationCode: string,
): Promise<Response> {
    const headers = new Headers({ "Content-Type": "application/json", Accept: "application/json" });
    if (accessToken) headers.set("Authorization", `Bearer ${accessToken}`);

    return fetch(getCoreApiUrl("/platform-credentials/Epic"), {
        method: "PUT",
        headers,
        body: JSON.stringify({
            schemaVersion: 1,
            credentials: { authorizationCode },
        }),
        cache: "no-store",
        redirect: "manual",
    });
}

async function copyCoreJsonResponse(upstream: Response): Promise<NextResponse> {
    const headers = new Headers();
    const contentType = upstream.headers.get("content-type");
    if (contentType) headers.set("content-type", contentType);
    return new NextResponse(upstream.status === 204 ? null : await upstream.text(), {
        status: upstream.status,
        headers,
    });
}

function validateSameOrigin(request: NextRequest): void {
    const origin = request.headers.get("origin");
    if (origin && origin !== request.nextUrl.origin) {
        throw new Error("invalid_epic_connect_origin");
    }
}

function readAuthorizationCode(value: unknown): string {
    if (typeof value !== "string") {
        throw new Error("missing_epic_authorization_code");
    }

    const code = value.trim();
    if (!code || code.length > 2048) {
        throw new Error("invalid_epic_authorization_code");
    }
    return code;
}
