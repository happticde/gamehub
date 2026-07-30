import {
    SESSION_ENDED_ROUTE,
    createSingleNavigation,
    isConfirmedSessionEnded,
} from "@/lib/session-ended";

const navigateToEndedSession = createSingleNavigation(() => {
    // replace() is deliberately a top-level navigation. It reaches the server-side cookie cleanup
    // route even from a long-lived Client Component and prevents Back from reopening stale UI.
    window.location.replace(SESSION_ENDED_ROUTE);
});

/**
 * Browser fetch for authenticated same-origin BFF routes. Tokens remain in HttpOnly cookies; the
 * only client-visible auth state is a one-bit, BFF-owned instruction to leave a revoked session.
 */
export async function fetchAuthenticated(
    input: RequestInfo | URL,
    init?: RequestInit,
): Promise<Response> {
    const response = await fetch(input, init);
    if (!isConfirmedSessionEnded(response) || typeof window === "undefined") {
        return response;
    }

    navigateToEndedSession();

    // Do not let the calling component render its normal technical error state during navigation.
    // The promise is intentionally abandoned because the document is being replaced immediately.
    return new Promise<Response>(() => {
        // The top-level navigation unloads this JavaScript context.
    });
}
