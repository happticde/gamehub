import assert from "node:assert/strict";
import test from "node:test";
import {
    providerDisplayName,
    providerRefreshBlocker,
} from "../lib/provider-sync-status.ts";

const provider = (overrides = {}) => ({
    platform: "Steam",
    isConfigured: true,
    cooldownUntil: null,
    ...overrides,
});

test("Provider refresh blockers omit providers that are ready", () => {
    assert.equal(providerRefreshBlocker(provider(), (value) => value), "");
});

test("Provider refresh blockers explain only missing credentials and cooldowns", () => {
    const format = (timestamp) => "at " + timestamp;

    assert.equal(
        providerRefreshBlocker(provider({ cooldownUntil: "2030-02-03T05:00:00Z" }), format),
        "Provider cooldown until at 2030-02-03T05:00:00Z.");
    assert.equal(
        providerRefreshBlocker(provider({ isConfigured: false }), format),
        "Credentials are not configured.");
    assert.equal(providerDisplayName("Psn"), "PlayStation");
});
