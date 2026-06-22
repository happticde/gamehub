/** Pure guarded-enablement parsing kept separate so fail-closed behavior is directly testable. */
export function parseMultiUserMode(configured: string | undefined): boolean {
    const value = configured?.trim().toLowerCase();
    if (!value) return false;
    if (value === "true") return true;
    if (value === "false") return false;
    throw new Error("invalid_multi_user_enabled");
}

/** Validates the browser-facing prerequisites without returning any configured secret value. */
export function validateMultiUserPrerequisites(
    enabled: boolean,
    frontendAuthEnabled: boolean,
    secureCookiesEnabled: boolean,
    oidcEnabled: boolean,
): void {
    if (!enabled) return;
    if (!frontendAuthEnabled) throw new Error("multi_user_requires_frontend_auth");
    if (!secureCookiesEnabled) throw new Error("multi_user_requires_secure_cookies");
    if (!oidcEnabled) throw new Error("multi_user_requires_oidc");
}
