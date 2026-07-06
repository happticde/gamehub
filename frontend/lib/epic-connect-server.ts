import "server-only";

import { randomBytes } from "node:crypto";

let processLocalCookieKey: Buffer | null = null;

export function getEpicConnectCookieKey(): Buffer {
    const encodedKey = process.env.EPIC_CONNECT_COOKIE_KEY?.trim()
        || process.env.OIDC_COOKIE_KEY?.trim()
        || "";
    if (encodedKey) {
        const key = Buffer.from(encodedKey, "base64");
        if (key.length !== 32 || key.toString("base64") !== encodedKey) {
            throw new Error("invalid_epic_connect_cookie_key");
        }
        return key;
    }

    processLocalCookieKey ??= randomBytes(32);
    return processLocalCookieKey;
}

export function shouldUseSecureCookies(): boolean {
    return process.env.AUTH_COOKIE_SECURE?.toLowerCase() === "true";
}
