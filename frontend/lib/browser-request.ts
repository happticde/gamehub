import "server-only";

import { NextRequest, NextResponse } from "next/server";
import { isTrustedBrowserMutation, trustedBrowserOrigin } from "@/lib/browser-origin-security";

/** Reject untrusted writes before reading bodies, rotating tokens, or contacting Core. */
export function rejectUnsafeBrowserRequest(request: NextRequest): NextResponse | null {
    if (["GET", "HEAD", "OPTIONS"].includes(request.method)) return null;
    let origin: string;
    try {
        const configured = process.env.PUBLIC_ORIGIN
            || (process.env.OIDC_REDIRECT_URI ? new URL(process.env.OIDC_REDIRECT_URI).origin : undefined)
            || (process.env.NODE_ENV === "development" ? "http://localhost:3005" : undefined);
        origin = trustedBrowserOrigin(configured);
    } catch {
        return NextResponse.json(
            { code: "browser_origin_unavailable", message: "Browser request configuration is unavailable." },
            { status: 503, headers: { "Cache-Control": "no-store" } },
        );
    }
    if (isTrustedBrowserMutation(request.headers, origin)) return null;
    return NextResponse.json(
        { code: "invalid_request_origin", message: "A same-origin browser request is required." },
        { status: 403, headers: { "Cache-Control": "no-store" } },
    );
}
