import assert from "node:assert/strict";
import test from "node:test";
import {
    DEFAULT_ADMIN_TAB,
    getAdminTab,
} from "../lib/admin-tabs.ts";

test("Admin tab selection preserves every supported URL value", () => {
    for (const tab of [
        "enrichment",
        "credentials",
        "ingestion",
        "sync",
        "activity",
        "security",
        "backup",
    ]) {
        assert.equal(getAdminTab(tab), tab);
    }
});

test("Admin tab selection falls back to the first tab", () => {
    assert.equal(DEFAULT_ADMIN_TAB, "enrichment");
    assert.equal(getAdminTab(null), DEFAULT_ADMIN_TAB);
    assert.equal(getAdminTab("unknown"), DEFAULT_ADMIN_TAB);
});
