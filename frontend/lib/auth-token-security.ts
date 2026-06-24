/** Short-lived Core token accepted exclusively by DELETE /api/auth/account. */
export interface CoreAccountDeletionTokenResponse {
    accessToken: string;
    tokenType: "Bearer";
    expiresAtUtc: string;
    refreshToken: null;
    refreshTokenExpiresAtUtc: null;
    sessionPurpose: "account_deletion_only";
}

/** Runtime validation for the external reauthentication deletion-only response. */
export function isAccountDeletionTokenResponse(
    value: unknown,
): value is CoreAccountDeletionTokenResponse {
    if (!value || typeof value !== "object") return false;
    const token = value as Partial<CoreAccountDeletionTokenResponse>;
    return typeof token.accessToken === "string"
        && token.tokenType === "Bearer"
        && typeof token.expiresAtUtc === "string"
        && token.refreshToken === null
        && token.refreshTokenExpiresAtUtc === null
        && token.sessionPurpose === "account_deletion_only";
}
