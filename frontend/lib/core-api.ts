import "server-only";

import { cookies } from "next/headers";
import { ACCESS_TOKEN_COOKIE, getCoreApiUrl } from "@/lib/auth-session";

/**
 * Authenticated Core fetch for React Server Components. Browser components instead call
 * /api/core/*, where the BFF route performs the same Authorization-header translation.
 */
export async function fetchCoreApi(path: string, init: RequestInit = {}): Promise<Response> {
    const cookieStore = await cookies();
    const accessToken = cookieStore.get(ACCESS_TOKEN_COOKIE)?.value;
    const headers = new Headers(init.headers);
    if (accessToken) headers.set("Authorization", `Bearer ${accessToken}`);

    return fetch(getCoreApiUrl(path), {
        ...init,
        headers,
    });
}
