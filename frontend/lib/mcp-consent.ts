import "server-only";

import { getOidcConfig } from "@/lib/oidc";
import { trustedBrowserOrigin } from "@/lib/browser-origin-security";
import type { McpConsentInput } from "@/lib/mcp-consent-contract";
import type { McpRemoteRequest } from "@/lib/mcp-remote-contract";
import { getManagedMcpConfig, managedMcpHeaders } from "@/lib/mcp-managed-config.mjs";

/** Independent server-only transport. The generic Core adapter adds /api and cannot serve this flow. */
export function getMcpConsentConfig() {
    const managed = getManagedMcpConfig();
    if (managed) {
        getOidcConfig();
        return { ...managed, destination: new URL("/connect/verify", managed.core), headers: managedMcpHeaders(managed) };
    }
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
    return { instance, key, destination: new URL("/connect/verify", destination), headers: { Host: new URL(instance).host } };
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
            ...config.headers,
            "Content-Type": "application/x-www-form-urlencoded",
            "X-GameHub-Mcp-Bff-Key": config.key,
            Authorization: `Bearer ${accessToken}`,
        },
        body, cache: "no-store", redirect: "error", signal: AbortSignal.timeout(10_000),
    });
}

type RemoteDecision = { action: "preview" } | {
    action: "approve" | "deny"; confirmation: string; allowTrackingWrite: boolean;
};

/** The managed browser route alone can contact the private authorization continuation. */
export async function sendMcpRemoteConsent(request: McpRemoteRequest, decision: RemoteDecision, accessToken: string) {
    const config = getManagedMcpConfig();
    if (!config) throw new Error("mcp_remote_consent_disabled");
    const body = new URLSearchParams({ client_id: request.clientId, request_uri: request.requestUri, action: decision.action });
    if (decision.action !== "preview") body.set("confirmation", decision.confirmation);
    if (decision.action === "approve") body.set("allow_tracking_write", String(decision.allowTrackingWrite));
    return fetch(new URL("/connect/authorize/consent", config.core), {
        method: "POST", body, cache: "no-store", redirect: "manual", signal: AbortSignal.timeout(10_000),
        headers: {
            ...managedMcpHeaders(config),
            "Content-Type": "application/x-www-form-urlencoded",
            "X-GameHub-Mcp-Bff-Key": config.key,
            Authorization: `Bearer ${accessToken}`,
        },
    });
}
