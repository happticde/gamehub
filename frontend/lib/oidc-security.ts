import {
    createCipheriv,
    createDecipheriv,
    createHash,
    randomBytes,
    timingSafeEqual,
} from "node:crypto";

export const OIDC_TRANSACTION_COOKIE = "gamehub_oidc_transaction";
export const OIDC_TRANSACTION_MAX_AGE_SECONDS = 10 * 60;
const TRANSACTION_AAD = Buffer.from("gamehub-oidc-transaction-v1", "utf8");

export type OidcIntent = "login" | "link";

export interface OidcTransaction {
    state: string;
    nonce: string;
    codeVerifier: string;
    codeChallenge: string;
    redirectUri: string;
    returnTo: string;
    intent: OidcIntent;
    createdAt: number;
}

/** Generates independent state, nonce, and RFC 7636 S256 PKCE values. */
export function createOidcTransaction(
    redirectUri: string,
    returnTo: string,
    intent: OidcIntent,
    now = Date.now(),
): OidcTransaction {
    const codeVerifier = randomBytes(32).toString("base64url");
    return {
        state: randomBytes(32).toString("base64url"),
        nonce: randomBytes(32).toString("base64url"),
        codeVerifier,
        codeChallenge: pkceChallenge(codeVerifier),
        redirectUri,
        returnTo,
        intent,
        createdAt: now,
    };
}

/** Encrypts and authenticates correlation state so the browser cannot alter the verifier. */
export function sealOidcTransaction(transaction: OidcTransaction, key: Buffer): string {
    assertCookieKey(key);
    const iv = randomBytes(12);
    const cipher = createCipheriv("aes-256-gcm", key, iv, { authTagLength: 16 });
    cipher.setAAD(TRANSACTION_AAD);
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

/** Opens and structurally validates the authenticated correlation cookie. */
export function openOidcTransaction(value: string, key: Buffer): OidcTransaction {
    assertCookieKey(key);
    const [version, encodedIv, encodedCiphertext, encodedTag, extra] = value.split(".");
    if (version !== "v1" || !encodedIv || !encodedCiphertext || !encodedTag || extra) {
        throw new Error("invalid_oidc_transaction");
    }

    try {
        const iv = Buffer.from(encodedIv, "base64url");
        const tag = Buffer.from(encodedTag, "base64url");
        if (iv.length !== 12 || tag.length !== 16) throw new Error("invalid_oidc_transaction");
        const decipher = createDecipheriv("aes-256-gcm", key, iv, { authTagLength: 16 });
        decipher.setAAD(TRANSACTION_AAD);
        decipher.setAuthTag(tag);
        const plaintext = Buffer.concat([
            decipher.update(Buffer.from(encodedCiphertext, "base64url")),
            decipher.final(),
        ]).toString("utf8");
        const transaction = JSON.parse(plaintext) as Partial<OidcTransaction>;
        if (!isOidcTransaction(transaction)) throw new Error("invalid_oidc_transaction");
        return transaction;
    } catch {
        throw new Error("invalid_oidc_transaction");
    }
}

/**
 * Verifies callback binding before exchanging the authorization code. A mismatched state,
 * redirect URI, expired transaction, or altered PKCE verifier is rejected locally.
 */
export function validateOidcCallback(
    transaction: OidcTransaction,
    state: string | null,
    actualRedirectUri: string,
    now = Date.now(),
): void {
    if (!state || !constantTimeEqual(transaction.state, state)) {
        throw new Error("invalid_oidc_state");
    }
    if (transaction.redirectUri !== actualRedirectUri) {
        throw new Error("invalid_oidc_redirect");
    }
    if (now < transaction.createdAt
        || now - transaction.createdAt > OIDC_TRANSACTION_MAX_AGE_SECONDS * 1000) {
        throw new Error("expired_oidc_transaction");
    }
    if (!constantTimeEqual(pkceChallenge(transaction.codeVerifier), transaction.codeChallenge)) {
        throw new Error("invalid_oidc_pkce_verifier");
    }
}

export function pkceChallenge(verifier: string): string {
    return createHash("sha256").update(verifier, "ascii").digest("base64url");
}

function constantTimeEqual(left: string, right: string): boolean {
    const leftBytes = Buffer.from(left, "utf8");
    const rightBytes = Buffer.from(right, "utf8");
    return leftBytes.length === rightBytes.length && timingSafeEqual(leftBytes, rightBytes);
}

function assertCookieKey(key: Buffer): void {
    if (key.length !== 32) throw new Error("invalid_oidc_cookie_key");
}

function isOidcTransaction(value: Partial<OidcTransaction>): value is OidcTransaction {
    return typeof value.state === "string" && value.state.length === 43
        && typeof value.nonce === "string" && value.nonce.length === 43
        && typeof value.codeVerifier === "string" && value.codeVerifier.length === 43
        && typeof value.codeChallenge === "string" && value.codeChallenge.length === 43
        && typeof value.redirectUri === "string" && value.redirectUri.startsWith("https://")
        && typeof value.returnTo === "string" && value.returnTo.startsWith("/")
        && (value.intent === "login" || value.intent === "link")
        && typeof value.createdAt === "number" && Number.isFinite(value.createdAt);
}
