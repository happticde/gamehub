import assert from "node:assert/strict";
import test from "node:test";
import { isAccountDeletionTokenResponse } from "../lib/auth-token-security.ts";
import { parseOidcProvisioningMode } from "../lib/oidc-provisioning-security.ts";

test("OIDC provisioning mode is disabled when configuration is missing", () => {
    assert.equal(parseOidcProvisioningMode(undefined), "Disabled");
});

test("unknown OIDC provisioning mode fails closed", () => {
    assert.throws(
        () => parseOidcProvisioningMode("automatic"),
        /invalid_oidc_provisioning_mode/,
    );
});

test("personal tenant JIT mode is parsed only from its exact stable label", () => {
    assert.equal(parseOidcProvisioningMode("PersonalTenantJit"), "PersonalTenantJit");
});

test("account deletion token response accepts only the restricted purpose and no refresh token", () => {
    const valid = {
        accessToken: "restricted-token",
        tokenType: "Bearer",
        expiresAtUtc: "2026-06-24T10:10:00Z",
        refreshToken: null,
        refreshTokenExpiresAtUtc: null,
        sessionPurpose: "account_deletion_only",
    };

    assert.equal(isAccountDeletionTokenResponse(valid), true);
    assert.equal(isAccountDeletionTokenResponse({ ...valid, sessionPurpose: "full" }), false);
    assert.equal(isAccountDeletionTokenResponse({ ...valid, refreshToken: "forbidden" }), false);
});
