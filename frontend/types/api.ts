/** Message payload returned by Core actions that use either .NET or JSON naming. */
export interface ApiMessageResponse {
    Message?: string;
    message?: string;
}

/** Result returned after applying the retroactive library ignore rules. */
export interface RetroactiveIgnoreResponse {
    newlyIgnoredCount: number;
}

/** Secret-free provider outcome returned by a Core library sync. */
export interface PlatformSyncResult {
    platform: "Steam" | "Psn" | "Gog" | "Epic";
    refreshStatus: "Cached" | "Updated" | "SkippedFresh" | "SkippedCooldown" | "NotConfigured" | "Partial" | "Failed";
    itemsProcessed: number;
    detail: string | null;
}

/** Result of cached, refresh-if-stale, or forced-refresh synchronization. */
export interface LibrarySyncResult {
    mode: "Cached" | "RefreshIfStale" | "ForceRefresh";
    processed: number;
    platforms: PlatformSyncResult[];
    message: string;
}
