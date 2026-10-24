"use client";

import { useEffect, useRef, useState } from "react";
import { createLatestRequestGate } from "@/lib/latest-request-gate";
import { fetchAuthenticated } from "@/lib/authenticated-fetch";
import type { ApiMessageResponse } from "@/types/api";
import type { MetadataSearchResult } from "@/types/game";

interface MetadataSearchState {
    results: MetadataSearchResult[];
    pending: boolean;
    error: string | null;
    completedQuery: string | null;
}

function emptySearch(inputKey: string): MetadataSearchState & { inputKey: string } {
    return { inputKey, results: [], pending: false, error: null, completedQuery: null };
}

/** Reuses the Fix Match endpoint with debounce, cancellation, and stale-response protection. */
export function useDebouncedMetadataSearch(query: string, enabled = true): MetadataSearchState {
    const gate = useRef(createLatestRequestGate());
    const inputKey = JSON.stringify([query, enabled]);
    const [state, setState] = useState(() => emptySearch(inputKey));

    // Reset input-owned state before commit, so old results never flash for a new query.
    if (state.inputKey !== inputKey) {
        setState(emptySearch(inputKey));
    }

    useEffect(() => {
        const normalizedQuery = query.trim();
        const requestGate = gate.current;
        requestGate.invalidate();
        if (!enabled || normalizedQuery.length < 2) {
            return;
        }

        const controller = new AbortController();
        const generation = requestGate.begin();
        const update = (changes: Partial<MetadataSearchState>) => {
            setState(current => current.inputKey === inputKey ? { ...current, ...changes } : current);
        };
        const timer = setTimeout(async () => {
            if (!requestGate.isLatest(generation)) return;
            update({ pending: true });

            try {
                const response = await fetchAuthenticated(
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
                    update({ results: data, completedQuery: normalizedQuery });
                }
            } catch (caughtError: unknown) {
                if (controller.signal.aborted || !requestGate.isLatest(generation)) return;
                update({
                    error: caughtError instanceof Error
                        ? caughtError.message
                        : "IGDB search is temporarily unavailable.",
                    completedQuery: normalizedQuery,
                });
            } finally {
                if (requestGate.isLatest(generation)) update({ pending: false });
            }
        }, 350);

        return () => {
            clearTimeout(timer);
            controller.abort();
            requestGate.invalidate();
        };
    }, [enabled, query, inputKey]);

    return state;
}

function readApiMessage(data: unknown, fallback: string) {
    if (!data || typeof data !== "object") return fallback;
    if ("message" in data && typeof data.message === "string") return data.message;
    if ("Message" in data && typeof data.Message === "string") return data.Message;
    return fallback;
}
