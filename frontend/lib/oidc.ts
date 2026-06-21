import "server-only";

export interface OidcBffConfig {
    issuer: string;
    clientId: string;
    clientSecret?: string;
    redirectUri: string;
    scopes: string;
    cookieKey: Buffer;
    bffServiceKey: string;
}

export interface OidcDiscoveryDocument {
    issuer: string;
    authorization_endpoint: string;
    token_endpoint: string;
}

export function isOidcEnabled(): boolean {
    return process.env.OIDC_ENABLED?.toLowerCase() === "true";
}

/** Parses all BFF trust settings together; any partial enabled configuration fails closed. */
export function getOidcConfig(): OidcBffConfig {
    if (!isOidcEnabled()) throw new Error("oidc_disabled");
    if (process.env.AUTH_ENABLED?.toLowerCase() !== "true") throw new Error("oidc_requires_auth");
    if (process.env.AUTH_COOKIE_SECURE?.toLowerCase() !== "true") {
        throw new Error("oidc_requires_secure_cookies");
    }

    const issuer = requireHttpsUrl("OIDC_ISSUER", process.env.OIDC_ISSUER);
    const redirectUri = requireHttpsUrl("OIDC_REDIRECT_URI", process.env.OIDC_REDIRECT_URI);
    const clientId = process.env.OIDC_CLIENT_ID?.trim();
    if (!clientId) throw new Error("missing_oidc_client_id");
    const scopes = process.env.OIDC_SCOPES?.trim() || "openid profile email";
    if (!scopes.split(/\s+/).includes("openid")) throw new Error("oidc_scope_requires_openid");

    const encodedKey = process.env.OIDC_COOKIE_KEY?.trim() || "";
    const cookieKey = Buffer.from(encodedKey, "base64");
    if (cookieKey.length !== 32 || cookieKey.toString("base64") !== encodedKey) {
        throw new Error("invalid_oidc_cookie_key");
    }
    const bffServiceKey = process.env.OIDC_BFF_SERVICE_KEY?.trim() || "";
    if (bffServiceKey.length < 32 || bffServiceKey.length > 512 || !/^[!-~]+$/.test(bffServiceKey)) {
        throw new Error("invalid_oidc_bff_service_key");
    }

    return {
        issuer,
        clientId,
        clientSecret: process.env.OIDC_CLIENT_SECRET?.trim() || undefined,
        redirectUri,
        scopes,
        cookieKey,
        bffServiceKey,
    };
}

/** Fetches provider metadata and pins its issuer plus HTTPS endpoints to configured trust. */
export async function getOidcDiscovery(config: OidcBffConfig): Promise<OidcDiscoveryDocument> {
    const response = await fetch(`${config.issuer}/.well-known/openid-configuration`, {
        headers: { Accept: "application/json" },
        cache: "no-store",
        redirect: "error",
        signal: AbortSignal.timeout(10_000),
    });
    if (!response.ok) throw new Error("oidc_discovery_unavailable");
    const value = (await response.json()) as Partial<OidcDiscoveryDocument>;
    if (value.issuer !== config.issuer) throw new Error("oidc_issuer_mismatch");
    const authorizationEndpoint = requireHttpsUrl(
        "authorization_endpoint",
        value.authorization_endpoint,
    );
    const tokenEndpoint = requireHttpsUrl("token_endpoint", value.token_endpoint);
    return {
        issuer: config.issuer,
        authorization_endpoint: authorizationEndpoint,
        token_endpoint: tokenEndpoint,
    };
}

function requireHttpsUrl(name: string, value: string | undefined): string {
    if (!value) throw new Error(`missing_${name.toLowerCase()}`);
    const url = new URL(value);
    if (url.protocol !== "https:" || !url.hostname || url.hash) throw new Error(`invalid_${name}`);
    return value;
}
