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

export interface UnifiedGame {
    id: number;
    title: string;
    slug: string;
    platforms: LibraryItem[];

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
