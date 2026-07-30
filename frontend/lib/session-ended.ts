/** Internal BFF signal emitted only after both access and refresh authentication have failed. */
export const SESSION_ENDED_HEADER = "X-GameHub-Session-Ended";
export const SESSION_ENDED_HEADER_VALUE = "1";
export const SESSION_VALIDATED_REQUEST_HEADER = "X-GameHub-Session-Validated";

/** Fixed Same-Origin boundary that expires GameHub cookies before showing the login page. */
export const SESSION_ENDED_ROUTE = "/api/auth/session-ended";
export const SESSION_ENDED_LOGIN_PATH = "/login?sessionEnded=true";

/** Keeps ordinary Core errors, including every non-401 response, out of the logout path. */
export function isConfirmedSessionEnded(response: Pick<Response, "status" | "headers">): boolean {
    return response.status === 401
        && response.headers.get(SESSION_ENDED_HEADER) === SESSION_ENDED_HEADER_VALUE;
}

/**
 * Creates a one-shot navigation guard. Parallel background requests may all observe the same
 * revoked session, but only the first one is allowed to start the fixed cleanup navigation.
 */
export function createSingleNavigation(navigate: () => void): () => void {
    let started = false;
    return () => {
        if (started) return;
        started = true;
        navigate();
    };
}
