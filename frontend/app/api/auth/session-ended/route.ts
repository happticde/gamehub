import { NextResponse } from "next/server";
import { clearAuthCookies } from "@/lib/auth-session";
import { SESSION_ENDED_LOGIN_PATH } from "@/lib/session-ended";

/**
 * Same-origin convergence point for a remotely revoked GameHub session.
 *
 * This route accepts no destination or provider input, calls no upstream service, and is safe to
 * repeat. Its only effects are expiring GameHub authentication cookies and navigating to login.
 */
export function GET(): NextResponse {
    const response = new NextResponse(null, {
        status: 303,
        headers: {
            "Cache-Control": "no-store",
            Location: SESSION_ENDED_LOGIN_PATH,
        },
    });
    clearAuthCookies(response);
    return response;
}
