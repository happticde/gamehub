import type { Platform, ProviderSyncStatus } from "@/types/game";

/** Returns only the actionable local blocker for a provider refresh, when one exists. */
export function providerRefreshBlocker(
    provider: ProviderSyncStatus,
    formatTimestamp: (value: string | null) => string): string {
    if (!provider.isConfigured) {
        return "Credentials are not configured.";
    }

    return provider.cooldownUntil
        ? "Provider cooldown until " + formatTimestamp(provider.cooldownUntil) + "."
        : "";
}

/** Formats platform names consistently in compact sync-action messages. */
export const providerDisplayName = (platform: Platform): string =>
    platform === "Psn" ? "PlayStation" : platform;
