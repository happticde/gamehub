"use client";

/**
 * SyncPanel — Admin tab for library unification sync.
 *
 * This wraps the existing SyncTriggerButton and SyncHistoryTable components
 * but fetches data client-side (the original /ingest page did server-side fetch).
 *
 * Why client-side? Because this panel lives inside a tab that's controlled by
 * client-side state — the tab content is only rendered when the tab is active,
 * so we can't rely on server-side rendering for data freshness.
 *
 * Sections:
 *   1. Sync Scheduler Status  — live status card for the background scheduler
 *   2. Library Unification    — manual sync trigger
 *   3. Retroactive Maintenance — keyword-based game hiding
 *   4. Unification Sync Logs  — audit history table
 */

import { useState, useEffect, useCallback } from "react";
import { SyncTriggerButton } from "@/components/SyncTriggerButton";
import { SyncHistoryTable } from "@/components/SyncHistoryTable";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Loader2, Clock, Activity, CalendarClock, CheckCircle2, XCircle, Timer } from "lucide-react";
import { getErrorMessage } from "@/lib/errors";
import { fetchAuthenticated } from "@/lib/authenticated-fetch";
import type { RetroactiveIgnoreResponse } from "@/types/api";
import type { SyncAuditLog } from "@/types/game";

/** Shape returned by GET /api/library/sync/status */
interface SyncSchedulerStatus {
    isEnabled: boolean;
    isRunning: boolean;
    lastRunAt: string | null;
    nextRunAt: string | null;
    lastResult: string | null;
    itemsProcessed: number;
}

/**
 * Formats a UTC ISO timestamp into a user-friendly local string.
 * Returns "Never" if the timestamp is null.
 */
function formatTimestamp(iso: string | null): string {
    if (!iso) return "Never";
    const date = new Date(iso);
    return date.toLocaleString(undefined, {
        month: "short",
        day: "numeric",
        hour: "2-digit",
        minute: "2-digit",
        second: "2-digit",
    });
}

/**
 * Calculates a human-friendly "time until" string (e.g., "in 2h 34m").
 * Returns null if the date is in the past or input is null.
 */
function timeUntil(iso: string | null): string | null {
    if (!iso) return null;
    const diff = new Date(iso).getTime() - Date.now();
    if (diff <= 0) return "soon";
    const hours = Math.floor(diff / 3_600_000);
    const minutes = Math.floor((diff % 3_600_000) / 60_000);
    if (hours > 0) return `in ${hours}h ${minutes}m`;
    return `in ${minutes}m`;
}

export function SyncPanel() {
    const [logs, setLogs] = useState<SyncAuditLog[]>([]);
    const [isLoading, setIsLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);

    // Scheduler status state
    const [status, setStatus] = useState<SyncSchedulerStatus | null>(null);
    const [statusError, setStatusError] = useState<string | null>(null);

    const fetchLogs = useCallback(() => {
        // Client-side fetch goes through the authenticated same-origin BFF.
        return fetchAuthenticated("/api/core/library/sync-history?limit=100", {
            cache: "no-store",
        }).then(async (res) => {
            if (!res.ok) {
                setError(`Failed to fetch history (Status ${res.status})`);
            } else {
                const syncLogs: SyncAuditLog[] = await res.json();
                setLogs(syncLogs);
                setError(null);
            }
        }).catch((caughtError: unknown) => {
            setError(getErrorMessage(caughtError, "Unknown error"));
        }).finally(() => {
            setIsLoading(false);
        });
    }, []);

    const fetchStatus = useCallback(() => {
        return fetchAuthenticated("/api/core/library/sync/status").then(async (res) => {
            if (res.ok) {
                const schedulerStatus: SyncSchedulerStatus = await res.json();
                setStatus(schedulerStatus);
                setStatusError(null);
            } else {
                setStatusError(`Status ${res.status}`);
            }
        }).catch((caughtError: unknown) => {
            setStatusError(getErrorMessage(caughtError, "Unknown error"));
        });
    }, []);

    const refreshAfterSync = useCallback(async () => {
        setIsLoading(true);
        setError(null);
        await Promise.all([fetchLogs(), fetchStatus()]);
    }, [fetchLogs, fetchStatus]);

    useEffect(() => {
        fetchLogs();
        fetchStatus();

        // Auto-refresh scheduler status every 30 seconds
        const interval = setInterval(fetchStatus, 30_000);
        return () => clearInterval(interval);
    }, [fetchLogs, fetchStatus]);

    return (
        <div className="space-y-8 animate-in fade-in slide-in-from-bottom-2 duration-300">
            {/* Sync Scheduler Status Card */}
            <Card>
                <CardHeader className="pb-3">
                    <div className="flex items-center justify-between">
                        <CardTitle className="flex items-center gap-2 text-lg">
                            <Timer className="h-5 w-5 text-primary" />
                            Background Sync Scheduler
                        </CardTitle>
                        {status && (
                            <Badge variant={status.isEnabled ? "default" : "secondary"}>
                                {status.isEnabled ? "Enabled" : "Disabled"}
                            </Badge>
                        )}
                    </div>
                </CardHeader>
                <CardContent>
                    {statusError ? (
                        <p className="text-sm text-destructive">
                            Could not load scheduler status: {statusError}
                        </p>
                    ) : !status ? (
                        <div className="flex items-center text-sm text-muted-foreground">
                            <Loader2 className="h-4 w-4 animate-spin mr-2" />
                            Loading status...
                        </div>
                    ) : !status.isEnabled ? (
                        <p className="text-sm text-muted-foreground">
                            The background sync scheduler is disabled. Set{" "}
                            <code className="text-xs bg-muted px-1.5 py-0.5 rounded font-mono">
                                Sync:EnableScheduler=true
                            </code>{" "}
                            in your configuration to activate automatic periodic syncing.
                        </p>
                    ) : (
                        <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
                            {/* Running State */}
                            <div className="flex items-start gap-2">
                                <Activity className={`h-4 w-4 mt-0.5 shrink-0 ${status.isRunning ? "text-primary animate-pulse" : "text-muted-foreground"}`} />
                                <div>
                                    <p className="text-xs font-medium text-muted-foreground uppercase tracking-wider">Status</p>
                                    <p className="text-sm font-semibold mt-0.5">
                                        {status.isRunning ? (
                                            <span className="text-primary">Syncing…</span>
                                        ) : (
                                            <span className="text-foreground">Idle</span>
                                        )}
                                    </p>
                                </div>
                            </div>

                            {/* Last Run */}
                            <div className="flex items-start gap-2">
                                <Clock className="h-4 w-4 mt-0.5 text-muted-foreground shrink-0" />
                                <div>
                                    <p className="text-xs font-medium text-muted-foreground uppercase tracking-wider">Last Run</p>
                                    <p className="text-sm font-semibold mt-0.5">{formatTimestamp(status.lastRunAt)}</p>
                                </div>
                            </div>

                            {/* Next Run */}
                            <div className="flex items-start gap-2">
                                <CalendarClock className="h-4 w-4 mt-0.5 text-muted-foreground shrink-0" />
                                <div>
                                    <p className="text-xs font-medium text-muted-foreground uppercase tracking-wider">Next Run</p>
                                    <p className="text-sm font-semibold mt-0.5">
                                        {status.nextRunAt ? (
                                            <>
                                                {formatTimestamp(status.nextRunAt)}
                                                <span className="text-xs font-normal text-muted-foreground ml-1">
                                                    ({timeUntil(status.nextRunAt)})
                                                </span>
                                            </>
                                        ) : (
                                            "—"
                                        )}
                                    </p>
                                </div>
                            </div>

                            {/* Last Result */}
                            <div className="flex items-start gap-2">
                                {status.lastResult === "Success" ? (
                                    <CheckCircle2 className="h-4 w-4 mt-0.5 text-green-500 shrink-0" />
                                ) : status.lastResult ? (
                                    <XCircle className="h-4 w-4 mt-0.5 text-destructive shrink-0" />
                                ) : (
                                    <CheckCircle2 className="h-4 w-4 mt-0.5 text-muted-foreground shrink-0" />
                                )}
                                <div>
                                    <p className="text-xs font-medium text-muted-foreground uppercase tracking-wider">Last Result</p>
                                    <p className={`text-sm font-semibold mt-0.5 ${
                                        status.lastResult === "Success"
                                            ? "text-green-500"
                                            : status.lastResult
                                            ? "text-destructive"
                                            : ""
                                    }`}>
                                        {status.lastResult
                                            ? status.lastResult === "Success"
                                                ? `${status.itemsProcessed} items`
                                                : status.lastResult
                                            : "—"}
                                    </p>
                                </div>
                            </div>
                        </div>
                    )}
                </CardContent>
            </Card>

            {/* Sync trigger — reuses the existing component */}
            <div className="flex flex-col gap-4 border-b pb-6">
                <div>
                    <h3 className="text-lg font-semibold tracking-tight">Library Unification</h3>
                    <p className="text-sm text-muted-foreground mt-1">
                        Cached sync is the fast default and never contacts Steam, GOG, PlayStation, or Epic.
                        Platform refresh can be slow; providers inside the configured freshness window are skipped.
                        Force refresh ignores freshness, but still respects credentials and rate-limit cooldowns.
                    </p>
                </div>
                <SyncTriggerButton onSyncComplete={refreshAfterSync} />
            </div>

            {/* Retroactive Maintenance */}
            <div className="flex items-center justify-between border-b pb-6">
                <div>
                    <h3 className="text-lg font-semibold tracking-tight">Retroactive Maintenance</h3>
                    <p className="text-sm text-muted-foreground mt-1 text-balance">
                        Scans your existing library and automatically hides games matching the ignore keywords (e.g. demos, betas). 
                        This does not delete data or enrichment status.
                    </p>
                </div>
                <button 
                    onClick={async () => {
                        try {
                            const res = await fetchAuthenticated("/api/core/library/retroactive-ignore", { method: "POST" });
                            if (res.ok) {
                                const data: RetroactiveIgnoreResponse = await res.json();
                                alert(`Success: Found and hid ${data.newlyIgnoredCount} games.`);
                                setIsLoading(true);
                                setError(null);
                                fetchLogs(); // just to refresh UI if needed
                            } else {
                                alert("Failed to run retroactive ignore script.");
                            }
                        } catch (caughtError: unknown) {
                            alert(getErrorMessage(caughtError, "Failed to run the retroactive ignore script."));
                        }
                    }}
                    className="inline-flex items-center justify-center whitespace-nowrap rounded-md text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:pointer-events-none disabled:opacity-50 border border-input bg-background shadow-sm hover:bg-accent hover:text-accent-foreground h-9 px-4 py-2"
                >
                    Run Keyword Filter
                </button>
            </div>

            {/* Sync history */}
            <div className="space-y-4">
                <div className="flex items-center justify-between">
                    <h3 className="text-lg font-semibold tracking-tight">Unification Sync Logs</h3>
                    <span className="text-sm text-muted-foreground">Showing last 100 entries</span>
                </div>

                {isLoading ? (
                    <div className="flex items-center justify-center p-12 text-muted-foreground">
                        <Loader2 className="h-5 w-5 animate-spin mr-2" />
                        Loading sync history...
                    </div>
                ) : error ? (
                    <div className="rounded-xl border border-destructive/50 bg-destructive/10 p-6 text-destructive">
                        <h4 className="font-semibold mb-2">Failed to load sync history</h4>
                        <p className="text-sm font-mono">{error}</p>
                        <p className="text-sm mt-4 text-muted-foreground">
                            Make sure the C# Core API is running on port 5001 and is accessible.
                        </p>
                    </div>
                ) : (
                    <SyncHistoryTable logs={logs} />
                )}
            </div>
        </div>
    );
}
