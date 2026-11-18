const clients = new Map([
    ["https://chatgpt.com/oauth/codex/client.json", "Codex"],
    ["https://nousresearch.github.io/hermes-agent/docs/oauth/client-metadata.json", "Hermes Agent"],
]);

export function parseMcpRemoteRequest(clientId: unknown, requestUri: unknown) {
    if (typeof clientId !== "string" || !clients.has(clientId) || typeof requestUri !== "string"
        || !/^urn:ietf:params:oauth:request_uri:[A-Za-z0-9_-]{43}$/.test(requestUri)) {
        throw new Error("invalid_remote_request");
    }
    return { clientId, requestUri };
}

export type McpRemoteRequest = ReturnType<typeof parseMcpRemoteRequest>;

export function parseMcpRemoteStart(location: string, instance: string): URL {
    const url = new URL(location);
    if (url.origin !== instance || url.pathname !== "/mcp/authorize" || url.hash || url.username || url.password
        || url.searchParams.size !== 2 || url.searchParams.getAll("client_id").length !== 1
        || url.searchParams.getAll("request_uri").length !== 1) throw new Error("invalid_remote_start");
    parseMcpRemoteRequest(url.searchParams.get("client_id"), url.searchParams.get("request_uri"));
    return url;
}

export function parseMcpRemoteCallback(location: string, instance: string, clientId: string, action: "approve" | "deny"): URL {
    const url = new URL(location);
    if (url.protocol !== "http:" || (url.hostname !== "127.0.0.1" && url.hostname !== "localhost")
        || url.pathname !== "/callback" || !location.startsWith(`${url.origin}/callback?`)
        || url.username || url.password || url.hash
        || (clients.get(clientId) === "Hermes Agent" && !/^2789[0-4]$/.test(url.port))
        || !clients.has(clientId) || url.searchParams.size !== 3
        || url.searchParams.getAll("iss").length !== 1 || url.searchParams.get("iss") !== `${instance}/`
        || url.searchParams.getAll("state").length !== 1 || !url.searchParams.get("state")
        || (action === "approve" ? url.searchParams.getAll("code").length !== 1 || !url.searchParams.get("code")
            : url.searchParams.getAll("error").length !== 1 || url.searchParams.get("error") !== "access_denied")) {
        throw new Error("invalid_remote_callback");
    }
    return url;
}

export function parseMcpRemotePreview(value: unknown, instance: string, clientId: string) {
    if (typeof value !== "object" || value === null) throw new Error("invalid_remote_preview");
    const data = value as Record<string, unknown>;
    if (typeof data.client !== "string" || data.client !== clients.get(clientId)
        || data.resource !== `${instance}/mcp`
        || typeof data.account !== "string" || data.account.length === 0 || data.account.length > 512
        || typeof data.accountReference !== "string" || !/^[a-f0-9-]{36}$/i.test(data.accountReference)
        || typeof data.confirmation !== "string" || !/^[A-Za-z0-9_-]{32,2048}$/.test(data.confirmation)
        || typeof data.trackingWriteRequested !== "boolean" || typeof data.renewalRequested !== "boolean"
        || typeof data.expiresAtUtc !== "string" || !(Date.parse(data.expiresAtUtc) > Date.now())) {
        throw new Error("invalid_remote_preview");
    }
    return {
        client: data.client, resource: data.resource, account: data.account, accountReference: data.accountReference,
        confirmation: data.confirmation, trackingWriteRequested: data.trackingWriteRequested,
        renewalRequested: data.renewalRequested,
    };
}
