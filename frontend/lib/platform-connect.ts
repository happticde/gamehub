export type ProviderConnectResult =
    | { provider: "gog"; user: string; cookie: string; expired: boolean }
    | { provider: "psn"; cookies: Record<string, string>; expired: boolean }
    | { provider: "epic"; authorizationCode: string; expired: boolean };

export type HelperLaunchStatus = {
    status: "starting" | "handoff_ready" | "failed";
    handoffUrl?: string;
    message?: string;
};

export type EpicAuthorizationCodeResult = {
    authorizationCode: string;
};

export function parseGogConnectPayload(value: string): ProviderConnectResult | Error {
    const parsed = parseJsonObject(value, "Paste the JSON result returned by the local GOG helper.");
    if (parsed instanceof Error) return parsed;
    return parseGogConnectObject(parsed);
}

export function parsePsnConnectPayload(value: string): ProviderConnectResult | Error {
    const parsed = parseJsonObject(value, "Paste the JSON result returned by the local PlayStation helper.");
    if (parsed instanceof Error) return parsed;
    return parsePsnConnectObject(parsed);
}

export function parseEpicConnectPayload(value: string): ProviderConnectResult | Error {
    const parsed = parseJsonObject(value, "Paste the JSON result returned by the local Epic Games helper.");
    if (parsed instanceof Error) return parsed;
    return parseEpicConnectObject(parsed);
}

export function parseEpicAuthorizationCodePayload(value: string): EpicAuthorizationCodeResult | Error {
    const trimmed = value.trim();
    if (!trimmed) {
        return new Error("Paste the authorizationCode from the Epic page.");
    }

    if (!trimmed.startsWith("{")) {
        return { authorizationCode: trimmed };
    }

    const parsed = parseJsonObject(trimmed, "Paste the JSON result returned by the Epic page.");
    if (parsed instanceof Error) return parsed;
    const provider = readOptionalText(parsed.provider ?? parsed.platform);
    if (provider && provider.toLowerCase() !== "epic") {
        return new Error("This helper result is not for Epic Games.");
    }
    const status = readOptionalText(parsed.status ?? parsed.error)?.toLowerCase();
    if (isExpiredStatus(parsed, status)) {
        return new Error("The Epic Games authorization code is already expired. Reconnect Epic Games and try again.");
    }
    const authorizationCode = readOptionalText(parsed.authorizationCode ?? parsed.code);
    if (!authorizationCode) {
        return new Error("The Epic result is missing authorizationCode.");
    }
    return { authorizationCode };
}

export function parseGogConnectObject(parsed: Record<string, unknown>): ProviderConnectResult | Error {
    const provider = readOptionalText(parsed.provider ?? parsed.platform);
    if (provider && provider.toLowerCase() !== "gog") {
        return new Error("This helper result is not for GOG.");
    }

    const status = readOptionalText(parsed.status ?? parsed.error)?.toLowerCase();
    const user = readOptionalText(parsed.user ?? parsed.username);
    const cookie = readOptionalText(
        parsed.cookie ?? parsed.gogAl ?? parsed.gogAlCookie ?? parsed["gog-al"],
    )
        ?? readCookieValue(parsed.cookies, "gog-al");

    if (!user) {
        return new Error("The GOG helper result is missing the user field.");
    }
    if (!cookie) {
        return new Error("The GOG helper result is missing the gog-al cookie.");
    }

    return { provider: "gog", user, cookie, expired: isExpiredStatus(parsed, status) };
}

export function parsePsnConnectObject(parsed: Record<string, unknown>): ProviderConnectResult | Error {
    const provider = readOptionalText(parsed.provider ?? parsed.platform);
    if (provider && provider.toLowerCase() !== "psn" && provider.toLowerCase() !== "playstation") {
        return new Error("This helper result is not for PlayStation.");
    }

    const status = readOptionalText(parsed.status ?? parsed.error)?.toLowerCase();
    const cookies = readCookieMap(parsed.cookies);
    if (Object.keys(cookies).length === 0) {
        return new Error("The PlayStation helper result is missing the cookies object.");
    }

    return { provider: "psn", cookies, expired: isExpiredStatus(parsed, status) };
}

export function parseEpicConnectObject(parsed: Record<string, unknown>): ProviderConnectResult | Error {
    const provider = readOptionalText(parsed.provider ?? parsed.platform);
    if (provider && provider.toLowerCase() !== "epic") {
        return new Error("This helper result is not for Epic Games.");
    }

    const status = readOptionalText(parsed.status ?? parsed.error)?.toLowerCase();
    const authorizationCode = readOptionalText(parsed.authorizationCode ?? parsed.code);
    if (!authorizationCode) {
        return new Error("The Epic Games helper result is missing authorizationCode.");
    }

    return {
        provider: "epic",
        authorizationCode,
        expired: isExpiredStatus(parsed, status),
    };
}

export function parseHelperLaunchStatus(parsed: unknown): HelperLaunchStatus | Error {
    if (!isObjectRecord(parsed)) {
        return new Error("The local helper launch status response was not valid.");
    }

    const status = readOptionalText(parsed.status);
    if (status !== "starting" && status !== "handoff_ready" && status !== "failed") {
        return new Error("The local helper launch status response was not recognized.");
    }

    return {
        status,
        handoffUrl: readOptionalText(parsed.handoffUrl) ?? undefined,
        message: readOptionalText(parsed.message) ?? undefined,
    };
}

export function parseHandoffUrl(value: string, provider: "gog" | "psn" | "epic", pageOrigin: string): string | Error {
    const match = value.trim().match(/https?:\/\/\S+/);
    if (!match) {
        return new Error(`Paste the local handoff URL printed by the ${providerLabel(provider)} helper.`);
    }

    let url: URL;
    try {
        url = new URL(match[0]);
    } catch {
        return new Error("The local handoff URL is not valid.");
    }

    const isLoopbackHost = url.hostname === "127.0.0.1" || url.hostname === "localhost";
    if (url.protocol !== "http:" || !isLoopbackHost) {
        return new Error("The helper handoff URL must be an http://127.0.0.1 or http://localhost URL.");
    }
    if (url.pathname !== `/connect/${provider}/result` || !url.searchParams.get("nonce")) {
        return new Error(`The helper handoff URL does not match the ${providerLabel(provider)} loopback result endpoint.`);
    }

    url.searchParams.set("origin", pageOrigin);
    return url.toString();
}

function parseJsonObject(value: string, errorMessage: string): Record<string, unknown> | Error {
    let parsed: unknown;
    try {
        parsed = JSON.parse(value);
    } catch {
        return new Error(errorMessage);
    }

    if (!isObjectRecord(parsed)) {
        return new Error("The helper result must be a JSON object.");
    }
    return parsed;
}

function isExpiredStatus(parsed: Record<string, unknown>, status?: string): boolean {
    const expiresAt = readOptionalText(parsed.expiresAtUtc ?? parsed.expiresAt);
    return status === "session_expired"
        || status === "expired"
        || (expiresAt ? Date.parse(expiresAt) <= Date.now() : false);
}

function readCookieValue(value: unknown, name: string): string | null {
    if (!isObjectRecord(value)) return null;
    return readOptionalText(value[name]);
}

function readCookieMap(value: unknown): Record<string, string> {
    if (!isObjectRecord(value)) return {};

    const cookies: Record<string, string> = {};
    for (const [name, cookieValue] of Object.entries(value)) {
        const cookieName = readOptionalText(name);
        const textValue = readOptionalText(cookieValue);
        if (cookieName && textValue) {
            cookies[cookieName] = textValue;
        }
    }
    return cookies;
}

function readOptionalText(value: unknown): string | null {
    return typeof value === "string" && value.trim() ? value.trim() : null;
}

function isObjectRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === "object" && value !== null && !Array.isArray(value);
}

function providerLabel(provider: "gog" | "psn" | "epic"): string {
    if (provider === "gog") return "GOG";
    if (provider === "psn") return "PlayStation";
    return "Epic Games";
}
