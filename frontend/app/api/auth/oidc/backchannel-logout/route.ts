import { NextRequest, NextResponse } from "next/server";
import { getCoreApiUrl } from "@/lib/auth-session";
import { getOidcConfig } from "@/lib/oidc";

export const runtime = "nodejs";

const MAXIMUM_LOGOUT_TOKEN_LENGTH = 32 * 1024;

/**
 * Receives an OIDC Back-Channel Logout request directly from the configured provider.
 *
 * This route owns only the public HTTP/form boundary. Core independently validates the signed
 * Logout Token and owns identity resolution plus durable session invalidation. No browser cookie,
 * request host, provider profile claim, or caller-selected tenant participates in the decision.
 */
export async function POST(request: NextRequest): Promise<NextResponse> {
    const contentType = request.headers.get("content-type")?.toLowerCase() ?? "";
    if (!contentType.startsWith("application/x-www-form-urlencoded")) {
        return noStoreResponse(400);
    }

    let submittedTokens: FormDataEntryValue[];
    try {
        submittedTokens = (await request.formData()).getAll("logout_token");
    } catch {
        return noStoreResponse(400);
    }
    if (submittedTokens.length !== 1
        || typeof submittedTokens[0] !== "string"
        || submittedTokens[0].length === 0
        || submittedTokens[0].length > MAXIMUM_LOGOUT_TOKEN_LENGTH) {
        return noStoreResponse(400);
    }

    try {
        const config = getOidcConfig();
        const coreResponse = await fetch(getCoreApiUrl("/auth/oidc/backchannel-logout"), {
            method: "POST",
            headers: {
                "Content-Type": "application/json",
                "X-GameHub-Oidc-Bff-Key": config.bffServiceKey,
            },
            body: JSON.stringify({ logoutToken: submittedTokens[0] }),
            cache: "no-store",
            redirect: "error",
            signal: AbortSignal.timeout(10_000),
        });

        // Protocol-invalid messages are 400. Provider/Core availability failures stay retryable
        // as 503, while all accepted, repeated, unknown, or already logged-out identities are 200.
        return noStoreResponse(coreResponse.ok ? 200 : coreResponse.status === 400 ? 400 : 503);
    } catch {
        return noStoreResponse(503);
    }
}

function noStoreResponse(status: number): NextResponse {
    return new NextResponse(null, {
        status,
        headers: { "Cache-Control": "no-store" },
    });
}
