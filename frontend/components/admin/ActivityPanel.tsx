"use client";

/**
 * ActivityPanel — Admin tab showing recent system activity.
 *
 * Fetches the latest activity log entries through the authenticated Core BFF.
 * and renders them in a card with relative timestamps (e.g. "5 mins ago").
 *
 * Why client-side? Same reason as the other admin panels — it lives inside
 * a tab controlled by client state, so data must be fetched on mount.
 *
 * Auto-refreshes every 30 seconds to keep the feed current.
 */

import { useState, useEffect, useCallback } from "react";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import {
    Loader2,
    Bell,
    RefreshCw,
    GitMerge,
    GitBranch,
    Activity,
} from "lucide-react";
import { getErrorMessage } from "@/lib/errors";
import { fetchAuthenticated } from "@/lib/authenticated-fetch";

/** Shape of a single activity log entry from the API. */
interface ActivityEntry {
    id: number;
    timestamp: string;
    eventType: string;
    message: string;
    details: string | null;
}

/**
 * Converts a UTC ISO timestamp into a human-friendly relative string.
 *
 * Uses simple thresholds instead of Intl.RelativeTimeFormat for broader
 * compatibility and cleaner output (e.g. "5 mins ago" vs "5 minutes ago").
 */
function timeAgo(iso: string): string {
    const seconds = Math.floor(
        (Date.now() - new Date(iso).getTime()) / 1000
    );

    if (seconds < 60) return "just now";
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) return `${minutes} min${minutes !== 1 ? "s" : ""} ago`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `${hours} hour${hours !== 1 ? "s" : ""} ago`;
    const days = Math.floor(hours / 24);
    if (days < 7) return `${days} day${days !== 1 ? "s" : ""} ago`;

    // Fallback: show the actual date for anything older than a week
    return new Date(iso).toLocaleDateString(undefined, {
        month: "short",
        day: "numeric",
    });
}

/**
 * Returns an icon component and badge variant based on the event type.
 * This visual differentiation makes the feed scannable at a glance.
 */
function getEventMeta(eventType: string): {
    icon: React.ReactNode;
    variant: "default" | "secondary" | "outline" | "destructive";
    label: string;
} {
    switch (eventType) {
        case "SyncStarted":
            return {
                icon: <RefreshCw className="h-4 w-4" />,
                variant: "secondary",
                label: "Sync Started",
            };
        case "SyncFinished":
            return {
                icon: <Activity className="h-4 w-4" />,
                variant: "default",
                label: "Sync Finished",
            };
        case "Merge":
            return {
                icon: <GitMerge className="h-4 w-4" />,
                variant: "outline",
                label: "Merge",
            };
        case "Split":
            return {
                icon: <GitBranch className="h-4 w-4" />,
                variant: "outline",
                label: "Split",
            };
        default:
            return {
                icon: <Bell className="h-4 w-4" />,
                variant: "secondary",
                label: eventType,
            };
    }
}

export function ActivityPanel() {
    const [entries, setEntries] = useState<ActivityEntry[]>([]);
    const [isLoading, setIsLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);

    const fetchActivity = useCallback(async () => {
        try {
            const res = await fetchAuthenticated("/api/core/activity");
            if (!res.ok) {
                setError(`Failed to fetch activity (Status ${res.status})`);
                return;
            }
            const activityEntries: ActivityEntry[] = await res.json();
            setEntries(activityEntries);
            setError(null);
        } catch (caughtError: unknown) {
            setError(getErrorMessage(caughtError, "Unknown error"));
        } finally {
            setIsLoading(false);
        }
    }, []);

    useEffect(() => {
        fetchActivity();

        // Auto-refresh every 30 seconds to keep the feed current
        const interval = setInterval(fetchActivity, 30_000);
        return () => clearInterval(interval);
    }, [fetchActivity]);

    return (
        <div className="space-y-6 animate-in fade-in slide-in-from-bottom-2 duration-300">
            <Card>
                <CardHeader className="pb-3">
                    <CardTitle className="flex items-center gap-2 text-lg">
                        <Bell className="h-5 w-5 text-primary" />
                        Recent Activity
                        <span className="text-sm font-normal text-muted-foreground ml-auto">
                            Auto-refreshes every 30s
                        </span>
                    </CardTitle>
                </CardHeader>
                <CardContent>
                    {isLoading ? (
                        <div className="flex items-center justify-center p-12 text-muted-foreground">
                            <Loader2 className="h-5 w-5 animate-spin mr-2" />
                            Loading activity...
                        </div>
                    ) : error ? (
                        <div className="rounded-xl border border-destructive/50 bg-destructive/10 p-6 text-destructive">
                            <h4 className="font-semibold mb-2">
                                Failed to load activity feed
                            </h4>
                            <p className="text-sm font-mono">{error}</p>
                            <p className="text-sm mt-4 text-muted-foreground">
                                Make sure the C# Core API is running on port
                                5001 and is accessible.
                            </p>
                        </div>
                    ) : entries.length === 0 ? (
                        <div className="text-center py-12 text-muted-foreground">
                            <Bell className="h-10 w-10 mx-auto mb-3 opacity-30" />
                            <p className="text-sm">No activity yet.</p>
                            <p className="text-xs mt-1">
                                Events will appear here when you run syncs,
                                merge, or split games.
                            </p>
                        </div>
                    ) : (
                        <ul className="space-y-3">
                            {entries.map((entry) => {
                                const meta = getEventMeta(entry.eventType);
                                return (
                                    <li
                                        key={entry.id}
                                        className="flex items-start gap-3 p-3 rounded-lg border bg-card hover:bg-accent/50 transition-colors"
                                    >
                                        {/* Event icon */}
                                        <div className="mt-0.5 shrink-0 text-muted-foreground">
                                            {meta.icon}
                                        </div>

                                        {/* Event details */}
                                        <div className="flex-1 min-w-0">
                                            <div className="flex items-center gap-2 flex-wrap">
                                                <Badge
                                                    variant={meta.variant}
                                                    className="text-xs"
                                                >
                                                    {meta.label}
                                                </Badge>
                                                <span className="text-xs text-muted-foreground">
                                                    {timeAgo(entry.timestamp)}
                                                </span>
                                            </div>
                                            <p className="text-sm mt-1 text-foreground">
                                                {entry.message}
                                            </p>
                                            {entry.details && (
                                                <p className="text-xs text-muted-foreground mt-1 font-mono truncate">
                                                    {entry.details}
                                                </p>
                                            )}
                                        </div>
                                    </li>
                                );
                            })}
                        </ul>
                    )}
                </CardContent>
            </Card>
        </div>
    );
}
