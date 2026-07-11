import assert from "node:assert/strict";
import test from "node:test";
import {
    providerStatusDetail,
    providerStatusLabel,
} from "../lib/provider-sync-status.ts";

const provider = (freshnessStatus, overrides = {}) => ({
    platform: "Steam",
    isConfigured: true,
    lastSuccessfulRefreshAt: "2030-02-03T04:00:00Z",
    freshUntil: "2030-02-03T04:30:00Z",
    freshnessStatus,
    cooldownUntil: null,
    ...overrides,
});

test("Provider readiness labels describe all closed status values", () => {
    assert.equal(providerStatusLabel("NotConfigured"), "Not configured");
    assert.equal(providerStatusLabel("NeverRefreshed"), "Ready for first refresh");
    assert.equal(providerStatusLabel("Fresh"), "Fresh");
    assert.equal(providerStatusLabel("Stale"), "Refresh recommended");
    assert.equal(providerStatusLabel("Cooldown"), "Cooldown");
});

test("Provider readiness text distinguishes refresh timing from cooldowns", () => {
    const format = (timestamp) => "at " + timestamp;

    assert.equal(
        providerStatusDetail(provider("Fresh"), format),
        "Fresh until at 2030-02-03T04:30:00Z.");
    assert.equal(
        providerStatusDetail(provider("Stale"), format),
        "Refresh was due at 2030-02-03T04:30:00Z.");
    assert.equal(
        providerStatusDetail(provider("Cooldown", { cooldownUntil: "2030-02-03T05:00:00Z" }), format),
        "Next refresh may start after at 2030-02-03T05:00:00Z.");
    assert.equal(
        providerStatusDetail(provider("NotConfigured", { isConfigured: false }), format),
        "Add this platform's credentials before it can refresh.");
});
