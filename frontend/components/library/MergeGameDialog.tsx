"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { AlertCircle, ArrowLeft, GitMerge, Loader2, Search } from "lucide-react";
import {
    Dialog,
    DialogContent,
    DialogDescription,
    DialogFooter,
    DialogHeader,
    DialogTitle,
    DialogTrigger,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { getErrorMessage } from "@/lib/errors";
import { fetchAuthenticated } from "@/lib/authenticated-fetch";
import { getPlatformColor } from "@/lib/platform-colors";
import type { ApiMessageResponse } from "@/types/api";
import type { Platform } from "@/types/game";

interface MergeGameDialogProps {
    sourceGameId: number;
    sourceTitle: string;
    sourcePlatforms: Platform[];
}

interface MergeTarget {
    id: number;
    title: string;
    firstReleaseDate: string | null;
    platforms: {
        platform: Platform;
        externalId: string;
    }[];
}

interface MergeSearchResponse {
    items: MergeTarget[];
    totalCount: number;
}

interface MergeGameResponse extends ApiMessageResponse {
    targetGameId?: number;
}

export default function MergeGameDialog({
    sourceGameId,
    sourceTitle,
    sourcePlatforms,
}: MergeGameDialogProps) {
    const router = useRouter();
    const [open, setOpen] = useState(false);
    const [query, setQuery] = useState(sourceTitle);
    const [results, setResults] = useState<MergeTarget[]>([]);
    const [totalCount, setTotalCount] = useState(0);
    const [selectedTarget, setSelectedTarget] = useState<MergeTarget | null>(null);
    const [isSearching, setIsSearching] = useState(false);
    const [isMerging, setIsMerging] = useState(false);
    const [searchError, setSearchError] = useState<string | null>(null);
    const [mergeError, setMergeError] = useState<string | null>(null);
    const searchEnabled = open && !selectedTarget && Boolean(query.trim());
    const searchKey = JSON.stringify([searchEnabled, query, sourceGameId]);
    const [previousSearchKey, setPreviousSearchKey] = useState(searchKey);

    if (previousSearchKey !== searchKey) {
        setPreviousSearchKey(searchKey);
        setResults([]);
        setTotalCount(0);
        setSearchError(null);
        setIsSearching(searchEnabled);
    }

    useEffect(() => {
        const trimmedQuery = query.trim();
        if (!open || selectedTarget || !trimmedQuery) {
            return;
        }

        const abortController = new AbortController();

        const timer = window.setTimeout(async () => {
            try {
                const response = await fetchAuthenticated(
                    `/api/core/library?search=${encodeURIComponent(trimmedQuery)}&includeIgnored=true&pageSize=10`,
                    { cache: "no-store", signal: abortController.signal },
                );
                if (!response.ok) {
                    throw new Error("Game search failed. Please try again.");
                }

                const data: MergeSearchResponse = await response.json();
                if (abortController.signal.aborted) return;
                setResults((data.items ?? []).filter((game) => game.id !== sourceGameId));
                setTotalCount(data.totalCount ?? 0);
            } catch (caughtError: unknown) {
                if (abortController.signal.aborted) return;
                setSearchError(getErrorMessage(caughtError, "Game search failed. Please try again."));
            } finally {
                if (!abortController.signal.aborted) setIsSearching(false);
            }
        }, 300);

        return () => {
            window.clearTimeout(timer);
            abortController.abort();
        };
    }, [open, query, selectedTarget, sourceGameId]);

    const handleOpenChange = (nextOpen: boolean) => {
        if (isMerging) return;

        setOpen(nextOpen);
        if (nextOpen) {
            setQuery(sourceTitle);
            setSelectedTarget(null);
            setSearchError(null);
            setMergeError(null);
        }
    };

    const handleMerge = async () => {
        if (!selectedTarget || isMerging) return;

        setIsMerging(true);
        setMergeError(null);

        try {
            const response = await fetchAuthenticated("/api/core/library/merge", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    sourceGameId,
                    targetGameId: selectedTarget.id,
                }),
            });
            const data = await response.json().catch(() => null) as MergeGameResponse | null;

            if (!response.ok) {
                throw new Error(data?.message ?? data?.Message ?? "The games could not be merged.");
            }

            setOpen(false);
            router.push(`/library/${data?.targetGameId ?? selectedTarget.id}`);
            router.refresh();
        } catch (caughtError: unknown) {
            setMergeError(getErrorMessage(caughtError, "The games could not be merged."));
            setIsMerging(false);
        }
    };

    const conflictingPlatforms = selectedTarget
        ? Array.from(new Set(
            sourcePlatforms.filter((platform) =>
                selectedTarget.platforms.some((item) => item.platform === platform),
            ),
        ))
        : [];

    return (
        <Dialog open={open} onOpenChange={handleOpenChange}>
            <DialogTrigger asChild>
                <Button type="button" variant="outline" size="sm" className="h-8 w-full gap-1.5">
                    <GitMerge />
                    Merge into…
                </Button>
            </DialogTrigger>
            <DialogContent
                className="flex max-h-[85vh] w-[calc(100%-2rem)] flex-col overflow-hidden sm:max-w-xl"
                showCloseButton={!isMerging}
            >
                {selectedTarget ? (
                    <>
                        <DialogHeader>
                            <DialogTitle>Merge these games?</DialogTitle>
                            <DialogDescription>
                                Review which game will remain before applying this permanent library change.
                            </DialogDescription>
                        </DialogHeader>

                        <div className="grid gap-3 sm:grid-cols-2">
                            <GameSummary label="Source — will be deleted" title={sourceTitle} platforms={sourcePlatforms} />
                            <GameSummary
                                label="Target — will be kept"
                                title={selectedTarget.title}
                                platforms={selectedTarget.platforms.map((item) => item.platform)}
                            />
                        </div>

                        <div className="space-y-3 overflow-y-auto pr-1 text-sm">
                            <div className="rounded-lg border border-border bg-muted/30 p-3 text-muted-foreground">
                                The target game&apos;s title, metadata, tracking, and tag assignments will be kept.
                                Platform entries from <span className="font-medium text-foreground">{sourceTitle}</span>{" "}
                                will move to it, and future syncs will respect the merged assignment.
                            </div>

                            {conflictingPlatforms.length > 0 && (
                                <div
                                    className="flex items-start gap-2 rounded-lg border border-amber-500/40 bg-amber-500/10 p-3 text-amber-800 dark:text-amber-200"
                                    role="alert"
                                >
                                    <AlertCircle className="mt-0.5 shrink-0" />
                                    <div>
                                        <p className="font-semibold">Platform conflict</p>
                                        <p className="mt-1 text-xs leading-relaxed">
                                            Both games contain {formatPlatformList(conflictingPlatforms)}. GameHub keeps the
                                            target game&apos;s entry for each conflicting platform and removes the source entry.
                                        </p>
                                    </div>
                                </div>
                            )}

                            {mergeError && (
                                <div
                                    className="flex items-start gap-2 rounded-lg border border-destructive/40 bg-destructive/10 p-3 text-destructive"
                                    role="alert"
                                >
                                    <AlertCircle className="mt-0.5 shrink-0" />
                                    <span>{mergeError}</span>
                                </div>
                            )}
                        </div>

                        <DialogFooter>
                            <Button
                                type="button"
                                variant="outline"
                                onClick={() => {
                                    setSelectedTarget(null);
                                    setMergeError(null);
                                }}
                                disabled={isMerging}
                            >
                                <ArrowLeft />
                                Back
                            </Button>
                            <Button
                                type="button"
                                variant="destructive"
                                onClick={() => void handleMerge()}
                                disabled={isMerging}
                            >
                                {isMerging ? <Loader2 className="animate-spin" /> : <GitMerge />}
                                {isMerging ? "Merging…" : "Merge games"}
                            </Button>
                        </DialogFooter>
                    </>
                ) : (
                    <>
                        <DialogHeader>
                            <DialogTitle>Merge into another game</DialogTitle>
                            <DialogDescription>
                                Search for the game that should remain. This game will become the source and be removed.
                            </DialogDescription>
                        </DialogHeader>

                        <div className="relative shrink-0">
                            <Search className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-muted-foreground" />
                            <Input
                                value={query}
                                onChange={(event) => setQuery(event.target.value)}
                                placeholder="Search by game title…"
                                className="h-10 pl-8"
                                aria-label="Search for the target game"
                            />
                        </div>

                        {searchError && (
                            <div
                                className="flex items-start gap-2 rounded-lg border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive"
                                role="alert"
                            >
                                <AlertCircle className="mt-0.5 shrink-0" />
                                <span>{searchError}</span>
                            </div>
                        )}

                        <div className="min-h-48 flex-1 space-y-2 overflow-y-auto pr-1">
                            {isSearching ? (
                                <div className="flex min-h-48 items-center justify-center text-muted-foreground">
                                    <Loader2 className="animate-spin" />
                                    <span className="ml-2">Searching your library…</span>
                                </div>
                            ) : !query.trim() ? (
                                <EmptySearchState message="Enter a title to find the target game." />
                            ) : results.length === 0 && !searchError ? (
                                <EmptySearchState message={`No other games found for “${query.trim()}”.`} />
                            ) : (
                                results.map((game) => (
                                    <button
                                        key={game.id}
                                        type="button"
                                        onClick={() => {
                                            setSelectedTarget(game);
                                            setMergeError(null);
                                        }}
                                        className="w-full rounded-lg border border-border p-3 text-left transition-colors hover:bg-muted/50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                                    >
                                        <div className="flex items-start justify-between gap-3">
                                            <div className="min-w-0">
                                                <p className="truncate font-semibold text-foreground">{game.title}</p>
                                                <p className="mt-0.5 text-xs text-muted-foreground">
                                                    {game.firstReleaseDate
                                                        ? new Date(game.firstReleaseDate).getFullYear()
                                                        : "Unknown year"}
                                                    {" · "}Game #{game.id}
                                                </p>
                                            </div>
                                            <GitMerge className="mt-0.5 shrink-0 text-muted-foreground" />
                                        </div>
                                        <PlatformBadges platforms={game.platforms.map((item) => item.platform)} />
                                    </button>
                                ))
                            )}
                        </div>

                        {totalCount > 10 && (
                            <p className="shrink-0 text-xs text-muted-foreground">
                                More matches exist. Refine the title to narrow the results.
                            </p>
                        )}
                    </>
                )}
            </DialogContent>
        </Dialog>
    );
}

function GameSummary({ label, title, platforms }: { label: string; title: string; platforms: Platform[] }) {
    return (
        <div className="min-w-0 rounded-lg border border-border bg-muted/20 p-3">
            <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">{label}</p>
            <p className="mt-1 truncate font-semibold" title={title}>{title}</p>
            <PlatformBadges platforms={platforms} />
        </div>
    );
}

function PlatformBadges({ platforms }: { platforms: Platform[] }) {
    const uniquePlatforms = Array.from(new Set(platforms));

    return uniquePlatforms.length > 0 ? (
        <div className="mt-2 flex flex-wrap gap-1.5">
            {uniquePlatforms.map((platform) => (
                <span
                    key={platform}
                    className={`inline-flex rounded-full px-2 py-0.5 text-[10px] font-semibold ${getPlatformColor(platform)}`}
                >
                    {platform}
                </span>
            ))}
        </div>
    ) : (
        <p className="mt-2 text-xs text-muted-foreground">No platform entries</p>
    );
}

function EmptySearchState({ message }: { message: string }) {
    return (
        <div className="flex min-h-48 items-center justify-center rounded-lg border border-dashed border-border p-6 text-center text-sm text-muted-foreground">
            {message}
        </div>
    );
}

function formatPlatformList(platforms: Platform[]): string {
    if (platforms.length === 1) return platforms[0];
    return `${platforms.slice(0, -1).join(", ")} and ${platforms.at(-1)}`;
}
