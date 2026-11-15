/** Only these fields may cross the browser consent boundary. Ownership is always server-derived. */
export type McpConsentInput = {
    action: "preview" | "approve" | "deny";
    userCode: string;
    confirmation?: string;
    allowTrackingWrite?: boolean;
};

export type McpConsentPreview = {
    confirmation: string;
    instance: string;
    account: string;
    accountReference: string;
    resource: string;
    trackingWriteRequested: boolean;
    renewalRequested: boolean;
    expiresAtUtc: string;
};

/** Reject unknown/duplicate semantic choices before any authenticated upstream work. */
export function parseMcpConsentInput(value: unknown): McpConsentInput {
    if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error("invalid_consent");
    const input = value as Record<string, unknown>;
    if (Object.keys(input).some(key => !["action", "userCode", "confirmation", "allowTrackingWrite"].includes(key))
        || !["preview", "approve", "deny"].includes(String(input.action))
        || typeof input.userCode !== "string" || !/^[A-Za-z0-9 -]{1,32}$/.test(input.userCode)
        || (input.allowTrackingWrite !== undefined && typeof input.allowTrackingWrite !== "boolean")
        || (input.action !== "preview" && (typeof input.confirmation !== "string"
            || input.confirmation.length < 1 || input.confirmation.length > 2048))
        || (input.action === "preview" && (input.confirmation !== undefined || input.allowTrackingWrite !== undefined))) {
        throw new Error("invalid_consent");
    }
    return input as McpConsentInput;
}

/** A malformed upstream response never becomes a displayed consent decision. */
export function parseMcpConsentPreview(value: unknown, instance: string): McpConsentPreview {
    if (!value || typeof value !== "object") throw new Error("invalid_preview");
    const preview = value as Record<string, unknown>;
    if (preview.instance !== instance || preview.resource !== `${instance}/api`
        || typeof preview.confirmation !== "string" || preview.confirmation.length > 2048 || !preview.confirmation
        || typeof preview.account !== "string" || !preview.account || preview.account.length > 512
        || typeof preview.accountReference !== "string" || !/^[a-f0-9-]{36}$/.test(preview.accountReference)
        || typeof preview.trackingWriteRequested !== "boolean" || typeof preview.renewalRequested !== "boolean"
        || typeof preview.expiresAtUtc !== "string" || !Number.isFinite(Date.parse(preview.expiresAtUtc))
        || Date.parse(preview.expiresAtUtc) <= Date.now()) throw new Error("invalid_preview");
    // Construct a fixed shape. Never forward unknown upstream fields into browser JSON.
    return {
        confirmation: preview.confirmation, instance, account: preview.account,
        accountReference: preview.accountReference, resource: preview.resource,
        trackingWriteRequested: preview.trackingWriteRequested, renewalRequested: preview.renewalRequested,
        expiresAtUtc: preview.expiresAtUtc,
    };
}
