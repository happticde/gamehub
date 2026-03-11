"use client";

/**
 * IngestionPanel — Admin tab for triggering platform data ingestion.
 *
 * This component lets the user provide platform credentials and trigger
 * a fetch-and-ingest in one step via the Retrieve endpoints on the Ingest service.
 *
 * Each platform has its own collapsible card with the required credential fields.
 * The PSN card supports dynamic key-value pairs for browser cookies.
 *
 * Below the platform cards, there's an ingest audit log table showing recent
 * retrieval activity fetched from GET /api/ingest/retrieve/history.
 */

import { useState, useEffect, useCallback } from "react";
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from "@/components/ui/select";
import {
    Loader2,
    ChevronDown,
    ChevronUp,
    Plus,
    Trash2,
    Download,
} from "lucide-react";
import { getPlatformColor } from "@/lib/platform-colors";
import type { IngestAuditLog } from "@/types/game";

export function IngestionPanel() {
    // -------------------------------------------------------
    // Ingest audit log state
    // -------------------------------------------------------
    const [logs, setLogs] = useState<IngestAuditLog[]>([]);
    const [platformFilter, setPlatformFilter] = useState<string>("all");
    const [isLoadingLogs, setIsLoadingLogs] = useState(true);

    const fetchLogs = useCallback(async () => {
        setIsLoadingLogs(true);
        try {
            const params = new URLSearchParams({ limit: "100" });
            if (platformFilter !== "all") params.set("platform", platformFilter);
            const res = await fetch(`/api/ingest/retrieve/history?${params}`);
            if (res.ok) setLogs(await res.json());
        } catch {
            // Swallow — table will show empty
        } finally {
            setIsLoadingLogs(false);
        }
    }, [platformFilter]);

    useEffect(() => { fetchLogs(); }, [fetchLogs]);

    return (
        <div className="space-y-8 animate-in fade-in slide-in-from-bottom-2 duration-300">
            {/* ---- Platform Ingest Cards ---- */}
            <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
                <SteamIngestCard onSuccess={fetchLogs} />
                <GogIngestCard onSuccess={fetchLogs} />
                <PsnIngestCard onSuccess={fetchLogs} />
            </div>

            {/* ---- Ingest Audit Log ---- */}
            <div className="space-y-4">
                <div className="flex items-center justify-between">
                    <h3 className="text-lg font-semibold tracking-tight">Ingest Audit Log</h3>
                    <Select value={platformFilter} onValueChange={setPlatformFilter}>
                        <SelectTrigger className="w-36">
                            <SelectValue placeholder="Filter platform" />
                        </SelectTrigger>
                        <SelectContent>
                            <SelectItem value="all">All Platforms</SelectItem>
                            <SelectItem value="Steam">Steam</SelectItem>
                            <SelectItem value="Gog">GOG</SelectItem>
                            <SelectItem value="Psn">PSN</SelectItem>
                            <SelectItem value="Epic">Epic</SelectItem>
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
                        <p>No ingest logs found.</p>
                        <p className="text-sm mt-1">Trigger a platform retrieval to populate the audit log.</p>
                    </div>
                ) : (
                    <>
                        {/* Mobile: Card layout */}
                        <div className="md:hidden space-y-3">
                            {logs.map((log) => (
                                <div key={log.id} className="rounded-xl border border-border bg-card p-4 space-y-2.5">
                                    <div className="flex items-start justify-between gap-2">
                                        <div className="flex items-center gap-2">
                                            <span className="inline-flex items-center rounded-full bg-secondary px-2.5 py-0.5 text-xs text-secondary-foreground">
                                                {log.platform}
                                            </span>
                                            <IngestStatusBadge status={log.status} />
                                        </div>
                                        <span className="text-xs text-muted-foreground whitespace-nowrap">
                                            {new Date(log.timestamp).toLocaleString(undefined, {
                                                month: "short", day: "numeric",
                                                hour: "2-digit", minute: "2-digit",
                                            })}
                                        </span>
                                    </div>
                                    <div className="flex items-center gap-2 text-sm">
                                        <span className="text-muted-foreground">Items:</span>
                                        <span className="font-medium tabular-nums">{log.itemsProcessed}</span>
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
                                            <th className="px-4 py-3 font-medium">Platform</th>
                                            <th className="px-4 py-3 font-medium">Status</th>
                                            <th className="px-4 py-3 font-medium">Items</th>
                                            <th className="px-4 py-3 font-medium">Error</th>
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
                                                <td className="px-4 py-3">
                                                    <span className="inline-flex items-center rounded-full bg-secondary px-2.5 py-0.5 text-xs text-secondary-foreground">
                                                        {log.platform}
                                                    </span>
                                                </td>
                                                <td className="px-4 py-3">
                                                    <IngestStatusBadge status={log.status} />
                                                </td>
                                                <td className="px-4 py-3 whitespace-nowrap tabular-nums font-medium">
                                                    {log.itemsProcessed}
                                                </td>
                                                <td className="px-4 py-3 text-muted-foreground max-w-[250px] truncate" title={log.errorMessage || ""}>
                                                    {log.errorMessage || "—"}
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
// Platform Ingest Card Components
// =========================================================================

/**
 * SteamIngestCard — Collapsible card with API Key + Steam ID inputs.
 * Calls POST /api/ingest/retrieve/steam when submitted.
 */
function SteamIngestCard({ onSuccess }: { onSuccess: () => void }) {
    const [expanded, setExpanded] = useState(false);
    const [apiKey, setApiKey] = useState("");
    const [steamId, setSteamId] = useState("");
    const [loading, setLoading] = useState(false);
    const [result, setResult] = useState("");

    const handleSubmit = async () => {
        setLoading(true);
        setResult("");
        try {
            const res = await fetch("/api/ingest/retrieve/steam", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ apiKey, steamId }),
            });
            const data = await res.json();
            setResult(data.Message || data.message || `Status: ${res.status}`);
            if (res.ok) onSuccess();
        } catch (err: any) {
            setResult(`Error: ${err.message}`);
        } finally {
            setLoading(false);
        }
    };

    return (
        <Card className="overflow-hidden">
            <CardHeader
                className="cursor-pointer select-none"
                onClick={() => setExpanded(!expanded)}
            >
                <div className="flex items-center justify-between">
                    <div className="flex items-center gap-3">
                        <div className="h-3 w-3 rounded-full" style={{ backgroundColor: getPlatformColor("Steam") }} />
                        <CardTitle className="text-base">Steam</CardTitle>
                    </div>
                    {expanded ? <ChevronUp className="h-4 w-4 text-muted-foreground" /> : <ChevronDown className="h-4 w-4 text-muted-foreground" />}
                </div>
                <CardDescription>Fetch via Steam Web API</CardDescription>
            </CardHeader>
            {expanded && (
                <CardContent className="space-y-3">
                    <div>
                        <label className="text-xs font-medium text-muted-foreground mb-1 block">Steam Web API Key</label>
                        <Input type="password" placeholder="Your Steam API key" value={apiKey} onChange={(e) => setApiKey(e.target.value)} />
                    </div>
                    <div>
                        <label className="text-xs font-medium text-muted-foreground mb-1 block">Steam ID (64-bit)</label>
                        <Input placeholder="e.g. 76561198012345678" value={steamId} onChange={(e) => setSteamId(e.target.value)} />
                    </div>
                    <Button onClick={handleSubmit} disabled={loading || !apiKey || !steamId} className="w-full">
                        {loading ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Download className="mr-2 h-4 w-4" />}
                        {loading ? "Fetching..." : "Fetch & Ingest"}
                    </Button>
                    {result && <p className="text-xs text-muted-foreground">{result}</p>}
                </CardContent>
            )}
        </Card>
    );
}

/**
 * GogIngestCard — Collapsible card with Cookie + Username inputs.
 * Calls POST /api/ingest/retrieve/gog when submitted.
 */
function GogIngestCard({ onSuccess }: { onSuccess: () => void }) {
    const [expanded, setExpanded] = useState(false);
    const [cookie, setCookie] = useState("");
    const [user, setUser] = useState("");
    const [loading, setLoading] = useState(false);
    const [result, setResult] = useState("");

    const handleSubmit = async () => {
        setLoading(true);
        setResult("");
        try {
            const res = await fetch("/api/ingest/retrieve/gog", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ cookie, user }),
            });
            const data = await res.json();
            setResult(data.Message || data.message || `Status: ${res.status}`);
            if (res.ok) onSuccess();
        } catch (err: any) {
            setResult(`Error: ${err.message}`);
        } finally {
            setLoading(false);
        }
    };

    return (
        <Card className="overflow-hidden">
            <CardHeader
                className="cursor-pointer select-none"
                onClick={() => setExpanded(!expanded)}
            >
                <div className="flex items-center justify-between">
                    <div className="flex items-center gap-3">
                        <div className="h-3 w-3 rounded-full" style={{ backgroundColor: getPlatformColor("Gog") }} />
                        <CardTitle className="text-base">GOG</CardTitle>
                    </div>
                    {expanded ? <ChevronUp className="h-4 w-4 text-muted-foreground" /> : <ChevronDown className="h-4 w-4 text-muted-foreground" />}
                </div>
                <CardDescription>Fetch via GOG Galaxy API</CardDescription>
            </CardHeader>
            {expanded && (
                <CardContent className="space-y-3">
                    <div>
                        <label className="text-xs font-medium text-muted-foreground mb-1 block">GOG Cookie (gog-al)</label>
                        <Input type="password" placeholder="gog-al cookie value" value={cookie} onChange={(e) => setCookie(e.target.value)} />
                    </div>
                    <div>
                        <label className="text-xs font-medium text-muted-foreground mb-1 block">GOG Username</label>
                        <Input placeholder="Your GOG username" value={user} onChange={(e) => setUser(e.target.value)} />
                    </div>
                    <Button onClick={handleSubmit} disabled={loading || !cookie || !user} className="w-full">
                        {loading ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Download className="mr-2 h-4 w-4" />}
                        {loading ? "Fetching..." : "Fetch & Ingest"}
                    </Button>
                    {result && <p className="text-xs text-muted-foreground">{result}</p>}
                </CardContent>
            )}
        </Card>
    );
}

/**
 * PsnIngestCard — Collapsible card with dynamic cookie key-value rows.
 *
 * PSN ingestion requires several browser cookies (JSESSIONID, sb-cdn.session, etc.).
 * This card lets the user add/remove rows for each cookie key-value pair,
 * then sends them all as POST /api/ingest/retrieve/psn.
 */
function PsnIngestCard({ onSuccess }: { onSuccess: () => void }) {
    const [expanded, setExpanded] = useState(false);
    // Each cookie row has a unique key string and a value string
    const [cookies, setCookies] = useState<{ key: string; value: string }[]>([
        { key: "", value: "" },
    ]);
    const [loading, setLoading] = useState(false);
    const [result, setResult] = useState("");

    /** Add a new empty cookie row */
    const addRow = () => setCookies((prev) => [...prev, { key: "", value: "" }]);

    /** Remove a cookie row by index */
    const removeRow = (index: number) =>
        setCookies((prev) => prev.filter((_, i) => i !== index));

    /** Update a specific field in a cookie row */
    const updateRow = (index: number, field: "key" | "value", val: string) =>
        setCookies((prev) =>
            prev.map((row, i) => (i === index ? { ...row, [field]: val } : row))
        );

    const handleSubmit = async () => {
        setLoading(true);
        setResult("");
        try {
            // Convert the array of {key, value} into a plain object
            const cookieObj: Record<string, string> = {};
            cookies.forEach((c) => {
                if (c.key.trim()) cookieObj[c.key.trim()] = c.value;
            });

            const res = await fetch("/api/ingest/retrieve/psn", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ cookies: cookieObj }),
            });
            const data = await res.json();
            setResult(data.Message || data.message || `Status: ${res.status}`);
            if (res.ok) onSuccess();
        } catch (err: any) {
            setResult(`Error: ${err.message}`);
        } finally {
            setLoading(false);
        }
    };

    // The submit button is disabled if there are no non-empty cookie keys
    const hasValidCookies = cookies.some((c) => c.key.trim() && c.value.trim());

    return (
        <Card className="overflow-hidden">
            <CardHeader
                className="cursor-pointer select-none"
                onClick={() => setExpanded(!expanded)}
            >
                <div className="flex items-center justify-between">
                    <div className="flex items-center gap-3">
                        <div className="h-3 w-3 rounded-full" style={{ backgroundColor: getPlatformColor("Psn") }} />
                        <CardTitle className="text-base">PlayStation</CardTitle>
                    </div>
                    {expanded ? <ChevronUp className="h-4 w-4 text-muted-foreground" /> : <ChevronDown className="h-4 w-4 text-muted-foreground" />}
                </div>
                <CardDescription>Fetch via PSN store cookies</CardDescription>
            </CardHeader>
            {expanded && (
                <CardContent className="space-y-3">
                    <p className="text-xs text-muted-foreground">
                        Add browser cookies from store.playstation.com (e.g. JSESSIONID, sb-cdn.session).
                    </p>
                    {cookies.map((cookie, i) => (
                        <div key={i} className="flex gap-2 items-center">
                            <Input
                                placeholder="Cookie name"
                                className="flex-1"
                                value={cookie.key}
                                onChange={(e) => updateRow(i, "key", e.target.value)}
                            />
                            <Input
                                placeholder="Cookie value"
                                className="flex-[2]"
                                type="password"
                                value={cookie.value}
                                onChange={(e) => updateRow(i, "value", e.target.value)}
                            />
                            <Button
                                variant="ghost"
                                size="icon"
                                onClick={() => removeRow(i)}
                                disabled={cookies.length <= 1}
                            >
                                <Trash2 className="h-4 w-4 text-muted-foreground" />
                            </Button>
                        </div>
                    ))}
                    <Button variant="outline" size="sm" onClick={addRow} className="w-full">
                        <Plus className="mr-2 h-3 w-3" />
                        Add Cookie
                    </Button>
                    <Button onClick={handleSubmit} disabled={loading || !hasValidCookies} className="w-full">
                        {loading ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Download className="mr-2 h-4 w-4" />}
                        {loading ? "Fetching..." : "Fetch & Ingest"}
                    </Button>
                    {result && <p className="text-xs text-muted-foreground">{result}</p>}
                </CardContent>
            )}
        </Card>
    );
}

// =========================================================================
// Helper sub-components
// =========================================================================

/** Color-coded badge for ingest status values. */
function IngestStatusBadge({ status }: { status: string }) {
    switch (status) {
        case "Success":
            return (
                <span className="inline-flex items-center rounded-md bg-green-500/15 px-2 py-1 text-xs font-medium text-green-600 dark:text-green-400 ring-1 ring-inset ring-green-500/20">
                    Success
                </span>
            );
        case "Failed":
            return (
                <span className="inline-flex items-center rounded-md bg-red-500/15 px-2 py-1 text-xs font-medium text-red-600 dark:text-red-400 ring-1 ring-inset ring-red-500/20">
                    Failed
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
