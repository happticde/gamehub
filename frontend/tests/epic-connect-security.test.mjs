import assert from "node:assert/strict";
import { randomBytes } from "node:crypto";
import test from "node:test";
import {
    EPIC_CONNECT_MAX_AGE_SECONDS,
    createEpicConnectTransaction,
    getEpicAuthorizationUrl,
    openEpicConnectTransaction,
    sealEpicConnectTransaction,
    validateEpicConnectState,
} from "../lib/epic-connect-security.ts";

test("Epic connect transaction round-trips with exact state", () => {
    const key = randomBytes(32);
    const createdAt = Date.parse("2026-07-06T12:00:00Z");
    const transaction = createEpicConnectTransaction(createdAt);
    const opened = openEpicConnectTransaction(
        sealEpicConnectTransaction(transaction, key),
        key,
    );

    assert.deepEqual(opened, transaction);
    validateEpicConnectState(opened, transaction.state, createdAt + 1_000);
});

test("Epic connect rejects wrong state", () => {
    const transaction = createEpicConnectTransaction();

    assert.throws(
        () => validateEpicConnectState(transaction, "wrong-state"),
        /invalid_epic_connect_state/,
    );
});

test("Epic connect rejects expired transactions", () => {
    const createdAt = Date.parse("2026-07-06T12:00:00Z");
    const transaction = createEpicConnectTransaction(createdAt);

    assert.throws(
        () => validateEpicConnectState(
            transaction,
            transaction.state,
            createdAt + (EPIC_CONNECT_MAX_AGE_SECONDS + 1) * 1000,
        ),
        /expired_epic_connect_transaction/,
    );
});

test("Epic authorization URL uses launcher redirect endpoint", () => {
    const url = new URL(getEpicAuthorizationUrl());
    const redirectUrl = new URL(url.searchParams.get("redirectUrl"));

    assert.equal(url.origin, "https://www.epicgames.com");
    assert.equal(url.pathname, "/id/login");
    assert.equal(redirectUrl.origin, "https://www.epicgames.com");
    assert.equal(redirectUrl.pathname, "/id/api/redirect");
    assert.equal(redirectUrl.searchParams.get("responseType"), "code");
});
