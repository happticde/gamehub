"use client";

import { useCallback, useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { DatabaseZap, RefreshCw, RotateCcw } from "lucide-react";
import { getErrorMessage } from "@/lib/errors";
import { fetchAuthenticated } from "@/lib/authenticated-fetch";
import { providerDisplayName, providerRefreshBlocker } from "@/lib/provider-sync-status";
import type { ApiMessageResponse, LibrarySyncResult } from "@/types/api";
import type { ProviderSyncStatus, ProviderSyncStatusResponse } from "@/types/game";

const platformNames: Record<LibrarySyncResult["platforms"][number]["platform"], string> = {
    Steam: "Steam",
    Psn: "PlayStation",
    Gog: "GOG",
    Epic: "Epic",
};

const refreshLabels: Record<LibrarySyncResult["platforms"][number]["refreshStatus"], string> = {
    Cached: "cached only",
    Updated: "updated",
    SkippedFresh: "fresh",
    SkippedCooldown: "cooldown",
    NotConfigured: "not configured",
    Partial: "partially updated",
    Failed: "failed; cache used",
};

interface SyncTriggerButtonProps {
    onSyncComplete?: () => void | Promise<void>;
}

export function SyncTriggerButton({ onSyncComplete }: SyncTriggerButtonProps) {
    const [activeAction, setActiveAction] = useState<string | null>(null);
    const [resultMsg, setResultMsg] = useState("");
    const [platformResults, setPlatformResults] = useState<LibrarySyncResult["platforms"]>([]);
    const [providerStatuses, setProviderStatuses] = useState<ProviderSyncStatus[]>([]);
    const [readinessError, setReadinessError] = useState<string | null>(null);

    const fetchRefreshReadiness = useCallback(() => {
        return fetchAuthenticated("/api/core/library/sync/providers", { cache: "no-store" }).then(async (res) => {
            if (!res.ok) {
                setReadinessError("Refresh availability could not be checked.");
                return;
            }

            const response: ProviderSyncStatusResponse = await res.json();
            setProviderStatuses(response.providers);
            setReadinessError(null);
        }).catch(() => {
            setReadinessError("Refresh availability could not be checked.");
        });
    }, []);

    useEffect(() => {
        fetchRefreshReadiness();
    }, [fetchRefreshReadiness]);

    const handleSync = async (endpoint: string, action: string) => {
        setActiveAction(action);
        setResultMsg("");
        setPlatformResults([]);

        try {
            // Because this is a Client Component, we hit the Next.js API proxy route defined in next.config.ts
            // /api/core/ -> http://localhost:5001/api/
            const res = await fetchAuthenticated(endpoint, {
                method: "POST",
            });

            if (!res.ok) {
                const data: ApiMessageResponse | null = await res.json().catch(() => null);
                setResultMsg(data?.message || `Sync failed with status: ${res.status}`);
            } else {
                const data: LibrarySyncResult = await res.json();
                setResultMsg(data.message || "Sync completed successfully.");
                setPlatformResults(data.platforms);

                await Promise.all([onSyncComplete?.(), fetchRefreshReadiness()]);
            }
        } catch (caughtError: unknown) {
            setResultMsg(`Error: ${getErrorMessage(caughtError, "The sync request failed.")}`);
        } finally {
            setActiveAction(null);
        }
    };

    const refreshBlockers = providerStatuses
        .filter((provider) => !provider.isConfigured || provider.cooldownUntil)
        .map((provider) => ({
            platform: providerDisplayName(provider.platform),
            message: providerRefreshBlocker(provider, formatTimestamp),
        }));

    return (
        <div className="flex w-full flex-col items-start gap-3 md:w-auto md:items-end">
            <div className="flex w-full flex-col gap-2 sm:flex-row md:w-auto">
                <Button
                    onClick={() => handleSync("/api/core/library/sync", "cached")}
                    disabled={activeAction !== null}
                    size="lg"
                >
                    <DatabaseZap className={`mr-2 h-5 w-5 ${activeAction === "cached" ? "animate-pulse" : ""}`} />
                    {activeAction === "cached" ? "Syncing cached library..." : "Sync Cached Library"}
                </Button>
                <Button
                    onClick={() => handleSync("/api/core/library/sync/refresh", "refresh")}
                    disabled={activeAction !== null}
                    variant="secondary"
                    size="lg"
                >
                    <RefreshCw className={`mr-2 h-5 w-5 ${activeAction === "refresh" ? "animate-spin" : ""}`} />
                    {activeAction === "refresh" ? "Refreshing platforms..." : "Refresh Platforms + Sync"}
                </Button>
                <Button
                    onClick={() => handleSync("/api/core/library/sync/refresh/force", "force")}
                    disabled={activeAction !== null}
                    variant="outline"
                    size="lg"
                >
                    <RotateCcw className={`mr-2 h-5 w-5 ${activeAction === "force" ? "animate-spin" : ""}`} />
                    {activeAction === "force" ? "Forcing refresh..." : "Force Refresh"}
                </Button>
            </div>

            {readinessError ? (
                <p className="text-sm text-muted-foreground">{readinessError} Cached sync is still available.</p>
            ) : refreshBlockers.length > 0 ? (
                <div className="w-full space-y-1 text-sm text-muted-foreground md:text-right">
                    <p className="font-medium text-foreground">Before refreshing platforms:</p>
                    {refreshBlockers.map((blocker) => (
                        <p key={blocker.platform}>
                            {blocker.platform}: {blocker.message}
                        </p>
                    ))}
                </div>
            ) : null}

            {resultMsg && (
                <div className="space-y-2 text-sm text-muted-foreground animate-in fade-in duration-300 md:text-right">
                    <p>{resultMsg}</p>
                    {platformResults.length > 0 && (
                        <p>
                            {platformResults.map((result) =>
                                `${platformNames[result.platform]}: ${refreshLabels[result.refreshStatus]}`
                            ).join(" · ")}
                        </p>
                    )}
                </div>
            )}
        </div>
    );
}

function formatTimestamp(iso: string | null): string {
    if (!iso) return "soon";
    return new Date(iso).toLocaleString(undefined, {
        month: "short",
        day: "numeric",
        hour: "2-digit",
        minute: "2-digit",
    });
}
