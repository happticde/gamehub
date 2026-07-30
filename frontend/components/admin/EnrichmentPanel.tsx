"use client";

/**
 * EnrichmentPanel — Admin tab for managing IGDB metadata enrichment.
 *
 * This is a "Client Component" because it uses React hooks (useState, useEffect)
 * to manage state and trigger API calls from the browser. Server Components can't
 * do that — they render once on the server and can't respond to button clicks.
 *
 * Layout:
 *   1. Stats cards row   — fetched from GET /api/core/library/enrichment-stats
 *   2. Action buttons    — POST calls to enrich / enrich/stale / enrich?force=true
 *   3. Audit log table   — fetched from GET /api/core/library/enrichment-history
 */

import { useState, useEffect, useCallback } from "react";
import { fetchAuthenticated } from "@/lib/authenticated-fetch";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from "@/components/ui/select";
import {
    AlertDialog,
    AlertDialogAction,
    AlertDialogCancel,
    AlertDialogContent,
    AlertDialogDescription,
    AlertDialogFooter,
    AlertDialogHeader,
    AlertDialogTitle,
    AlertDialogTrigger,
} from "@/components/ui/alert-dialog";
import {
    Sparkles,
    RotateCcw,
    RotateCw,
    AlertTriangle,
    Loader2,
    CheckCircle2,
    XCircle,
    HelpCircle,
    Database,
} from "lucide-react";
import type { EnrichmentStats, EnrichmentAuditLog } from "@/types/game";
import type { ApiMessageResponse } from "@/types/api";
import { getErrorMessage } from "@/lib/errors";

export function EnrichmentPanel() {
    // -------------------------------------------------------
    // State — stats, logs, loading flags, and result messages
    // -------------------------------------------------------
    const [stats, setStats] = useState<EnrichmentStats | null>(null);
    const [logs, setLogs] = useState<EnrichmentAuditLog[]>([]);
    const [statusFilter, setStatusFilter] = useState<string>("all");
    const [isLoadingStats, setIsLoadingStats] = useState(true);
    const [isLoadingLogs, setIsLoadingLogs] = useState(true);
    const [actionLoading, setActionLoading] = useState<string | null>(null);
    const [resultMsg, setResultMsg] = useState("");
    // Tracks which game ID is currently being re-enriched (for the per-row button)
    const [reEnrichingId, setReEnrichingId] = useState<number | null>(null);

    // -------------------------------------------------------
    // Data fetching — useCallback keeps the functions stable
    // so they don't cause extra re-renders when passed to useEffect.
    // -------------------------------------------------------
    const fetchStats = useCallback(async () => {
        setIsLoadingStats(true);
        try {
            const res = await fetchAuthenticated("/api/core/library/enrichment-stats");
            if (res.ok) {
                const enrichmentStats: EnrichmentStats = await res.json();
                setStats(enrichmentStats);
            }
        } catch {
            // Swallow — stats cards will show placeholders
        } finally {
            setIsLoadingStats(false);
        }
    }, []);

    const fetchLogs = useCallback(async () => {
        setIsLoadingLogs(true);
        try {
            const params = new URLSearchParams({ limit: "100" });
            if (statusFilter !== "all") params.set("status", statusFilter);
            const res = await fetchAuthenticated(`/api/core/library/enrichment-history?${params}`);
            if (res.ok) {
                const enrichmentLogs: EnrichmentAuditLog[] = await res.json();
                setLogs(enrichmentLogs);
            }
        } catch {
            // Swallow — table will show empty state
        } finally {
            setIsLoadingLogs(false);
        }
    }, [statusFilter]);

    // useEffect runs *after* the component renders.
    // The dependency array [fetchStats] means it re-runs only when fetchStats changes.
    useEffect(() => { fetchStats(); }, [fetchStats]);
    useEffect(() => { fetchLogs(); }, [fetchLogs]);

    // -------------------------------------------------------
    // Action handlers — each calls a backend endpoint and then
    // refreshes both stats and logs to reflect the new state.
    // -------------------------------------------------------
    const handleAction = async (
        label: string,
        url: string,
        method: string = "POST"
    ) => {
        setActionLoading(label);
        setResultMsg("");
        try {
            const res = await fetchAuthenticated(url, { method });
            const data: ApiMessageResponse = await res.json();
            setResultMsg(data.Message || data.message || "Done.");
            // Refresh data after action completes
            fetchStats();
            fetchLogs();
        } catch (caughtError: unknown) {
            setResultMsg(`Error: ${getErrorMessage(caughtError, "The enrichment request failed.")}`);
        } finally {
            setActionLoading(null);
        }
    };

    /**
     * Re-enrich a single game by its ID.
     * Uses force=true so it re-fetches even if already enriched.
     */
    const handleReEnrich = async (gameId: number) => {
        setReEnrichingId(gameId);
        try {
            const res = await fetchAuthenticated(`/api/core/library/enrich/${gameId}?force=true`, { method: "POST" });
            const data: ApiMessageResponse = await res.json();
            setResultMsg(data.Message || data.message || "Re-enriched.");
            // Refresh stats and logs to reflect the update
            fetchStats();
            fetchLogs();
        } catch (caughtError: unknown) {
            setResultMsg(`Error re-enriching game ${gameId}: ${getErrorMessage(caughtError, "The enrichment request failed.")}`);
        } finally {
            setReEnrichingId(null);
        }
    };

    // -------------------------------------------------------
    // Render
    // -------------------------------------------------------
    return (
        <div className="space-y-8 animate-in fade-in slide-in-from-bottom-2 duration-300">
            {/* ---- Stats Cards ---- */}
            <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
                <StatCard
                    title="Total Games"
                    value={stats?.totalGames}
                    loading={isLoadingStats}
                    icon={<Database className="h-4 w-4 text-muted-foreground" />}
                />
                <StatCard
                    title="Enriched"
                    value={stats?.enrichedCount}
                    loading={isLoadingStats}
                    icon={<CheckCircle2 className="h-4 w-4 text-green-500" />}
                    accent="text-green-500"
                />
                <StatCard
                    title="Unenriched"
                    value={stats?.unenrichedCount}
                    loading={isLoadingStats}
                    icon={<HelpCircle className="h-4 w-4 text-yellow-500" />}
                    accent="text-yellow-500"
                />
                <StatCard
                    title="Errors"
                    value={stats ? stats.errorCount + stats.notFoundCount : undefined}
                    loading={isLoadingStats}
                    icon={<XCircle className="h-4 w-4 text-red-500" />}
                    accent="text-red-500"
                />
            </div>

            {stats?.lastEnrichedAt && (
                <p className="text-xs text-muted-foreground -mt-4">
                    Last enrichment:{" "}
                    {new Date(stats.lastEnrichedAt).toLocaleString(undefined, {
                        month: "short", day: "numeric", hour: "2-digit", minute: "2-digit",
                    })}
                </p>
            )}

            {/* ---- Action Buttons ---- */}
            <div className="flex flex-wrap gap-3">
                <Button
                    onClick={() => handleAction("enrich", "/api/core/library/enrich", "POST")}
                    disabled={actionLoading !== null}
                    size="lg"
                >
                    {actionLoading === "enrich" ? (
                        <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                    ) : (
                        <Sparkles className="mr-2 h-4 w-4" />
                    )}
                    Enrich Un-enriched
                </Button>

                <Button
                    variant="secondary"
                    onClick={() => handleAction("stale", "/api/core/library/enrich/stale", "POST")}
                    disabled={actionLoading !== null}
                    size="lg"
                >
                    {actionLoading === "stale" ? (
                        <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                    ) : (
                        <RotateCcw className="mr-2 h-4 w-4" />
                    )}
                    Reset Stale
                </Button>

                {/* Force re-enrich uses an AlertDialog confirmation because it's destructive */}
                <AlertDialog>
                    <AlertDialogTrigger asChild>
                        <Button variant="destructive" size="lg" disabled={actionLoading !== null}>
                            <AlertTriangle className="mr-2 h-4 w-4" />
                            Force Re-enrich All
                        </Button>
                    </AlertDialogTrigger>
                    <AlertDialogContent>
                        <AlertDialogHeader>
                            <AlertDialogTitle>Force Re-enrich All Games?</AlertDialogTitle>
                            <AlertDialogDescription>
                                This will reset the enrichment status of <strong>all</strong> games.
                                You&apos;ll need to run &quot;Enrich Un-enriched&quot; afterwards to re-fetch
                                metadata from IGDB. This is rate-limited and may take several minutes.
                            </AlertDialogDescription>
                        </AlertDialogHeader>
                        <AlertDialogFooter>
                            <AlertDialogCancel>Cancel</AlertDialogCancel>
                            <AlertDialogAction
                                onClick={() =>
                                    handleAction("force", "/api/core/library/enrich?force=true", "POST")
                                }
                            >
                                Yes, Reset All
                            </AlertDialogAction>
                        </AlertDialogFooter>
                    </AlertDialogContent>
                </AlertDialog>
            </div>

            {/* Result message */}
            {resultMsg && (
                <p className="text-sm text-muted-foreground animate-in fade-in duration-300 px-1">
                    {resultMsg}
                </p>
            )}

            {/* ---- Enrichment Audit Log ---- */}
            <div className="space-y-4">
                <div className="flex items-center justify-between">
                    <h3 className="text-lg font-semibold tracking-tight">Enrichment Audit Log</h3>
                    <Select value={statusFilter} onValueChange={setStatusFilter}>
                        <SelectTrigger className="w-36">
                            <SelectValue placeholder="Filter by status" />
                        </SelectTrigger>
                        <SelectContent>
                            <SelectItem value="all">All Statuses</SelectItem>
                            <SelectItem value="Success">Success</SelectItem>
                            <SelectItem value="Error">Error</SelectItem>
                            <SelectItem value="NotFound">Not Found</SelectItem>
                        </SelectContent>
                    </Select>
                </div>

                {isLoadingLogs ? (
                    <div className="flex items-center justify-center p-12 text-muted-foreground">
                        <Loader2 className="h-5 w-5 animate-spin mr-2" />
                        Loading logs...
                    </div>
                ) : logs.length === 0 ? (
                    <div className="flex flex-col items-center justify-center rounded-xl border border-dashed p-12 text-center text-muted-foreground">
                        <p>No enrichment logs found.</p>
                        <p className="text-sm mt-1">Trigger an enrichment run to populate the audit log.</p>
                    </div>
                ) : (
                    <>
                        {/* Mobile: Card layout */}
                        <div className="md:hidden space-y-3">
                            {logs.map((log) => (
                                <div key={log.id} className="rounded-xl border border-border bg-card p-4 space-y-2.5">
                                    <div className="flex items-start justify-between gap-2">
                                        <div className="min-w-0 flex-1">
                                            <p className="font-medium text-sm truncate" title={log.gameTitle}>{log.gameTitle}</p>
                                            <p className="text-xs text-muted-foreground mt-0.5">
                                                {new Date(log.timestamp).toLocaleString(undefined, {
                                                    month: "short", day: "numeric",
                                                    hour: "2-digit", minute: "2-digit",
                                                })}
                                            </p>
                                        </div>
                                        <div className="flex items-center gap-2 shrink-0">
                                            <EnrichmentStatusBadge status={log.status} />
                                            <Button
                                                variant="ghost"
                                                size="icon-sm"
                                                onClick={() => handleReEnrich(log.unifiedGameId)}
                                                disabled={reEnrichingId !== null}
                                                title={`Re-enrich "${log.gameTitle}"`}
                                            >
                                                {reEnrichingId === log.unifiedGameId ? (
                                                    <Loader2 className="h-3.5 w-3.5 animate-spin" />
                                                ) : (
                                                    <RotateCw className="h-3.5 w-3.5" />
                                                )}
                                            </Button>
                                        </div>
                                    </div>
                                    <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground">
                                        <span>Provider: <span className="text-foreground">{log.providerName}</span></span>
                                        {log.matchedTitle && (
                                            <span>Match: <span className="text-foreground">{log.matchedTitle}</span></span>
                                        )}
                                    </div>
                                    {log.errorMessage && (
                                        <p className="text-xs text-destructive bg-destructive/10 rounded-md px-2.5 py-1.5 break-words">
                                            {log.errorMessage}
                                        </p>
                                    )}
                                </div>
                            ))}
                        </div>

                        {/* Desktop: Table layout */}
                        <div className="hidden md:block rounded-xl border border-border bg-card overflow-hidden">
                            <div className="overflow-x-auto">
                                <table className="w-full text-sm text-left">
                                    <thead className="bg-muted/50 text-muted-foreground">
                                        <tr>
                                            <th className="px-4 py-3 font-medium">Timestamp</th>
                                            <th className="px-4 py-3 font-medium">Game</th>
                                            <th className="px-4 py-3 font-medium">Status</th>
                                            <th className="px-4 py-3 font-medium">Provider</th>
                                            <th className="px-4 py-3 font-medium">Matched Title</th>
                                            <th className="px-4 py-3 font-medium">Error</th>
                                            <th className="px-4 py-3 font-medium w-20"></th>
                                        </tr>
                                    </thead>
                                    <tbody className="divide-y divide-border">
                                        {logs.map((log) => (
                                            <tr key={log.id} className="hover:bg-muted/30 transition-colors">
                                                <td className="px-4 py-3 whitespace-nowrap text-muted-foreground">
                                                    {new Date(log.timestamp).toLocaleString(undefined, {
                                                        month: "short", day: "numeric",
                                                        hour: "2-digit", minute: "2-digit",
                                                    })}
                                                </td>
                                                <td className="px-4 py-3 font-medium max-w-[200px] truncate" title={log.gameTitle}>
                                                    {log.gameTitle}
                                                </td>
                                                <td className="px-4 py-3">
                                                    <EnrichmentStatusBadge status={log.status} />
                                                </td>
                                                <td className="px-4 py-3 text-muted-foreground">
                                                    {log.providerName}
                                                </td>
                                                <td className="px-4 py-3 text-muted-foreground max-w-[180px] truncate" title={log.matchedTitle || ""}>
                                                    {log.matchedTitle || "—"}
                                                </td>
                                                <td className="px-4 py-3 text-muted-foreground max-w-[200px] truncate" title={log.errorMessage || ""}>
                                                    {log.errorMessage || "—"}
                                                </td>
                                                <td className="px-4 py-3">
                                                    <Button
                                                        variant="ghost"
                                                        size="icon-sm"
                                                        onClick={() => handleReEnrich(log.unifiedGameId)}
                                                        disabled={reEnrichingId !== null}
                                                        title={`Re-enrich "${log.gameTitle}"`}
                                                    >
                                                        {reEnrichingId === log.unifiedGameId ? (
                                                            <Loader2 className="h-3.5 w-3.5 animate-spin" />
                                                        ) : (
                                                            <RotateCw className="h-3.5 w-3.5" />
                                                        )}
                                                    </Button>
                                                </td>
                                            </tr>
                                        ))}
                                    </tbody>
                                </table>
                            </div>
                        </div>
                    </>
                )}
            </div>
        </div>
    );
}

// =========================================================================
// Helper sub-components (used only within this file)
// =========================================================================

/** A small stats card showing a numeric value with an icon. */
function StatCard({
    title,
    value,
    loading,
    icon,
    accent,
}: {
    title: string;
    value: number | undefined;
    loading: boolean;
    icon: React.ReactNode;
    accent?: string;
}) {
    return (
        <Card>
            <CardHeader className="flex flex-row items-center justify-between pb-2">
                <CardTitle className="text-sm font-medium text-muted-foreground">
                    {title}
                </CardTitle>
                {icon}
            </CardHeader>
            <CardContent>
                {loading ? (
                    <div className="h-8 w-16 animate-pulse rounded bg-muted" />
                ) : (
                    <div className={`text-3xl font-bold tabular-nums ${accent || ""}`}>
                        {value ?? "—"}
                    </div>
                )}
            </CardContent>
        </Card>
    );
}

/** Color-coded badge for enrichment status values. */
function EnrichmentStatusBadge({ status }: { status: string }) {
    switch (status) {
        case "Success":
            return (
                <span className="inline-flex items-center rounded-md bg-green-500/15 px-2 py-1 text-xs font-medium text-green-600 dark:text-green-400 ring-1 ring-inset ring-green-500/20">
                    Success
                </span>
            );
        case "Error":
            return (
                <span className="inline-flex items-center rounded-md bg-red-500/15 px-2 py-1 text-xs font-medium text-red-600 dark:text-red-400 ring-1 ring-inset ring-red-500/20">
                    Error
                </span>
            );
        case "NotFound":
            return (
                <span className="inline-flex items-center rounded-md bg-yellow-500/15 px-2 py-1 text-xs font-medium text-yellow-600 dark:text-yellow-400 ring-1 ring-inset ring-yellow-500/20">
                    Not Found
                </span>
            );
        default:
            return (
                <span className="inline-flex items-center rounded-md bg-zinc-500/15 px-2 py-1 text-xs font-medium text-zinc-600 dark:text-zinc-400 ring-1 ring-inset ring-zinc-500/20">
                    {status}
                </span>
            );
    }
}
