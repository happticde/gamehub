"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import Image from "next/image";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogTrigger, DialogDescription } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Search, Loader2, Link2 } from "lucide-react";

interface SearchResult {
    providerId: number;
    matchedTitle: string;
    coverUrl: string | null;
    firstReleaseDate: string | null;
    summary: string | null;
}

export default function FixMatchDialog({ gameId, initialTitle }: { gameId: number, initialTitle: string }) {
    const router = useRouter();
    const [open, setOpen] = useState(false);
    const [query, setQuery] = useState(initialTitle);
    const [results, setResults] = useState<SearchResult[]>([]);
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
            const data = await res.json();
            setResults(data || []);
        } catch (err: any) {
            setError(err.message || "Failed to search.");
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
                const data = await res.json();
                throw new Error(data?.message || "Failed to override.");
            }

            setOpen(false);
            router.refresh(); // Refresh page to reflect new data
        } catch (err: any) {
            setError(err.message || "An error occurred applying the match.");
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
                            results.map((r) => (
                                <div key={r.providerId} className="flex gap-4 p-3 border rounded-lg hover:bg-muted/50 transition-colors">
                                    <div className="w-16 h-20 bg-muted rounded overflow-hidden shrink-0 relative">
                                        {r.coverUrl ? (
                                            <Image src={r.coverUrl} alt="Cover" fill className="object-cover" unoptimized />
                                        ) : (
                                            <div className="flex items-center justify-center h-full text-xs text-muted-foreground">No img</div>
                                        )}
                                    </div>
                                    <div className="flex-1 min-w-0">
                                        <h4 className="font-semibold text-sm truncate">{r.matchedTitle || "Unknown Title"}</h4>
                                        <div className="text-xs text-muted-foreground mb-2">
                                            {r.firstReleaseDate ? new Date(r.firstReleaseDate).getFullYear() : "Unknown Year"} • ID: {r.providerId}
                                        </div>
                                        <div className="text-xs text-muted-foreground line-clamp-2">
                                            {r.summary || "No description available."}
                                        </div>
                                    </div>
                                    <div className="flex items-center">
                                        <Button
                                            type="button"
                                            size="sm"
                                            onClick={() => handleSelect(r.providerId)}
                                            disabled={isApplying !== null}
                                        >
                                            {isApplying === r.providerId ? <Loader2 className="h-4 w-4 animate-spin" /> : "Select"}
                                        </Button>
                                    </div>
                                </div>
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
