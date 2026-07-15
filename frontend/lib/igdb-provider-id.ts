const MAX_DOTNET_INT = 2_147_483_647;

/** Parses a user-entered IGDB ID into the positive Int32 accepted by the Core API. */
export function parseIgdbProviderId(value: string): number | null {
    const normalized = value.trim();
    if (!/^[1-9]\d*$/.test(normalized)) return null;

    const providerId = Number(normalized);
    return Number.isSafeInteger(providerId) && providerId <= MAX_DOTNET_INT
        ? providerId
        : null;
}
