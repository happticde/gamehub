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
