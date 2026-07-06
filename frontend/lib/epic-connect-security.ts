import {
    createCipheriv,
    createDecipheriv,
    randomBytes,
    timingSafeEqual,
} from "node:crypto";

export const EPIC_CONNECT_TRANSACTION_COOKIE = "gamehub_epic_connect";
export const EPIC_CONNECT_MAX_AGE_SECONDS = 10 * 60;

const EPIC_CONNECT_AAD = Buffer.from("gamehub-epic-connect-v1", "utf8");
const EPIC_LAUNCHER_CLIENT_ID = "34a02cf8f4414e29b15921876da36f9a";
const EPIC_REDIRECT_API_URL =
    `https://www.epicgames.com/id/api/redirect?clientId=${EPIC_LAUNCHER_CLIENT_ID}&responseType=code`;

export interface EpicConnectTransaction {
    state: string;
    createdAt: number;
}

export function createEpicConnectTransaction(now = Date.now()): EpicConnectTransaction {
    return {
        state: randomBytes(32).toString("base64url"),
        createdAt: now,
    };
}

export function getEpicAuthorizationUrl(): string {
    const url = new URL("https://www.epicgames.com/id/login");
    url.searchParams.set("redirectUrl", EPIC_REDIRECT_API_URL);
    return url.toString();
}

export function sealEpicConnectTransaction(
    transaction: EpicConnectTransaction,
    key: Buffer,
): string {
    assertCookieKey(key);
    const iv = randomBytes(12);
    const cipher = createCipheriv("aes-256-gcm", key, iv, { authTagLength: 16 });
    cipher.setAAD(EPIC_CONNECT_AAD);
    const ciphertext = Buffer.concat([
        cipher.update(JSON.stringify(transaction), "utf8"),
        cipher.final(),
    ]);
    return [
        "v1",
        iv.toString("base64url"),
        ciphertext.toString("base64url"),
        cipher.getAuthTag().toString("base64url"),
    ].join(".");
}

export function openEpicConnectTransaction(value: string, key: Buffer): EpicConnectTransaction {
    assertCookieKey(key);
    const [version, encodedIv, encodedCiphertext, encodedTag, extra] = value.split(".");
    if (version !== "v1" || !encodedIv || !encodedCiphertext || !encodedTag || extra) {
        throw new Error("invalid_epic_connect_transaction");
    }

    try {
        const iv = Buffer.from(encodedIv, "base64url");
        const tag = Buffer.from(encodedTag, "base64url");
        if (iv.length !== 12 || tag.length !== 16) {
            throw new Error("invalid_epic_connect_transaction");
        }
        const decipher = createDecipheriv("aes-256-gcm", key, iv, { authTagLength: 16 });
        decipher.setAAD(EPIC_CONNECT_AAD);
        decipher.setAuthTag(tag);
        const plaintext = Buffer.concat([
            decipher.update(Buffer.from(encodedCiphertext, "base64url")),
            decipher.final(),
        ]).toString("utf8");
        const transaction = JSON.parse(plaintext) as Partial<EpicConnectTransaction>;
        if (!isEpicConnectTransaction(transaction)) {
            throw new Error("invalid_epic_connect_transaction");
        }
        return transaction;
    } catch {
        throw new Error("invalid_epic_connect_transaction");
    }
}

export function validateEpicConnectState(
    transaction: EpicConnectTransaction,
    state: string | null,
    now = Date.now(),
): void {
    if (!state || !constantTimeEqual(transaction.state, state)) {
        throw new Error("invalid_epic_connect_state");
    }
    if (now < transaction.createdAt
        || now - transaction.createdAt > EPIC_CONNECT_MAX_AGE_SECONDS * 1000) {
        throw new Error("expired_epic_connect_transaction");
    }
}

function isEpicConnectTransaction(
    value: Partial<EpicConnectTransaction>,
): value is EpicConnectTransaction {
    return typeof value.state === "string" && value.state.length === 43
        && typeof value.createdAt === "number" && Number.isFinite(value.createdAt);
}

function constantTimeEqual(left: string, right: string): boolean {
    const leftBytes = Buffer.from(left, "utf8");
    const rightBytes = Buffer.from(right, "utf8");
    return leftBytes.length === rightBytes.length && timingSafeEqual(leftBytes, rightBytes);
}

function assertCookieKey(key: Buffer): void {
    if (key.length !== 32) throw new Error("invalid_epic_connect_cookie_key");
}
