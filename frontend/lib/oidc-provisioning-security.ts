/** Stable browser-facing representation of Core's immutable OIDC provisioning policy. */
export type OidcProvisioningMode = "Disabled" | "PersonalTenantJit";

/** Missing configuration is disabled; any unknown non-empty value fails closed. */
export function parseOidcProvisioningMode(
    configured: string | undefined,
): OidcProvisioningMode {
    const value = configured?.trim().toLowerCase();
    if (!value || value === "disabled") return "Disabled";
    if (value === "personaltenantjit") return "PersonalTenantJit";
    throw new Error("invalid_oidc_provisioning_mode");
}
