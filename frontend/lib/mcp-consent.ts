import "server-only";

import { getOidcConfig } from "@/lib/oidc";
import { trustedBrowserOrigin } from "@/lib/browser-origin-security";
import type { McpConsentInput } from "@/lib/mcp-consent-contract";

/** Independent server-only transport. The generic Core adapter adds /api and cannot serve this flow. */
export function getMcpConsentConfig() {
    if (process.env.MCP_DEVICE_ENABLED !== "true") throw new Error("mcp_consent_disabled");
    const oidc = getOidcConfig();
    const instance = trustedBrowserOrigin(process.env.PUBLIC_ORIGIN);
    if (!instance.startsWith("https:") || process.env.MCP_ISSUER !== `${instance}/`
        || new URL(oidc.redirectUri).origin !== instance || new URL(oidc.issuer).origin === instance) {
        throw new Error("invalid_mcp_origin");
    }
    const key = process.env.MCP_BFF_SERVICE_KEY ?? "";
    if (!/^[!-~]{32,512}$/.test(key)
        || [oidc.bffServiceKey, process.env.OIDC_COOKIE_KEY, oidc.clientSecret, process.env.CORE_API_KEY]
            .map(value => value?.trim()).includes(key)) {
        throw new Error("invalid_mcp_service_trust");
    }
    const destination = new URL(process.env.MCP_CORE_ORIGIN ?? "");
    if (destination.protocol !== "https:" || destination.username || destination.password
        || destination.pathname !== "/" || destination.search || destination.hash) throw new Error("invalid_mcp_destination");
    return { instance, key, destination: new URL("/connect/verify", destination) };
}

/** Never forwards browser headers, owner selectors, queries or redirects; never retries a decision. */
export async function sendMcpConsent(input: McpConsentInput, accessToken: string) {
    const config = getMcpConsentConfig();
    const body = new URLSearchParams({ user_code: input.userCode, action: input.action });
    if (input.confirmation !== undefined) body.set("confirmation", input.confirmation);
    if (input.allowTrackingWrite !== undefined) body.set("allow_tracking_write", String(input.allowTrackingWrite));
    return fetch(config.destination, {
        method: "POST",
        headers: {
            "Content-Type": "application/x-www-form-urlencoded",
            "X-GameHub-Mcp-Bff-Key": config.key,
            Authorization: `Bearer ${accessToken}`,
            Host: new URL(config.instance).host,
        },
        body, cache: "no-store", redirect: "error", signal: AbortSignal.timeout(10_000),
    });
}
