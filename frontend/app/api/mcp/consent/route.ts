import { NextRequest, NextResponse } from "next/server";
import { rejectUnsafeBrowserRequest } from "@/lib/browser-request";
import {
    ACCESS_TOKEN_COOKIE, REFRESH_TOKEN_COOKIE, isAccessTokenFresh, rotateRefreshToken,
    setAuthCookies, createSessionEndedApiResponse, createAuthenticationUnavailableResponse,
    type CoreTokenResponse,
} from "@/lib/auth-session";
import { getMcpConsentConfig, sendMcpConsent } from "@/lib/mcp-consent";
import { parseMcpConsentInput, parseMcpConsentPreview } from "@/lib/mcp-consent-contract";

export const runtime = "nodejs";

/** CSRF checks precede cookies, bounded body parsing and every upstream request. */
export async function POST(request: NextRequest): Promise<NextResponse> {
    const rejected = rejectUnsafeBrowserRequest(request);
    if (rejected) return rejected;
    let instance: string;
    try { instance = getMcpConsentConfig().instance; }
    catch { return failure(503); }
    let input;
    try {
        if (request.nextUrl.search || request.headers.get("content-type") !== "application/json") return failure(400);
        // Enforce the cap for chunked input too, before JSON parsing or token rotation.
        const reader = request.body?.getReader();
        if (!reader) return failure(400);
        const chunks: Uint8Array[] = [];
        let size = 0;
        try {
            while (true) {
                const { done, value } = await reader.read();
                if (done) break;
                size += value.length;
                if (size > 4096) { await reader.cancel(); return failure(413); }
                chunks.push(value);
            }
        } finally { reader.releaseLock(); }
        input = parseMcpConsentInput(JSON.parse(Buffer.concat(chunks).toString("utf8")));
    } catch { return failure(400); }

    let accessToken = request.cookies.get(ACCESS_TOKEN_COOKIE)?.value;
    let rotated: CoreTokenResponse | undefined;
    if (!isAccessTokenFresh(accessToken)) {
        const rotation = await rotateRefreshToken(request.cookies.get(REFRESH_TOKEN_COOKIE)?.value);
        if (rotation.kind === "unavailable") return createAuthenticationUnavailableResponse(rotation.status);
        if (rotation.kind !== "rotated") return createSessionEndedApiResponse();
        rotated = rotation.tokens;
        accessToken = rotated.accessToken;
    }
    let response: NextResponse;
    try {
        const upstream = await sendMcpConsent(input, accessToken!);
        if (upstream.status === 401) return createSessionEndedApiResponse();
        if (input.action === "preview" && upstream.status === 200) {
            response = NextResponse.json(parseMcpConsentPreview(await upstream.json(), instance), {
                headers: { "Cache-Control": "no-store" },
            });
        } else if (input.action !== "preview" && upstream.status === 204) {
            response = new NextResponse(null, { status: 204, headers: { "Cache-Control": "no-store" } });
        } else response = failure([400, 403, 413, 429].includes(upstream.status) ? upstream.status : 503);
    } catch { response = failure(503); }
    if (rotated) setAuthCookies(response, rotated);
    return response;
}

function failure(status: number): NextResponse {
    return NextResponse.json({ code: "consent_unavailable" }, { status, headers: { "Cache-Control": "no-store" } });
}
