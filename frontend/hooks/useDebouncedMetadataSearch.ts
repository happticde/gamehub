"use client";

import { useEffect, useRef, useState } from "react";
import { createLatestRequestGate } from "@/lib/latest-request-gate";
import type { ApiMessageResponse } from "@/types/api";
import type { MetadataSearchResult } from "@/types/game";

interface MetadataSearchState {
    results: MetadataSearchResult[];
    pending: boolean;
    error: string | null;
    completedQuery: string | null;
}

/** Reuses the Fix Match endpoint with debounce, cancellation, and stale-response protection. */
export function useDebouncedMetadataSearch(query: string, enabled = true): MetadataSearchState {
    const gate = useRef(createLatestRequestGate());
    const [results, setResults] = useState<MetadataSearchResult[]>([]);
    const [pending, setPending] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [completedQuery, setCompletedQuery] = useState<string | null>(null);

    useEffect(() => {
        const normalizedQuery = query.trim();
        const requestGate = gate.current;
        requestGate.invalidate();
        setResults([]);
        setError(null);
        setCompletedQuery(null);
        setPending(false);

        if (!enabled || normalizedQuery.length < 2) {
            return;
        }

        const controller = new AbortController();
        const generation = requestGate.begin();
        const timer = setTimeout(async () => {
            if (!requestGate.isLatest(generation)) return;
            setPending(true);

            try {
                const response = await fetch(
                    `/api/core/library/enrichment/search?title=${encodeURIComponent(normalizedQuery)}&limit=10`,
                    { signal: controller.signal },
                );
                const data = await response.json().catch(() => null) as
                    | MetadataSearchResult[]
                    | ApiMessageResponse
                    | null;
                if (!response.ok || !Array.isArray(data)) {
                    throw new Error(readApiMessage(data, "IGDB search is temporarily unavailable."));
                }

                if (requestGate.isLatest(generation)) {
                    setResults(data);
                    setCompletedQuery(normalizedQuery);
                }
            } catch (caughtError: unknown) {
                if (controller.signal.aborted || !requestGate.isLatest(generation)) return;
                setError(caughtError instanceof Error
                    ? caughtError.message
                    : "IGDB search is temporarily unavailable.");
                setCompletedQuery(normalizedQuery);
            } finally {
                if (requestGate.isLatest(generation)) setPending(false);
            }
        }, 350);

        return () => {
            clearTimeout(timer);
            controller.abort();
            requestGate.invalidate();
        };
    }, [enabled, query]);

    return { results, pending, error, completedQuery };
}

function readApiMessage(data: unknown, fallback: string) {
    if (!data || typeof data !== "object") return fallback;
    if ("message" in data && typeof data.message === "string") return data.message;
    if ("Message" in data && typeof data.Message === "string") return data.Message;
    return fallback;
}
