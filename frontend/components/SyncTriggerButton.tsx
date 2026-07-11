"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { DatabaseZap, RefreshCw, RotateCcw } from "lucide-react";
import { getErrorMessage } from "@/lib/errors";
import type { ApiMessageResponse, LibrarySyncResult } from "@/types/api";

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

export function SyncTriggerButton() {
    const router = useRouter();
    const [isPending, startTransition] = useTransition();
    const [activeAction, setActiveAction] = useState<string | null>(null);
    const [resultMsg, setResultMsg] = useState("");
    const [platformResults, setPlatformResults] = useState<LibrarySyncResult["platforms"]>([]);

    const handleSync = async (endpoint: string, action: string) => {
        setActiveAction(action);
        setResultMsg("");
        setPlatformResults([]);

        try {
            // Because this is a Client Component, we hit the Next.js API proxy route defined in next.config.ts
            // /api/core/ -> http://localhost:5001/api/
            const res = await fetch(endpoint, {
                method: "POST",
            });

            if (!res.ok) {
                const data: ApiMessageResponse | null = await res.json().catch(() => null);
                setResultMsg(data?.message || `Sync failed with status: ${res.status}`);
            } else {
                const data: LibrarySyncResult = await res.json();
                setResultMsg(data.message || "Sync completed successfully.");
                setPlatformResults(data.platforms);

                // Refresh the server component to pull the fresh sync-history
                startTransition(() => {
                    router.refresh();
                });
            }
        } catch (caughtError: unknown) {
            setResultMsg(`Error: ${getErrorMessage(caughtError, "The sync request failed.")}`);
        } finally {
            setActiveAction(null);
        }
    };

    return (
        <div className="flex w-full flex-col items-start gap-3 md:w-auto md:items-end">
            <div className="flex w-full flex-col gap-2 sm:flex-row md:w-auto">
                <Button
                    onClick={() => handleSync("/api/core/library/sync", "cached")}
                    disabled={activeAction !== null || isPending}
                    size="lg"
                >
                    <DatabaseZap className={`mr-2 h-5 w-5 ${activeAction === "cached" ? "animate-pulse" : ""}`} />
                    {activeAction === "cached" ? "Syncing cached library..." : "Sync Cached Library"}
                </Button>
                <Button
                    onClick={() => handleSync("/api/core/library/sync/refresh", "refresh")}
                    disabled={activeAction !== null || isPending}
                    variant="secondary"
                    size="lg"
                >
                    <RefreshCw className={`mr-2 h-5 w-5 ${activeAction === "refresh" ? "animate-spin" : ""}`} />
                    {activeAction === "refresh" ? "Refreshing platforms..." : "Refresh Platforms + Sync"}
                </Button>
                <Button
                    onClick={() => handleSync("/api/core/library/sync/refresh/force", "force")}
                    disabled={activeAction !== null || isPending}
                    variant="outline"
                    size="lg"
                >
                    <RotateCcw className={`mr-2 h-5 w-5 ${activeAction === "force" ? "animate-spin" : ""}`} />
                    {activeAction === "force" ? "Forcing refresh..." : "Force Refresh"}
                </Button>
            </div>

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
