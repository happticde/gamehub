import { NextRequest, NextResponse } from "next/server";
import {
    ACCESS_TOKEN_COOKIE,
    REFRESH_TOKEN_COOKIE,
    isFrontendAuthEnabled,
} from "@/lib/auth-session";
import { getEpicConnectCookieKey, shouldUseSecureCookies } from "@/lib/epic-connect-server";
import {
    EPIC_CONNECT_MAX_AGE_SECONDS,
    EPIC_CONNECT_TRANSACTION_COOKIE,
    createEpicConnectTransaction,
    getEpicAuthorizationUrl,
    sealEpicConnectTransaction,
} from "@/lib/epic-connect-security";

export const runtime = "nodejs";

export async function POST(request: NextRequest): Promise<NextResponse> {
    try {
        if (isFrontendAuthEnabled()
            && !request.cookies.get(ACCESS_TOKEN_COOKIE)?.value
            && !request.cookies.get(REFRESH_TOKEN_COOKIE)?.value) {
            return NextResponse.json(
                { code: "authentication_required", message: "Sign in is required." },
                { status: 401, headers: { "Cache-Control": "no-store" } },
            );
        }

        const transaction = createEpicConnectTransaction();
        const response = NextResponse.json(
            {
                authorizationUrl: getEpicAuthorizationUrl(),
                state: transaction.state,
                expiresAtUtc: new Date(
                    transaction.createdAt + EPIC_CONNECT_MAX_AGE_SECONDS * 1000,
                ).toISOString(),
            },
            { headers: { "Cache-Control": "no-store" } },
        );
        response.cookies.set(
            EPIC_CONNECT_TRANSACTION_COOKIE,
            sealEpicConnectTransaction(transaction, getEpicConnectCookieKey()),
            {
                httpOnly: true,
                secure: shouldUseSecureCookies(),
                sameSite: "lax",
                path: "/api/platform-credentials/epic/connect",
                maxAge: EPIC_CONNECT_MAX_AGE_SECONDS,
            },
        );
        return response;
    } catch {
        return NextResponse.json(
            { code: "epic_connect_unavailable", message: "Epic Games connect is unavailable." },
            { status: 503, headers: { "Cache-Control": "no-store" } },
        );
    }
}
