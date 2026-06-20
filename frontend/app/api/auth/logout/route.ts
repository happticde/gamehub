import { NextRequest, NextResponse } from "next/server";
import {
    REFRESH_TOKEN_COOKIE,
    clearAuthCookies,
    getCoreApiUrl,
} from "@/lib/auth-session";

/** Revokes the current refresh session and always removes the local cookies. */
export async function POST(request: NextRequest): Promise<NextResponse> {
    const refreshToken = request.cookies.get(REFRESH_TOKEN_COOKIE)?.value;
    if (refreshToken) {
        try {
            await fetch(getCoreApiUrl("/auth/revoke"), {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ refreshToken }),
                cache: "no-store",
            });
        } catch {
            // Logout must still clear a browser's copied credentials when Core is temporarily
            // unavailable. The server-side session will expire naturally if revocation failed.
        }
    }

    const response = new NextResponse(null, { status: 204 });
    clearAuthCookies(response);
    return response;
}
