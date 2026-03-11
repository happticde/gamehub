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
 */

import { useState, useEffect, useCallback } from "react";
import { SyncTriggerButton } from "@/components/SyncTriggerButton";
import { SyncHistoryTable } from "@/components/SyncHistoryTable";
import { Loader2 } from "lucide-react";

export function SyncPanel() {
    const [logs, setLogs] = useState([]);
    const [isLoading, setIsLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);

    const fetchLogs = useCallback(async () => {
        setIsLoading(true);
        setError(null);
        try {
            // Client-side fetch goes through the Next.js proxy defined in next.config.ts
            const res = await fetch("/api/core/library/sync-history?limit=100");
            if (!res.ok) {
                setError(`Failed to fetch history (Status ${res.status})`);
            } else {
                setLogs(await res.json());
            }
        } catch (err: any) {
            setError(err.message || "Unknown error");
        } finally {
            setIsLoading(false);
        }
    }, []);

    useEffect(() => { fetchLogs(); }, [fetchLogs]);

    return (
        <div className="space-y-8 animate-in fade-in slide-in-from-bottom-2 duration-300">
            {/* Sync trigger — reuses the existing component */}
            <div className="flex items-center justify-between border-b pb-6">
                <div>
                    <h3 className="text-lg font-semibold tracking-tight">Library Unification</h3>
                    <p className="text-sm text-muted-foreground mt-1">
                        Sweep all platform data from the Ingest service and merge into the unified library.
                    </p>
                </div>
                <SyncTriggerButton />
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
                            const res = await fetch("/api/core/library/retroactive-ignore", { method: "POST" });
                            if (res.ok) {
                                const data = await res.json();
                                alert(`Success: Found and hid ${data.newlyIgnoredCount} games.`);
                                fetchLogs(); // just to refresh UI if needed
                            } else {
                                alert("Failed to run retroactive ignore script.");
                            }
                        } catch (e: any) {
                            alert(e.message);
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
