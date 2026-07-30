import "server-only";

import { cookies, headers as requestHeaders } from "next/headers";
import { redirect, unstable_rethrow } from "next/navigation";
import { ACCESS_TOKEN_COOKIE, getCoreApiUrl } from "@/lib/auth-session";
import {
    SESSION_ENDED_ROUTE,
    SESSION_VALIDATED_REQUEST_HEADER,
} from "@/lib/session-ended";

/**
 * Authenticated Core fetch for React Server Components. Browser components instead call
 * /api/core/*, where the BFF route performs the same Authorization-header translation.
 */
export async function fetchCoreApi(path: string, init: RequestInit = {}): Promise<Response> {
    const [cookieStore, incomingHeaders] = await Promise.all([cookies(), requestHeaders()]);
    const accessToken = cookieStore.get(ACCESS_TOKEN_COOKIE)?.value;
    const headers = new Headers(init.headers);
    if (accessToken) headers.set("Authorization", `Bearer ${accessToken}`);

    const response = await fetch(getCoreApiUrl(path), {
        ...init,
        headers,
    });

    // Middleware has already live-validated this request or successfully rotated its refresh
    // token. A subsequent 401 is therefore a confirmed revocation race and must not render the
    // page's ordinary Core/infrastructure error card.
    if (
        response.status === 401
        && incomingHeaders.get(SESSION_VALIDATED_REQUEST_HEADER) === "1"
    ) {
        redirect(SESSION_ENDED_ROUTE);
    }

    return response;
}

/**
 * Broad page-level error boundaries must preserve Next.js redirect exceptions raised above.
 * Keeping this helper beside fetchCoreApi avoids page-specific authentication redirect logic.
 */
export function rethrowCoreApiNavigation(error: unknown): void {
    unstable_rethrow(error);
}
