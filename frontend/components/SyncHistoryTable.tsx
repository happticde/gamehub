import { Platform } from "@/types/game";

interface SyncAuditLog {
    id: number;
    timestamp: string;
    platform: Platform;
    status: "Success" | "Failed" | "Skipped" | "In_Progress";
    itemsProcessed: number;
    newGamesCreated: number;
    errorMessage: string | null;
}

interface SyncHistoryTableProps {
    logs: SyncAuditLog[];
}

export function SyncHistoryTable({ logs }: SyncHistoryTableProps) {
    if (!logs || logs.length === 0) {
        return (
            <div className="flex flex-col items-center justify-center rounded-xl border border-dashed p-12 text-center text-muted-foreground">
                <p>No unification sync history found.</p>
                <p className="text-sm mt-1">Trigger a manual sync to populate the audit logs.</p>
            </div>
        );
    }

    return (
        <>
            {/* Mobile: Card layout */}
            <div className="md:hidden space-y-3">
                {logs.map((log) => (
                    <div key={log.id} className="rounded-xl border border-border bg-card p-4 space-y-2.5">
                        <div className="flex items-start justify-between gap-2">
                            <div className="flex items-center gap-2">
                                <span className="inline-flex items-center rounded-full bg-secondary px-2.5 py-0.5 text-xs text-secondary-foreground">
                                    {log.platform || "System"}
                                </span>
                                <StatusBadge status={log.status} error={log.errorMessage} />
                            </div>
                            <span className="text-xs text-muted-foreground whitespace-nowrap">
                                {new Date(log.timestamp).toLocaleString(undefined, {
                                    month: "short", day: "numeric",
                                    hour: "2-digit", minute: "2-digit",
                                })}
                            </span>
                        </div>
                        <div className="flex items-center gap-4 text-sm">
                            <span className="text-muted-foreground">Swept: <span className="font-medium tabular-nums text-foreground">{log.itemsProcessed}</span></span>
                            <span className="text-muted-foreground">New: {log.newGamesCreated > 0 ? (
                                <span className="text-green-500 font-medium tabular-nums">+{log.newGamesCreated}</span>
                            ) : (
                                <span className="tabular-nums">0</span>
                            )}</span>
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
                                <th className="px-4 py-3 font-medium">Target Platform</th>
                                <th className="px-4 py-3 font-medium">Status</th>
                                <th className="px-4 py-3 font-medium">Items Swept</th>
                                <th className="px-4 py-3 font-medium">New Games Created</th>
                            </tr>
                        </thead>
                        <tbody className="divide-y divide-border">
                            {logs.map((log) => (
                                <tr key={log.id} className="hover:bg-muted/30 transition-colors">
                                    <td className="px-4 py-3 whitespace-nowrap text-muted-foreground">
                                        {new Date(log.timestamp).toLocaleString(undefined, {
                                            month: 'short',
                                            day: 'numeric',
                                            hour: '2-digit',
                                            minute: '2-digit'
                                        })}
                                    </td>
                                    <td className="px-4 py-3 font-medium">
                                        <span className="inline-flex items-center rounded-full bg-secondary px-2.5 py-0.5 text-xs text-secondary-foreground">
                                            {log.platform || "System"}
                                        </span>
                                    </td>
                                    <td className="px-4 py-3">
                                        <StatusBadge status={log.status} error={log.errorMessage} />
                                    </td>
                                    <td className="px-4 py-3 whitespace-nowrap tabular-nums font-medium">
                                        {log.itemsProcessed}
                                    </td>
                                    <td className="px-4 py-3 whitespace-nowrap tabular-nums text-muted-foreground">
                                        {log.newGamesCreated > 0 ? (
                                            <span className="text-green-500 font-medium">+{log.newGamesCreated}</span>
                                        ) : (
                                            "0"
                                        )}
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            </div>
        </>
    );
}

function StatusBadge({ status, error }: { status: string, error: string | null }) {
    switch (status) {
        case "Success":
            return (
                <span className="inline-flex items-center rounded-md bg-green-500/15 px-2 py-1 text-xs font-medium text-green-600 dark:text-green-400 ring-1 ring-inset ring-green-500/20">
                    Success
                </span>
            );
        case "Failed":
            return (
                <div className="flex flex-col items-start">
                    <span className="inline-flex items-center rounded-md bg-red-500/15 px-2 py-1 text-xs font-medium text-red-600 dark:text-red-400 ring-1 ring-inset ring-red-500/20">
                        Failed
                    </span>
                    {error && <span className="text-[10px] text-red-500 mt-1 max-w-[200px] truncate" title={error}>{error}</span>}
                </div>
            );
        case "Skipped":
            return (
                <span className="inline-flex items-center rounded-md bg-yellow-500/15 px-2 py-1 text-xs font-medium text-yellow-600 dark:text-yellow-400 ring-1 ring-inset ring-yellow-500/20">
                    Skipped
                </span>
            );
        default:
            return (
                <span className="inline-flex items-center rounded-md bg-zinc-500/15 px-2 py-1 text-xs font-medium text-zinc-600 dark:text-zinc-400 ring-1 ring-inset ring-zinc-500/20">
                    {status.replace("_", " ")}
                </span>
            );
    }
}
