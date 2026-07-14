"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogTrigger, DialogDescription } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Search, Loader2, Link2 } from "lucide-react";
import { MetadataSearchResultCard } from "@/components/library/MetadataSearchResultCard";
import { getErrorMessage } from "@/lib/errors";
import type { ApiMessageResponse } from "@/types/api";
import type { MetadataSearchResult } from "@/types/game";

export default function FixMatchDialog({ gameId, initialTitle }: { gameId: number, initialTitle: string }) {
    const router = useRouter();
    const [open, setOpen] = useState(false);
    const [query, setQuery] = useState(initialTitle);
    const [results, setResults] = useState<MetadataSearchResult[]>([]);
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

    const handleSelect = async (providerId: number) => {
        setIsApplying(providerId);
        setError(null);

        try {
            const res = await fetch(`/api/core/library/${gameId}/enrichment/override`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ providerId }),
            });

            if (!res.ok) {
                const data: ApiMessageResponse = await res.json();
                throw new Error(data?.message || "Failed to override.");
            }

            setOpen(false);
            router.refresh(); // Refresh page to reflect new data
        } catch (caughtError: unknown) {
            setError(getErrorMessage(caughtError, "An error occurred applying the match."));
            setIsApplying(null);
        }
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
                    <DialogDescription>Search for the correct game on IGDB to update this entry&apos;s metadata.</DialogDescription>
                </DialogHeader>

                <form onSubmit={handleSearch} className="flex flex-col gap-4 mt-2 flex-1 overflow-hidden">
                    <div className="flex gap-2 shrink-0">
                        <Input
                            placeholder="Enter game title or IGDB ID..."
                            value={query}
                            onChange={(e) => setQuery(e.target.value)}
                        />
                        <Button type="submit" disabled={isSearching || !query.trim()}>
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
                                    onAction={() => handleSelect(result.providerId)}
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
