import { SyncHistoryTable } from "@/components/SyncHistoryTable";
import { SyncTriggerButton } from "@/components/SyncTriggerButton";
import { Server } from "lucide-react";

// Prevent static caching of this dashboard since audit logs change rapidly
export const dynamic = "force-dynamic";

export default async function IngestDashboardPage() {
    let logs = [];
    let error = null;

    try {
        // Fetch server-side directly from the Docker internal network or via localhost depending on Devcontainer mapping
        const backendHost = process.env.BACKEND_HOST || "host.docker.internal";
        const res = await fetch(`http://${backendHost}:5001/api/library/sync-history?limit=100`, {
            cache: "no-store"
        });

        if (!res.ok) {
            error = `Failed to fetch history (Status ${res.status})`;
        } else {
            logs = await res.json();
        }
    } catch (err: any) {
        console.error("Error fetching sync history:", err);
        error = err.message || "Unknown error occurred while fetching sync history.";
    }

    return (
        <div className="min-h-screen bg-background">
            <main className="container mx-auto px-8 py-12 max-w-screen-xl animate-in fade-in slide-in-from-bottom-4 duration-500">
                <header className="mb-10 flex flex-col items-start gap-4 border-b pb-8 sm:flex-row sm:items-center sm:justify-between">
                    <div>
                        <h1 className="text-4xl font-extrabold tracking-tight lg:text-5xl flex items-center gap-4">
                            <Server className="h-10 w-10 text-primary" />
                            Ingest & Sync
                        </h1>
                        <p className="mt-4 text-lg text-muted-foreground max-w-2xl">
                            Unify game libraries from your connected providers. Triggers a backend sweep to merge new Steam, GOG, PSN, and Epic purchases into a single collection.
                        </p>
                    </div>

                    <div className="shrink-0 mt-4 sm:mt-0">
                        <SyncTriggerButton />
                    </div>
                </header>

                <div className="space-y-6">
                    <div className="flex items-center justify-between">
                        <h2 className="text-2xl font-semibold tracking-tight">Recent Unification Logs</h2>
                        <span className="text-sm text-muted-foreground">Showing last 100 entries</span>
                    </div>

                    {error ? (
                        <div className="rounded-xl border border-destructive/50 bg-destructive/10 p-6 text-destructive">
                            <h3 className="font-semibold mb-2">Failed to load sync history</h3>
                            <p className="text-sm font-mono">{error}</p>
                            <p className="text-sm mt-4 text-muted-foreground">Make sure the C# Core API is running on port 5001 and is accessible.</p>
                        </div>
                    ) : (
                        <SyncHistoryTable logs={logs} />
                    )}
                </div>
            </main>
        </div>
    );
}
