"use client";

/**
 * Tenant-safe immediate platform retrieval. The browser never sends API keys or platform
 * secrets to Ingest directly; Core authenticates the user and delegates the tenant internally.
 */
import { useCallback, useEffect, useState } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from "@/components/ui/select";
import { getPlatformColor } from "@/lib/platform-colors";
import { fetchAuthenticated } from "@/lib/authenticated-fetch";
import type {
    IngestAuditLog,
    Platform,
    PlatformCredentialManagement,
    PlatformCredentialStatus,
} from "@/types/game";
import { Download, Loader2, ShieldAlert } from "lucide-react";

const RETRIEVAL_PLATFORMS: Platform[] = ["Steam", "Gog", "Psn", "Epic"];

type IngestStatusPresentation = {
    label: string;
    badgeClassName: string;
    messageClassName: string;
    guidance?: string;
};

const INGEST_STATUS_PRESENTATIONS: Record<string, IngestStatusPresentation> = {
    Success: {
        label: "Succeeded",
        badgeClassName: "bg-green-500/15 text-green-700 dark:text-green-300",
        messageClassName: "text-green-700 dark:text-green-300",
    },
    PartialSuccess: {
        label: "Partial success",
        badgeClassName: "bg-amber-500/15 text-amber-800 dark:text-amber-200",
        messageClassName: "text-amber-800 dark:text-amber-200",
        guidance: "Available games were updated; some catalog metadata was skipped.",
    },
    TokenExpired: {
        label: "Credential expired",
        badgeClassName: "bg-red-500/15 text-red-700 dark:text-red-300",
        messageClassName: "text-red-700 dark:text-red-300",
        guidance: "Reconnect this platform account, then retry.",
    },
    RateLimited: {
        label: "Rate limited",
        badgeClassName: "bg-amber-500/15 text-amber-800 dark:text-amber-200",
        messageClassName: "text-amber-800 dark:text-amber-200",
        guidance: "Wait before retrying this platform.",
    },
    UpstreamUnavailable: {
        label: "Provider unavailable",
        badgeClassName: "bg-orange-500/15 text-orange-800 dark:text-orange-200",
        messageClassName: "text-orange-800 dark:text-orange-200",
        guidance: "Existing raw games remain available; try again later.",
    },
    MalformedResponse: {
        label: "Unexpected response",
        badgeClassName: "bg-orange-500/15 text-orange-800 dark:text-orange-200",
        messageClassName: "text-orange-800 dark:text-orange-200",
        guidance: "Existing raw games remain available; try again later.",
    },
    Error: {
        label: "Failed",
        badgeClassName: "bg-red-500/15 text-red-700 dark:text-red-300",
        messageClassName: "text-red-700 dark:text-red-300",
        guidance: "Review the error and retry this platform.",
    },
    Failed: {
        label: "Failed",
        badgeClassName: "bg-red-500/15 text-red-700 dark:text-red-300",
        messageClassName: "text-red-700 dark:text-red-300",
        guidance: "Review the error and retry this platform.",
    },
};

const DEFAULT_INGEST_STATUS_PRESENTATION: IngestStatusPresentation = {
    label: "Unknown status",
    badgeClassName: "bg-muted text-muted-foreground",
    messageClassName: "text-muted-foreground",
    guidance: "Review the audit entry before retrying this platform.",
};

export function IngestionPanel() {
    const [credentials, setCredentials] = useState<PlatformCredentialManagement | null>(null);
    const [logs, setLogs] = useState<IngestAuditLog[]>([]);
    const [platformFilter, setPlatformFilter] = useState("all");
    const [loadingStatus, setLoadingStatus] = useState(true);
    const [loadingLogs, setLoadingLogs] = useState(true);
    const [busyPlatform, setBusyPlatform] = useState<Platform | null>(null);
    const [message, setMessage] = useState<string | null>(null);
    const [error, setError] = useState<string | null>(null);

    const fetchCredentialStatus = useCallback(async () => {
        setLoadingStatus(true);
        try {
            const response = await fetchAuthenticated("/api/core/platform-credentials", { cache: "no-store" });
            if (!response.ok) throw new Error(await readError(response));
            setCredentials(await response.json());
        } catch (caught) {
            setError(errorMessage(caught));
        } finally {
            setLoadingStatus(false);
        }
    }, []);

    const fetchLogs = useCallback(async () => {
        setLoadingLogs(true);
        try {
            const params = new URLSearchParams({ limit: "100" });
            if (platformFilter !== "all") params.set("platform", platformFilter);
            const response = await fetchAuthenticated(
                `/api/core/platform-credentials/retrieval-history?${params}`,
                { cache: "no-store" },
            );
            if (!response.ok) throw new Error(await readError(response));
            setLogs(await response.json());
        } catch (caught) {
            setError(errorMessage(caught));
        } finally {
            setLoadingLogs(false);
        }
    }, [platformFilter]);

    useEffect(() => {
        void fetchCredentialStatus();
    }, [fetchCredentialStatus]);

    useEffect(() => {
        void fetchLogs();
    }, [fetchLogs]);

    const retrieve = async (platform: Platform) => {
        setBusyPlatform(platform);
        setMessage(null);
        setError(null);
        try {
            const response = await fetchAuthenticated(`/api/core/platform-credentials/${platform}/retrieve`, {
                method: "POST",
            });
            if (!response.ok) throw new Error(await readError(response));
            const result = await response.json() as { message?: string };
            setMessage(result.message ?? `${platformLabel(platform)} retrieval completed.`);
            await Promise.all([fetchCredentialStatus(), fetchLogs()]);
        } catch (caught) {
            setError(errorMessage(caught));
        } finally {
            setBusyPlatform(null);
        }
    };

    const statusFor = (platform: Platform) =>
        credentials?.credentials.find((credential) => credential.platform === platform);

    return (
        <div className="space-y-8 animate-in fade-in slide-in-from-bottom-2 duration-300">
            <div>
                <h2 className="text-lg font-semibold tracking-tight">Stored platform retrieval</h2>
                <p className="mt-1 text-sm text-muted-foreground">
                    Fetch fresh raw platform data using the encrypted credentials from the Credentials tab.
                </p>
            </div>

            {error && (
                <div className="flex gap-2 rounded-lg border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
                    <ShieldAlert className="mt-0.5 h-4 w-4 shrink-0" />
                    {error}
                </div>
            )}
            {message && (
                <div className="rounded-lg border border-green-500/30 bg-green-500/10 px-4 py-3 text-sm text-green-700 dark:text-green-300">
                    {message}
                </div>
            )}

            <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
                {RETRIEVAL_PLATFORMS.map((platform) => (
                    <StoredRetrievalCard
                        key={platform}
                        platform={platform}
                        status={statusFor(platform)}
                        loading={loadingStatus}
                        busy={busyPlatform === platform}
                        blocked={busyPlatform !== null}
                        onRetrieve={() => retrieve(platform)}
                    />
                ))}
            </div>

            <div className="space-y-4">
                <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
                    <h3 className="text-lg font-semibold tracking-tight">Ingest audit log</h3>
                    <Select value={platformFilter} onValueChange={setPlatformFilter}>
                        <SelectTrigger className="w-full sm:w-40">
                            <SelectValue placeholder="Filter platform" />
                        </SelectTrigger>
                        <SelectContent>
                            <SelectItem value="all">All platforms</SelectItem>
                            <SelectItem value="Steam">Steam</SelectItem>
                            <SelectItem value="Gog">GOG</SelectItem>
                            <SelectItem value="Psn">PlayStation</SelectItem>
                            <SelectItem value="Epic">Epic Games</SelectItem>
                        </SelectContent>
                    </Select>
                </div>

                {loadingLogs ? (
                    <div className="flex items-center justify-center p-12 text-muted-foreground">
                        <Loader2 className="mr-2 h-5 w-5 animate-spin" /> Loading logs…
                    </div>
                ) : logs.length === 0 ? (
                    <div className="rounded-xl border border-dashed p-12 text-center text-sm text-muted-foreground">
                        No retrieval logs found for this tenant.
                    </div>
                ) : (
                    <AuditLogList logs={logs} />
                )}
            </div>
        </div>
    );
}

function StoredRetrievalCard({
    platform,
    status,
    loading,
    busy,
    blocked,
    onRetrieve,
}: {
    platform: Platform;
    status?: PlatformCredentialStatus;
    loading: boolean;
    busy: boolean;
    blocked: boolean;
    onRetrieve: () => Promise<void>;
}) {
    const configured = status?.isConfigured === true;
    return (
        <Card>
            <CardHeader>
                <div className="flex items-start justify-between gap-3">
                    <div>
                        <CardTitle className="flex items-center gap-2 text-base">
                            <span
                                className="h-3 w-3 rounded-full"
                                style={{ backgroundColor: getPlatformColor(platform) }}
                            />
                            {platformLabel(platform)}
                        </CardTitle>
                        <CardDescription className="mt-1">
                            Uses the tenant&apos;s encrypted stored credential.
                        </CardDescription>
                    </div>
                    <Badge variant={configured ? "default" : "outline"}>
                        {configured ? "Ready" : "Not configured"}
                    </Badge>
                </div>
            </CardHeader>
            <CardContent>
                <Button
                    className="w-full"
                    disabled={loading || blocked || !configured}
                    onClick={() => void onRetrieve()}
                >
                    {busy ? (
                        <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                    ) : (
                        <Download className="mr-2 h-4 w-4" />
                    )}
                    {busy ? "Retrieving…" : "Fetch & ingest"}
                </Button>
                {!loading && !configured && (
                    <p className="mt-2 text-xs text-muted-foreground">
                        Store this credential in the Credentials tab first.
                    </p>
                )}
            </CardContent>
        </Card>
    );
}

function AuditLogList({ logs }: { logs: IngestAuditLog[] }) {
    return (
        <>
            <div className="space-y-3 md:hidden">
                {logs.map((log) => (
                    <div key={log.id} className="rounded-xl border bg-card p-4 space-y-2">
                        <div className="flex items-start justify-between gap-2">
                            <Badge variant="secondary">{platformLabel(log.platform as Platform)}</Badge>
                            <span className="text-xs text-muted-foreground">
                                {new Date(log.timestamp).toLocaleString()}
                            </span>
                        </div>
                        <div className="flex items-center justify-between text-sm">
                            <IngestStatusBadge status={log.status} />
                            <span>{log.itemsProcessed} items</span>
                        </div>
                        <IngestStatusGuidance status={log.status} errorMessage={log.errorMessage} />
                    </div>
                ))}
            </div>
            <div className="hidden overflow-x-auto rounded-xl border bg-card md:block">
                <table className="w-full text-left text-sm">
                    <thead className="bg-muted/50 text-muted-foreground">
                        <tr>
                            <th className="px-4 py-3 font-medium">Timestamp</th>
                            <th className="px-4 py-3 font-medium">Platform</th>
                            <th className="px-4 py-3 font-medium">Status</th>
                            <th className="px-4 py-3 font-medium">Items</th>
                            <th className="px-4 py-3 font-medium">Error</th>
                        </tr>
                    </thead>
                    <tbody className="divide-y">
                        {logs.map((log) => (
                            <tr key={log.id}>
                                <td className="px-4 py-3 whitespace-nowrap text-muted-foreground">
                                    {new Date(log.timestamp).toLocaleString()}
                                </td>
                                <td className="px-4 py-3">{platformLabel(log.platform as Platform)}</td>
                                <td className="px-4 py-3"><IngestStatusBadge status={log.status} /></td>
                                <td className="px-4 py-3 tabular-nums">{log.itemsProcessed}</td>
                                <td className="max-w-[280px] px-4 py-3">
                                    <IngestStatusGuidance status={log.status} errorMessage={log.errorMessage} />
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            </div>
        </>
    );
}

function IngestStatusBadge({ status }: { status: string }) {
    const presentation = getIngestStatusPresentation(status);
    return (
        <span className={`inline-flex rounded-md px-2 py-1 text-xs font-medium ${presentation.badgeClassName}`}>
            {presentation.label}
        </span>
    );
}

function IngestStatusGuidance({
    status,
    errorMessage,
}: {
    status: string;
    errorMessage: string | null;
}) {
    const presentation = getIngestStatusPresentation(status);
    if (!presentation.guidance && !errorMessage) {
        return <span className="text-muted-foreground">—</span>;
    }

    return (
        <div className="space-y-1 text-xs">
            {presentation.guidance && (
                <p className={presentation.messageClassName}>{presentation.guidance}</p>
            )}
            {errorMessage && <p className="text-muted-foreground">{errorMessage}</p>}
        </div>
    );
}

function getIngestStatusPresentation(status: string): IngestStatusPresentation {
    return INGEST_STATUS_PRESENTATIONS[status] ?? {
        ...DEFAULT_INGEST_STATUS_PRESENTATION,
        label: status || DEFAULT_INGEST_STATUS_PRESENTATION.label,
    };
}

async function readError(response: Response): Promise<string> {
    try {
        const body = await response.json() as { message?: string };
        return body.message ?? `Request failed with status ${response.status}.`;
    } catch {
        return `Request failed with status ${response.status}.`;
    }
}

function errorMessage(error: unknown): string {
    return error instanceof Error ? error.message : "The retrieval request failed.";
}

function platformLabel(platform: Platform): string {
    if (platform === "Gog") return "GOG";
    if (platform === "Psn") return "PlayStation";
    if (platform === "Epic") return "Epic Games";
    return platform;
}
