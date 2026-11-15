/** Minimal consent metadata; protocol and provider credentials never belong in browser state. */
export interface McpConnection {
    id: string;
    clientId: string;
    resource: string;
    scopes: string[];
    createdAtUtc: string;
    expiresAtUtc: string;
    revokedAtUtc: null;
}

const keys = new Set(["id", "clientId", "resource", "scopes", "createdAtUtc", "expiresAtUtc", "revokedAtUtc"]);

/** Reject extra fields before storing the server response, including accidental secret metadata. */
export function parseMcpConnections(value: unknown): McpConnection[] {
    if (!Array.isArray(value)) throw new Error("invalid_connections");
    const ids = new Set<string>();
    return value.map((item) => {
        if (!item || typeof item !== "object" || Array.isArray(item)) throw new Error("invalid_connection");
        const row = item as Record<string, unknown>;
        if (Object.keys(row).length !== keys.size || Object.keys(row).some((key) => !keys.has(key))
            || typeof row.id !== "string" || !/^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(row.id)
            || ids.has(row.id) || typeof row.clientId !== "string" || !row.clientId || row.clientId.length > 200
            || typeof row.resource !== "string" || !URL.canParse(row.resource)
            || new URL(row.resource).protocol !== "https:"
            || !Array.isArray(row.scopes) || row.scopes[0] !== "library:read"
            || row.scopes.length > 2 || (row.scopes.length === 2 && row.scopes[1] !== "tracking:write")
            || typeof row.createdAtUtc !== "string" || !Number.isFinite(Date.parse(row.createdAtUtc))
            || typeof row.expiresAtUtc !== "string" || !Number.isFinite(Date.parse(row.expiresAtUtc))
            || !row.createdAtUtc.endsWith("Z") || !row.expiresAtUtc.endsWith("Z")
            || Date.parse(row.expiresAtUtc) <= Date.parse(row.createdAtUtc)
            || row.revokedAtUtc !== null) throw new Error("invalid_connection");
        ids.add(row.id);
        return { id: row.id, clientId: row.clientId, resource: row.resource,
            scopes: [...row.scopes], createdAtUtc: row.createdAtUtc,
            expiresAtUtc: row.expiresAtUtc, revokedAtUtc: null };
    });
}
