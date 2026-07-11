import type { ProviderSyncStatus } from "@/types/game";

/** Returns a short, non-alarmist label for a local provider readiness state. */
export function providerStatusLabel(status: ProviderSyncStatus["freshnessStatus"]): string {
    switch (status) {
        case "NotConfigured":
            return "Not configured";
        case "NeverRefreshed":
            return "Ready for first refresh";
        case "Fresh":
            return "Fresh";
        case "Stale":
            return "Refresh recommended";
        case "Cooldown":
            return "Cooldown";
    }
}

/** Explains only the local GameHub state without implying an external health check. */
export function providerStatusDetail(
    provider: ProviderSyncStatus,
    formatTimestamp: (value: string | null) => string): string {
    switch (provider.freshnessStatus) {
        case "NotConfigured":
            return "Add this platform's credentials before it can refresh.";
        case "NeverRefreshed":
            return "No successful provider refresh has been recorded yet.";
        case "Fresh":
            return "Fresh until " + formatTimestamp(provider.freshUntil) + ".";
        case "Stale":
            return provider.freshUntil
                ? "Refresh was due " + formatTimestamp(provider.freshUntil) + "."
                : "A provider refresh is due.";
        case "Cooldown":
            return provider.cooldownUntil
                ? "Next refresh may start after " + formatTimestamp(provider.cooldownUntil) + "."
                : "A provider cooldown is active.";
    }
}
