import {
    createCipheriv,
    createDecipheriv,
    createHash,
    randomBytes,
    timingSafeEqual,
} from "node:crypto";

/**
 * Security primitives for the short-lived OIDC authorization transaction.
 *
 * This module intentionally contains no provider or HTTP logic. Keeping generation,
 * authenticated storage, and callback correlation as pure functions makes the protocol's
 * browser-facing trust boundary directly testable. The browser stores only the sealed result;
 * the plaintext PKCE verifier and nonce are available only inside the Next.js server runtime.
 */

export const OIDC_TRANSACTION_COOKIE = "gamehub_oidc_transaction";
export const OIDC_TRANSACTION_MAX_AGE_SECONDS = 10 * 60;
const TRANSACTION_AAD = Buffer.from("gamehub-oidc-transaction-v1", "utf8");

export type OidcIntent = "login" | "link";

/**
 * Server-owned state carried across the browser redirect to the identity provider.
 *
 * - `state` binds the callback to the browser transaction and prevents login CSRF.
 * - `nonce` binds the provider's ID token to this transaction and prevents token substitution.
 * - `codeVerifier` is the PKCE secret sent only to the token endpoint.
 * - `codeChallenge` is the public SHA-256 commitment sent in the authorization request.
 * - `redirectUri` is captured once so the callback cannot silently switch origin or path.
 * - `returnTo` is an already-sanitized application-local destination after authentication.
 * - `intent` distinguishes normal login from explicit identity linking.
 * - `createdAt` gives the otherwise stateless cookie a strict replay window.
 */
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

/**
 * Generates independent state, nonce, and RFC 7636 S256 PKCE values.
 *
 * Independence matters: reusing one random value for several protocol roles would couple their
 * security assumptions. Thirty-two random bytes provide 256 bits of entropy and become 43
 * unpadded Base64Url characters, which also satisfies PKCE's verifier length requirements.
 */
export function createOidcTransaction(
    redirectUri: string,
    returnTo: string,
    intent: OidcIntent,
    now = Date.now(),
): OidcTransaction {
    // The verifier never appears in the authorization URL. Only its one-way challenge does.
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

/**
 * Encrypts and authenticates correlation state so the browser can store but cannot read or alter
 * the verifier, nonce, intent, redirect, or return path.
 *
 * The compact cookie format is `v1.iv.ciphertext.tag`, with binary values encoded as Base64Url.
 * AES-GCM provides confidentiality and integrity together. A fresh 96-bit IV is mandatory for
 * every value encrypted with the same key; `randomBytes(12)` provides it. The fixed associated
 * data gives this ciphertext a protocol/type label so it cannot be reused as another encrypted
 * GameHub value even if a key were accidentally shared.
 */
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

/**
 * Opens and structurally validates the authenticated correlation cookie.
 *
 * Authentication happens before JSON is trusted. Any wrong key, changed byte, invalid tag,
 * unsupported version, malformed encoding, or unexpected field shape becomes the same generic
 * error. Collapsing those cases avoids turning the callback into a cryptographic error oracle.
 */
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
 * Verifies callback binding before exchanging the authorization code.
 *
 * Ordering is deliberate: no provider token request occurs until the callback proves that it
 * belongs to the initiating browser transaction, arrived at the exact configured callback, is
 * recent, and still contains the original PKCE verifier/challenge pair. The provider repeats the
 * final PKCE proof when it compares `code_verifier` with the challenge bound to the code.
 */
export function validateOidcCallback(
    transaction: OidcTransaction,
    state: string | null,
    actualRedirectUri: string,
    now = Date.now(),
): void {
    // `state` is public but unpredictable. Constant-time comparison avoids data-dependent exits.
    if (!state || !constantTimeEqual(transaction.state, state)) {
        throw new Error("invalid_oidc_state");
    }
    // State alone does not bind the callback host/path; compare the full configured redirect too.
    if (transaction.redirectUri !== actualRedirectUri) {
        throw new Error("invalid_oidc_redirect");
    }
    // Reject clock rollback as well as values older than the ten-minute correlation window.
    if (now < transaction.createdAt
        || now - transaction.createdAt > OIDC_TRANSACTION_MAX_AGE_SECONDS * 1000) {
        throw new Error("expired_oidc_transaction");
    }
    // The encrypted cookie should make mutation impossible; recomputation is defense in depth.
    if (!constantTimeEqual(pkceChallenge(transaction.codeVerifier), transaction.codeChallenge)) {
        throw new Error("invalid_oidc_pkce_verifier");
    }
}

/** Computes the RFC 7636 `S256` code challenge: BASE64URL(SHA256(code_verifier)). */
export function pkceChallenge(verifier: string): string {
    return createHash("sha256").update(verifier, "ascii").digest("base64url");
}

function constantTimeEqual(left: string, right: string): boolean {
    const leftBytes = Buffer.from(left, "utf8");
    const rightBytes = Buffer.from(right, "utf8");
    return leftBytes.length === rightBytes.length && timingSafeEqual(leftBytes, rightBytes);
}

function assertCookieKey(key: Buffer): void {
    // AES-256 means exactly 256 bits. Accepting arbitrary key lengths would hide bad deployment config.
    if (key.length !== 32) throw new Error("invalid_oidc_cookie_key");
}

function isOidcTransaction(value: Partial<OidcTransaction>): value is OidcTransaction {
    // Runtime validation is required because authenticated plaintext is still parsed from JSON.
    // Exact 43-character checks correspond to the 32-byte values generated above.
    return typeof value.state === "string" && value.state.length === 43
        && typeof value.nonce === "string" && value.nonce.length === 43
        && typeof value.codeVerifier === "string" && value.codeVerifier.length === 43
        && typeof value.codeChallenge === "string" && value.codeChallenge.length === 43
        && typeof value.redirectUri === "string" && value.redirectUri.startsWith("https://")
        && typeof value.returnTo === "string" && value.returnTo.startsWith("/")
        && (value.intent === "login" || value.intent === "link")
        && typeof value.createdAt === "number" && Number.isFinite(value.createdAt);
}
