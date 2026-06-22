import assert from "node:assert/strict";
import test from "node:test";
import {
    parseMultiUserMode,
    validateMultiUserPrerequisites,
} from "../lib/multi-user-security.ts";

test("missing enablement switch defaults to disabled even when OIDC is ready", () => {
    const enabled = parseMultiUserMode(undefined);

    validateMultiUserPrerequisites(enabled, true, true, true);
    assert.equal(enabled, false);
});

test("invalid enablement switch fails closed", () => {
    assert.throws(() => parseMultiUserMode("enabled"), /invalid_multi_user_enabled/);
});

test("enablement switch alone cannot bypass missing BFF prerequisites", () => {
    assert.throws(
        () => validateMultiUserPrerequisites(true, false, true, true),
        /multi_user_requires_frontend_auth/,
    );
    assert.throws(
        () => validateMultiUserPrerequisites(true, true, false, true),
        /multi_user_requires_secure_cookies/,
    );
    assert.throws(
        () => validateMultiUserPrerequisites(true, true, true, false),
        /multi_user_requires_oidc/,
    );
});

test("complete prerequisites permit the separately enabled mode", () => {
    assert.doesNotThrow(() => validateMultiUserPrerequisites(true, true, true, true));
});
