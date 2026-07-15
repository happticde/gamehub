"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogTrigger, DialogDescription } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Search, Loader2, Link2 } from "lucide-react";
import { MetadataSearchResultCard } from "@/components/library/MetadataSearchResultCard";
import { getErrorMessage } from "@/lib/errors";
import { parseIgdbProviderId } from "@/lib/igdb-provider-id";
import type { ApiMessageResponse } from "@/types/api";
import type { MetadataSearchResult } from "@/types/game";

export default function FixMatchDialog({ gameId, initialTitle }: { gameId: number, initialTitle: string }) {
    const router = useRouter();
    const [open, setOpen] = useState(false);
    const [query, setQuery] = useState(initialTitle);
    const [results, setResults] = useState<MetadataSearchResult[]>([]);
    const [directProviderId, setDirectProviderId] = useState("");
    const [isSearching, setIsSearching] = useState(false);
    const [isApplying, setIsApplying] = useState<number | null>(null);
    const [error, setError] = useState<string | null>(null);

    const handleSearch = async (e?: React.FormEvent) => {
        if (e) e.preventDefault();
        if (!query.trim()) return;

        setIsSearching(true);
        setError(null);
        setResults([]);

        try {
            const res = await fetch(`/api/core/library/enrichment/search?title=${encodeURIComponent(query)}&limit=10`);
            if (!res.ok) throw new Error("Search failed");
            const data: MetadataSearchResult[] = await res.json();
            setResults(data || []);
        } catch (caughtError: unknown) {
            setError(getErrorMessage(caughtError, "Failed to search."));
        } finally {
            setIsSearching(false);
        }
    };

    const handleApplyProviderId = async (providerId: number) => {
        setIsApplying(providerId);
        setError(null);

        try {
            const res = await fetch(`/api/core/library/${gameId}/enrichment/override`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ providerId }),
            });

            if (!res.ok) {
                const data: ApiMessageResponse = await res.json().catch(() => ({}));
                throw new Error(data.message ?? data.Message ?? "Failed to override.");
            }

            setIsApplying(null);
            setDirectProviderId("");
            setOpen(false);
            router.refresh(); // Refresh page to reflect new data
        } catch (caughtError: unknown) {
            setError(getErrorMessage(caughtError, "An error occurred applying the match."));
            setIsApplying(null);
        }
    };

    const handleDirectProviderId = async (e: React.FormEvent) => {
        e.preventDefault();
        const providerId = parseIgdbProviderId(directProviderId);
        if (providerId === null) {
            setError("Enter a positive IGDB ID no larger than 2147483647.");
            return;
        }

        await handleApplyProviderId(providerId);
    };

    return (
        <Dialog open={open} onOpenChange={(val) => {
            setOpen(val);
            if (val && results.length === 0) {
                // Auto-search on open if haven't yet
                handleSearch();
            }
        }}>
            <DialogTrigger asChild>
                <Button variant="outline" size="sm" className="gap-2 shrink-0 h-8">
                    <Link2 className="h-4 w-4" /> Fix Match
                </Button>
            </DialogTrigger>
            <DialogContent className="sm:max-w-[600px] max-h-[85vh] flex flex-col">
                <DialogHeader>
                    <DialogTitle>Manual Metadata Match</DialogTitle>
                    <DialogDescription>Use an exact IGDB ID or search for the correct game before updating this entry&apos;s metadata.</DialogDescription>
                </DialogHeader>

                <section className="rounded-lg border bg-muted/30 p-3">
                    <label htmlFor="direct-igdb-id" className="text-sm font-medium">
                        Direct IGDB ID
                    </label>
                    <p className="mt-1 text-xs text-muted-foreground">
                        Core verifies the exact ID before changing any metadata.
                    </p>
                    <form onSubmit={handleDirectProviderId} className="mt-3 flex gap-2">
                        <Input
                            id="direct-igdb-id"
                            inputMode="numeric"
                            autoComplete="off"
                            placeholder="e.g. 1942"
                            value={directProviderId}
                            onChange={(e) => setDirectProviderId(e.target.value)}
                            disabled={isApplying !== null}
                        />
                        <Button type="submit" disabled={isApplying !== null || !directProviderId.trim()}>
                            {isApplying !== null && parseIgdbProviderId(directProviderId) === isApplying ? (
                                <Loader2 className="h-4 w-4 animate-spin" />
                            ) : null}
                            Apply ID
                        </Button>
                    </form>
                </section>

                <div className="flex items-center gap-3 text-xs uppercase tracking-wide text-muted-foreground">
                    <span className="h-px flex-1 bg-border" />
                    Search by title
                    <span className="h-px flex-1 bg-border" />
                </div>

                <form onSubmit={handleSearch} className="flex flex-col gap-4 mt-2 flex-1 overflow-hidden">
                    <div className="flex gap-2 shrink-0">
                        <Input
                            placeholder="Enter a game title..."
                            value={query}
                            onChange={(e) => setQuery(e.target.value)}
                            disabled={isApplying !== null}
                        />
                        <Button type="submit" disabled={isSearching || isApplying !== null || !query.trim()}>
                            {isSearching ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
                        </Button>
                    </div>

                    {error && <div className="text-sm text-destructive shrink-0">{error}</div>}

                    <div className="flex-1 overflow-y-auto space-y-3 min-h-[300px] pr-2 pb-6">
                        {isSearching ? (
                            <div className="flex justify-center py-10">
                                <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
                            </div>
                        ) : results && results.length > 0 ? (
                            results.map((result) => (
                                <MetadataSearchResultCard
                                    key={result.providerId}
                                    result={result}
                                    actionLabel="Select"
                                    onAction={() => handleApplyProviderId(result.providerId)}
                                    pending={isApplying === result.providerId}
                                    disabled={isApplying !== null}
                                />
                            ))
                        ) : !isSearching && query !== initialTitle ? (
                            <div className="text-center py-10 text-muted-foreground">No results found for &quot;{query}&quot;.</div>
                        ) : null}
                    </div>
                </form>
            </DialogContent>
        </Dialog>
    );
}
