import { NextRequest, NextResponse } from "next/server";
import {
    ACCESS_TOKEN_COOKIE, REFRESH_TOKEN_COOKIE, clearAuthCookies, isAccessTokenFresh, rotateRefreshToken,
    setAuthCookies, type CoreTokenResponse,
} from "@/lib/auth-session";
import { rejectUnsafeBrowserRequest } from "@/lib/browser-request";
import { sendMcpRemoteConsent } from "@/lib/mcp-consent";
import { getManagedMcpConfig } from "@/lib/mcp-managed-config.mjs";
import { parseMcpRemoteCallback, parseMcpRemoteRequest } from "@/lib/mcp-remote-contract";

export const runtime = "nodejs";
const headers = { "Cache-Control": "no-store", "Referrer-Policy": "no-referrer" };
const fields = new Set(["client_id", "request_uri", "confirmation", "action", "tracking"]);

/** The browser receives a navigation response only after Core commits the one-time decision. */
export async function POST(request: NextRequest): Promise<NextResponse> {
    const rejected = rejectUnsafeBrowserRequest(request);
    if (rejected) return rejected;
    try {
        const config = getManagedMcpConfig();
        if (!config) return new NextResponse(null, { status: 404, headers });
        const form = await readForm(request);
        const native = parseMcpRemoteRequest(form.get("client_id"), form.get("request_uri"));
        const action = form.get("action");
        const confirmation = form.get("confirmation");
        if ((action !== "approve" && action !== "deny") || !confirmation || !/^[A-Za-z0-9_-]{32,2048}$/.test(confirmation)
            || (form.has("tracking") && form.get("tracking") !== "true")) throw new Error("invalid_decision");
        const review = new URL("/mcp/authorize", config.instance);
        review.searchParams.set("client_id", native.clientId);
        review.searchParams.set("request_uri", native.requestUri);
        const signIn = () => {
            const login = new URL("/login", config.instance);
            login.searchParams.set("returnTo", review.pathname + review.search);
            const reply = NextResponse.redirect(login, { status: 303, headers });
            clearAuthCookies(reply);
            return reply;
        };
        let accessToken = request.cookies.get(ACCESS_TOKEN_COOKIE)?.value;
        let rotated: CoreTokenResponse | undefined;
        if (!isAccessTokenFresh(accessToken)) {
            const rotation = await rotateRefreshToken(request.cookies.get(REFRESH_TOKEN_COOKIE)?.value);
            if (rotation.kind === "unavailable") return new NextResponse("Sign-in is temporarily unavailable.", { status: 503, headers });
            if (rotation.kind !== "rotated") return signIn();
            rotated = rotation.tokens;
            accessToken = rotated.accessToken;
        }
        let destination: URL;
        try {
            const upstream = await sendMcpRemoteConsent(native, {
                action, confirmation, allowTrackingWrite: form.get("tracking") === "true",
            }, accessToken!);
            if (upstream.status === 401) return signIn();
            if (upstream.status !== 302) throw new Error("decision_unconfirmed");
            destination = parseMcpRemoteCallback(upstream.headers.get("location") ?? "", config.instance, native.clientId, action);
        } catch {
            review.searchParams.set("result", "uncertain");
            destination = review;
        }
        const response = NextResponse.redirect(destination, { status: 303, headers });
        if (rotated) setAuthCookies(response, rotated);
        return response;
    } catch {
        return new NextResponse("This request could not be processed. Start the connection again from your assistant.", {
            status: 400, headers,
        });
    }
}

async function readForm(request: NextRequest) {
    const mediaType = request.headers.get("content-type")?.split(";")[0].trim().toLowerCase();
    if (request.nextUrl.search || mediaType !== "application/x-www-form-urlencoded"
        || !request.body) throw new Error("invalid_form");
    const reader = request.body.getReader();
    const chunks: Uint8Array[] = [];
    let length = 0;
    try {
        while (true) {
            const { done, value } = await reader.read();
            if (done) break;
            length += value.length;
            if (length > 4096) {
                await reader.cancel();
                throw new Error("form_too_large");
            }
            chunks.push(value);
        }
    } finally { reader.releaseLock(); }
    const form = new URLSearchParams(Buffer.concat(chunks).toString("utf8"));
    const seen = new Set<string>();
    for (const [name] of form) {
        if (!fields.has(name) || seen.has(name)) throw new Error("invalid_fields");
        seen.add(name);
    }
    return form;
}
