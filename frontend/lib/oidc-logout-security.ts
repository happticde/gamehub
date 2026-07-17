/**
 * Pure RP-Initiated Logout helpers kept separate from server configuration and Next.js types.
 * This makes the redirect trust boundary directly testable without exposing provider settings to
 * Client Components.
 */

export interface OidcLogoutMetadata {
    issuer: string;
    end_session_endpoint?: string;
}

export interface OidcLogoutRequest {
    configuredIssuer: string;
    clientId: string;
    redirectUri: string;
    metadata: OidcLogoutMetadata;
}

/** Builds the provider redirect exclusively from server configuration and pinned discovery. */
export function createOidcLogoutUrl(request: OidcLogoutRequest): string | null {
    if (request.metadata.issuer !== request.configuredIssuer) {
        throw new Error("oidc_issuer_mismatch");
    }
    if (!request.metadata.end_session_endpoint) return null;
    if (!request.clientId.trim()) throw new Error("missing_oidc_client_id");

    const endpoint = requireHttpsUrl(
        "end_session_endpoint",
        request.metadata.end_session_endpoint,
    );
    // Never forward a provider-supplied hint value. GameHub intentionally retains no ID token;
    // client_id is the standards-based client identifier for this logout request.
    endpoint.searchParams.delete("id_token_hint");
    endpoint.searchParams.set("client_id", request.clientId);
    endpoint.searchParams.set(
        "post_logout_redirect_uri",
        createPostLogoutRedirectUri(request.redirectUri),
    );
    return endpoint.toString();
}

/** Derives the only allowed post-logout destination from the configured callback origin. */
export function createPostLogoutRedirectUri(redirectUri: string): string {
    const callback = requireHttpsUrl("redirect_uri", redirectUri);
    return new URL("/login", callback.origin).toString();
}

/**
 * Attempts local revocation before resolving a provider redirect and always falls back locally.
 * A missing refresh cookie deliberately skips both callbacks, preventing logout CSRF against the
 * provider from cross-site POSTs whose SameSite cookies were withheld by the browser.
 */
export async function resolveLogoutLocation(
    refreshToken: string | undefined,
    revokeRefreshSession: (refreshToken: string) => Promise<void>,
    resolveProviderLocation: () => Promise<string | null>,
): Promise<string> {
    if (!refreshToken) return "/login";

    try {
        await revokeRefreshSession(refreshToken);
    } catch {
        // Browser credentials must still be cleared when Core cannot revoke the server-side row.
    }

    try {
        return (await resolveProviderLocation()) ?? "/login";
    } catch {
        // OIDC disablement, invalid/missing metadata, and provider outages are local-only logout.
        return "/login";
    }
}

function requireHttpsUrl(name: string, value: string): URL {
    let url: URL;
    try {
        url = new URL(value);
    } catch {
        throw new Error(`invalid_${name}`);
    }

    if (
        url.protocol !== "https:"
        || !url.hostname
        || url.username
        || url.password
        || url.hash
    ) {
        throw new Error(`invalid_${name}`);
    }
    return url;
}
