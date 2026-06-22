import "server-only";

import { validateFrontendMultiUserConfiguration } from "@/lib/multi-user";

/**
 * Provider-neutral OIDC configuration and discovery for the Next.js BFF.
 *
 * `server-only` makes accidental import into a Client Component a build-time error. That is
 * essential because this module reads the provider client secret, correlation-cookie key, and
 * BFF-to-Core service key. None of those values may enter a browser bundle.
 */

export interface OidcBffConfig {
    /** Exact provider issuer; discovery must return the same string byte-for-byte. */
    issuer: string;
    /** OAuth client identifier and expected Core ID-token audience. */
    clientId: string;
    /** Optional confidential-client credential; PKCE remains mandatory with or without it. */
    clientSecret?: string;
    /** Exact externally visible HTTPS callback registered at the provider. */
    redirectUri: string;
    /** Requested scopes; `openid` is mandatory because OAuth alone does not authenticate users. */
    scopes: string;
    /** 32-byte AES-GCM key used only for the short-lived correlation cookie. */
    cookieKey: Buffer;
    /** Independent server-to-server credential that prevents direct Core OIDC exchange calls. */
    bffServiceKey: string;
}

/** Minimal subset of discovery metadata used by the BFF. Core separately consumes JWKS metadata. */
export interface OidcDiscoveryDocument {
    issuer: string;
    authorization_endpoint: string;
    token_endpoint: string;
}

export function isOidcEnabled(): boolean {
    return process.env.OIDC_ENABLED?.toLowerCase() === "true";
}

/**
 * Parses all BFF trust settings as one security unit.
 *
 * OIDC has no partially enabled mode: frontend session handling, Secure cookies, issuer/client
 * trust, and both server-only keys must agree. Throwing here is intentional; route handlers turn
 * configuration faults into a generic 503 instead of attempting an insecure fallback.
 */
export function getOidcConfig(): OidcBffConfig {
    validateFrontendMultiUserConfiguration();
    if (!isOidcEnabled()) throw new Error("oidc_disabled");
    if (process.env.AUTH_ENABLED?.toLowerCase() !== "true") throw new Error("oidc_requires_auth");
    if (process.env.AUTH_COOKIE_SECURE?.toLowerCase() !== "true") {
        throw new Error("oidc_requires_secure_cookies");
    }

    // Issuer and redirect are trust anchors, not user input discovered at runtime.
    const issuer = requireHttpsUrl("OIDC_ISSUER", process.env.OIDC_ISSUER);
    const redirectUri = requireHttpsUrl("OIDC_REDIRECT_URI", process.env.OIDC_REDIRECT_URI);
    const clientId = process.env.OIDC_CLIENT_ID?.trim();
    if (!clientId) throw new Error("missing_oidc_client_id");
    const scopes = process.env.OIDC_SCOPES?.trim() || "openid profile email";
    if (!scopes.split(/\s+/).includes("openid")) throw new Error("oidc_scope_requires_openid");

    // Decode and re-encode to reject permissive/non-canonical Base64 spellings.
    const encodedKey = process.env.OIDC_COOKIE_KEY?.trim() || "";
    const cookieKey = Buffer.from(encodedKey, "base64");
    if (cookieKey.length !== 32 || cookieKey.toString("base64") !== encodedKey) {
        throw new Error("invalid_oidc_cookie_key");
    }
    // Printable ASCII is safe for an HTTP header; length provides a minimum entropy budget.
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

/**
 * Fetches provider metadata and pins it to configured trust.
 *
 * Discovery improves provider neutrality, but it is not permission to trust arbitrary metadata.
 * The returned issuer must exactly equal the configured issuer, both endpoints must use HTTPS,
 * redirects are refused, responses are not cached by Next.js, and a bounded timeout converts a
 * provider outage into a closed authentication failure.
 */
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
    // This helper validates the URL without normalizing the returned string. Exact issuer and
    // redirect comparison is security-relevant, so implicit trailing-slash rewrites are avoided.
    if (!value) throw new Error(`missing_${name.toLowerCase()}`);
    const url = new URL(value);
    if (url.protocol !== "https:" || !url.hostname || url.hash) throw new Error(`invalid_${name}`);
    return value;
}
