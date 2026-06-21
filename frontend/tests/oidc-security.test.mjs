import assert from "node:assert/strict";
import { randomBytes } from "node:crypto";
import test from "node:test";
import {
    createOidcTransaction,
    openOidcTransaction,
    sealOidcTransaction,
    validateOidcCallback,
} from "../lib/oidc-security.ts";

const redirectUri = "https://gamehub.example.test/api/auth/oidc/callback";
const createdAt = Date.now();

test("encrypted transaction round-trips exact state, nonce, and PKCE verifier", () => {
    const key = randomBytes(32);
    const transaction = createOidcTransaction(redirectUri, "/library", "login", createdAt);

    const opened = openOidcTransaction(sealOidcTransaction(transaction, key), key);

    assert.deepEqual(opened, transaction);
    validateOidcCallback(opened, transaction.state, redirectUri, createdAt + 1_000);
});

test("wrong state is rejected", () => {
    const transaction = createOidcTransaction(redirectUri, "/library", "login", createdAt);

    assert.throws(
        () => validateOidcCallback(transaction, "wrong-state", redirectUri, createdAt + 1_000),
        /invalid_oidc_state/,
    );
});

test("wrong callback redirect is rejected", () => {
    const transaction = createOidcTransaction(redirectUri, "/library", "login", createdAt);

    assert.throws(
        () => validateOidcCallback(
            transaction,
            transaction.state,
            "https://attacker.example.test/api/auth/oidc/callback",
            createdAt + 1_000,
        ),
        /invalid_oidc_redirect/,
    );
});

test("wrong PKCE verifier is rejected before token exchange", () => {
    const transaction = createOidcTransaction(redirectUri, "/library", "login", createdAt);
    transaction.codeVerifier = randomBytes(32).toString("base64url");

    assert.throws(
        () => validateOidcCallback(transaction, transaction.state, redirectUri, createdAt + 1_000),
        /invalid_oidc_pkce_verifier/,
    );
});

test("tampered correlation cookie is rejected", () => {
    const key = randomBytes(32);
    const transaction = createOidcTransaction(redirectUri, "/library", "login", createdAt);
    const sealed = sealOidcTransaction(transaction, key);
    const replacement = sealed.endsWith("A") ? "B" : "A";

    assert.throws(
        () => openOidcTransaction(`${sealed.slice(0, -1)}${replacement}`, key),
        /invalid_oidc_transaction/,
    );
});

test("expired transaction is rejected", () => {
    const transaction = createOidcTransaction(redirectUri, "/library", "login", createdAt);

    assert.throws(
        () => validateOidcCallback(transaction, transaction.state, redirectUri, createdAt + 601_000),
        /expired_oidc_transaction/,
    );
});
