export type Platform = "Steam" | "Psn" | "Gog" | "Epic";

export interface Tag {
    id: number;
    name: string;
}

export interface LibraryItem {
    id: number;
    unifiedGameId: number;
    platform: Platform;
    externalId: string;
    lastSynced: string; // ISO 8601 string
}

export type PlayStatus = "Unplayed" | "Played" | "Playing" | "Completed" | "Abandoned";

export interface UnifiedGame {
    id: number;
    title: string;
    slug: string;
    platforms: LibraryItem[];

    // Personal Tracking
    playStatus: PlayStatus;
    userRating?: number | null;

    // Metadata (may be null if not yet enriched)
    coverUrl?: string | null;
    summary?: string | null;
    genres?: string | null;
    developers?: string | null;
    publishers?: string | null;
    firstReleaseDate?: string | null;
    rating?: number | null;
    metadataProviderId?: number | null;
    metadataSource?: string | null;
    enrichedAt?: string | null;
    tags?: Tag[];
}

export interface PagedResult<T> {
    items: T[];
    totalCount: number;
    page: number;
    pageSize: number;
}

// =========================================================================
// Admin Dashboard Types
// =========================================================================

/** Aggregated enrichment counts returned by GET /api/library/enrichment-stats */
export interface EnrichmentStats {
    totalGames: number;
    enrichedCount: number;
    unenrichedCount: number;
    errorCount: number;
    notFoundCount: number;
    lastEnrichedAt: string | null;
}

/** Single row from GET /api/library/enrichment-history */
export interface EnrichmentAuditLog {
    id: number;
    unifiedGameId: number;
    gameTitle: string;
    timestamp: string;
    providerName: string;
    status: string;
    errorMessage: string | null;
    providerGameId: string | null;
    matchedTitle: string | null;
}

/** Single row from GET /api/ingest/retrieve/history */
export interface IngestAuditLog {
    id: number;
    timestamp: string;
    platform: string;
    status: string;
    itemsProcessed: number;
    errorMessage: string | null;
}

/** Single row from GET /api/library/sync-history. */
export interface SyncAuditLog {
    id: number;
    timestamp: string;
    platform: Platform;
    status: "Success" | "Failed" | "Skipped" | "In_Progress";
    itemsProcessed: number;
    newGamesCreated: number;
    errorMessage: string | null;
}

/** Secret-free state returned by Core for one encrypted platform credential. */
export interface PlatformCredentialStatus {
    platform: Platform;
    isConfigured: boolean;
    schemaVersion: number | null;
    updatedAtUtc: string | null;
}

/** Credential status plus Core's server-derived sync activation state. */
export interface PlatformCredentialManagement {
    platformSyncEnabled: boolean;
    credentials: PlatformCredentialStatus[];
}

/** One actionable provider-refresh state from GET /api/library/sync/providers. */
export interface ProviderSyncStatus {
    platform: Platform;
    isConfigured: boolean;
    cooldownUntil: string | null;
}

/** Secret-free provider-refresh blockers returned by GET /api/library/sync/providers. */
export interface ProviderSyncStatusResponse {
    providers: ProviderSyncStatus[];
}

/** Comprehensive stats payload from GET /api/library/stats */
export interface LibraryStats {
    totalGames: number;
    enrichedCount: number;
    totalPlatformLinks: number;
    multiPlatformCount: number;
    averageRating: number;
    platformCounts: { platform: string; count: number }[];
    genreCounts: { name: string; count: number }[];
    playStatusCounts: { status: PlayStatus; count: number }[];
    ratingDistribution: { bucket: string; count: number }[];
    topRated: {
        id: number;
        title: string;
        coverUrl: string | null;
        rating: number;
        genres: string | null;
    }[];
    recentlyAdded: {
        id: number;
        title: string;
        coverUrl: string | null;
        enrichedAt: string;
        platforms: string[];
    }[];
}

/** Record counts returned by the backup validation and restore APIs. */
export interface BackupSummary {
    gameCount: number;
    platformItemCount: number;
    manualLinkCount: number;
    tagCount: number;
}

/** Machine-readable issue returned for an invalid backup upload. */
export interface BackupValidationError {
    code: string;
    message: string;
    path: string | null;
}

/** Response from POST /api/backup/validate. */
export interface BackupValidationResult {
    isValid: boolean;
    format: string | null;
    formatVersion: number | null;
    summary: BackupSummary;
    errors: BackupValidationError[];
}

/** Response from POST /api/backup/restore. */
export interface BackupRestoreResult {
    restored: boolean;
    message: string;
    summary: BackupSummary;
}
