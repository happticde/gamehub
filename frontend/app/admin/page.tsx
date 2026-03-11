"use client";

/**
 * /admin — Operations Dashboard
 *
 * This is a CLIENT component (note "use client" above) because the tab state
 * needs to live in the browser — the user clicks tabs and the component
 * re-renders to show the right panel without a full page reload.
 *
 * Three tabs:
 *   1. Enrichment — manage IGDB metadata enrichment (stats, actions, audit)
 *   2. Ingestion  — trigger platform data fetches (Steam, GOG, PSN)
 *   3. Sync       — trigger and review unification syncs
 */

import { Tabs, TabsList, TabsTrigger, TabsContent } from "@/components/ui/tabs";
import { EnrichmentPanel } from "@/components/admin/EnrichmentPanel";
import { IngestionPanel } from "@/components/admin/IngestionPanel";
import { SyncPanel } from "@/components/admin/SyncPanel";
import { Settings, Sparkles, Download, RefreshCw } from "lucide-react";

export default function AdminPage() {
    return (
        <div className="min-h-screen bg-background">
            <main className="container mx-auto px-4 md:px-8 py-8 md:py-12 max-w-screen-xl animate-in fade-in slide-in-from-bottom-4 duration-500">
                {/* Page header */}
                <header className="mb-10 border-b pb-8">
                    <h1 className="text-2xl md:text-4xl font-extrabold tracking-tight lg:text-5xl flex items-center gap-3 md:gap-4">
                        <Settings className="h-7 w-7 md:h-10 md:w-10 text-primary shrink-0" />
                        Admin Dashboard
                    </h1>
                    <p className="mt-4 text-lg text-muted-foreground max-w-2xl">
                        Manage enrichment, ingest platform libraries, and monitor sync operations for your unified game collection.
                    </p>
                </header>

                {/* Tabbed interface — defaulting to the Enrichment tab */}
                <Tabs defaultValue="enrichment" className="space-y-6">
                    <TabsList>
                        <TabsTrigger value="enrichment" className="gap-1.5">
                            <Sparkles className="h-4 w-4" />
                            Enrichment
                        </TabsTrigger>
                        <TabsTrigger value="ingestion" className="gap-1.5">
                            <Download className="h-4 w-4" />
                            Ingestion
                        </TabsTrigger>
                        <TabsTrigger value="sync" className="gap-1.5">
                            <RefreshCw className="h-4 w-4" />
                            Sync
                        </TabsTrigger>
                    </TabsList>

                    <TabsContent value="enrichment">
                        <EnrichmentPanel />
                    </TabsContent>

                    <TabsContent value="ingestion">
                        <IngestionPanel />
                    </TabsContent>

                    <TabsContent value="sync">
                        <SyncPanel />
                    </TabsContent>
                </Tabs>
            </main>
        </div>
    );
}
