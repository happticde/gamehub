"use client";

import { useSyncExternalStore } from "react";
import { UnifiedGame } from "@/types/game";
import { LayoutGrid, Grid3X3, List } from "lucide-react";
import GameCard from "./GameCard";
import GameCardCompact from "./GameCardCompact";
import GameListRow from "./GameListRow";

type ViewMode = "grid" | "compact" | "list";

const DEFAULT_VIEW_MODE: ViewMode = "grid";
const VIEW_MODE_STORAGE_KEY = "libraryView";
const VIEW_MODE_CHANGE_EVENT = "gamehub:library-view-change";
const VIEW_MODES: readonly ViewMode[] = ["grid", "compact", "list"];

function isViewMode(value: string | null): value is ViewMode {
    return value !== null && VIEW_MODES.some((mode) => mode === value);
}

function getStoredViewMode(): ViewMode {
    const savedView = localStorage.getItem(VIEW_MODE_STORAGE_KEY);
    return isViewMode(savedView) ? savedView : DEFAULT_VIEW_MODE;
}

function subscribeToViewMode(onStoreChange: () => void): () => void {
    const handleStorage = (event: StorageEvent) => {
        if (event.key === VIEW_MODE_STORAGE_KEY) onStoreChange();
    };

    window.addEventListener("storage", handleStorage);
    window.addEventListener(VIEW_MODE_CHANGE_EVENT, onStoreChange);

    return () => {
        window.removeEventListener("storage", handleStorage);
        window.removeEventListener(VIEW_MODE_CHANGE_EVENT, onStoreChange);
    };
}

const subscribeToHydration = () => () => {};
const getHydratedSnapshot = () => true;
const getServerHydrationSnapshot = () => false;
const getServerViewModeSnapshot = () => DEFAULT_VIEW_MODE;

interface LibraryViewControlsProps {
    games: UnifiedGame[];
    filters: React.ReactNode;
    pageSizeControl: React.ReactNode;
}

export default function LibraryViewControls({ games, filters, pageSizeControl }: LibraryViewControlsProps) {
    const viewMode = useSyncExternalStore(
        subscribeToViewMode,
        getStoredViewMode,
        getServerViewModeSnapshot
    );
    const mounted = useSyncExternalStore(
        subscribeToHydration,
        getHydratedSnapshot,
        getServerHydrationSnapshot
    );

    // The custom event updates this tab; the storage event keeps other tabs synchronized.
    const updateViewMode = (mode: ViewMode) => {
        localStorage.setItem(VIEW_MODE_STORAGE_KEY, mode);
        window.dispatchEvent(new Event(VIEW_MODE_CHANGE_EVENT));
    };

    return (
        <div className="animate-in slide-in-from-bottom-4 fade-in duration-500">
            
            {/* Toolbar row with the Filters AND View Toggle switch */}
            <div className="flex w-full flex-col xl:flex-row xl:items-end justify-between gap-4 mb-8">
                <div className="flex-1 w-full xl:w-auto">
                    {filters}
                </div>

                <div className="flex flex-wrap items-center gap-3 self-start xl:self-auto">
                    {pageSizeControl}

                    <div className="inline-flex h-10 shrink-0 items-center justify-center rounded-md bg-muted p-1 text-muted-foreground">
                        <button
                            onClick={() => updateViewMode("grid")}
                            className={`inline-flex items-center justify-center whitespace-nowrap rounded-sm px-3 py-1.5 text-sm font-medium ring-offset-background transition-all focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 ${
                                viewMode === "grid" ? "bg-background text-foreground shadow-sm" : "hover:bg-background/50"
                            }`}
                            title="Grid View"
                        >
                            <LayoutGrid className="h-4 w-4 mr-2" />
                            Grid
                        </button>
                        <button
                            onClick={() => updateViewMode("compact")}
                            className={`inline-flex items-center justify-center whitespace-nowrap rounded-sm px-3 py-1.5 text-sm font-medium ring-offset-background transition-all focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 ${
                                viewMode === "compact" ? "bg-background text-foreground shadow-sm" : "hover:bg-background/50"
                            }`}
                            title="Compact View"
                        >
                            <Grid3X3 className="h-4 w-4 mr-2" />
                            Compact
                        </button>
                        <button
                            onClick={() => updateViewMode("list")}
                            className={`inline-flex items-center justify-center whitespace-nowrap rounded-sm px-3 py-1.5 text-sm font-medium ring-offset-background transition-all focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 ${
                                viewMode === "list" ? "bg-background text-foreground shadow-sm" : "hover:bg-background/50"
                            }`}
                            title="List View"
                        >
                            <List className="h-4 w-4 mr-2" />
                            List
                        </button>
                    </div>
                </div>
            </div>

            {/* Games Render or Empty State */}
            {games.length === 0 ? (
                <div className="flex min-h-[400px] flex-col items-center justify-center rounded-lg border border-border bg-card p-10 text-center animate-in fade-in duration-700 shadow-sm">
                    <div className="rounded-full bg-muted p-4">
                        <svg className="h-8 w-8 text-muted-foreground" fill="none" strokeWidth="1.5" stroke="currentColor" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">
                            <path d="M2.25 15.75l5.159-5.159a2.25 2.25 0 013.182 0l5.159 5.159m-1.5-1.5l1.409-1.409a2.25 2.25 0 013.182 0l2.909 2.909m-18 3.75h16.5a1.5 1.5 0 001.5-1.5V6a1.5 1.5 0 00-1.5-1.5H3.75A1.5 1.5 0 002.25 6v12a1.5 1.5 0 001.5 1.5zm10.5-11.25h.008v.008h-.008V8.25zm.375 0a.375.375 0 11-.75 0 .375.375 0 01.75 0z" strokeLinecap="round" strokeLinejoin="round" />
                        </svg>
                    </div>
                    <h3 className="mt-4 text-lg font-semibold">No games found</h3>
                    <p className="mt-2 max-w-sm text-sm text-muted-foreground">
                        Try adjusting your filters or search query to find what you&apos;re looking for.
                    </p>
                </div>
            ) : (
                mounted && (
                    <div className="mt-2">
                        {/* Grid View */}
                        {viewMode === "grid" && (
                            <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 md:gap-6 lg:grid-cols-4 xl:grid-cols-5">
                                {games.map((game) => (
                                    <GameCard key={game.id} game={game} />
                                ))}
                            </div>
                        )}

                        {/* Compact View */}
                        {viewMode === "compact" && (
                            <div className="grid grid-cols-3 gap-2 sm:grid-cols-4 md:grid-cols-6 lg:grid-cols-8 xl:grid-cols-10">
                                {games.map((game) => (
                                    <GameCardCompact key={game.id} game={game} />
                                ))}
                            </div>
                        )}

                        {/* List View */}
                        {viewMode === "list" && (
                            <div className="flex flex-col gap-1 border-y border-border py-2">
                                {/* Optional Header Row */}
                                <div className="hidden lg:flex items-center gap-4 px-2 py-2 text-xs font-semibold uppercase tracking-wider text-muted-foreground border-b border-border/50 mb-2">
                                    <div className="w-9 shrink-0">Art</div>
                                    <div className="flex-1">Title & Details</div>
                                    <div className="flex-1 min-w-[120px]">Platforms</div>
                                    <div className="w-[100px] shrink-0">Status</div>
                                    <div className="w-[120px] shrink-0 text-right">Rating</div>
                                    <div className="w-[100px] shrink-0 text-right">Added</div>
                                </div>
                                
                                {games.map((game) => (
                                    <GameListRow key={game.id} game={game} />
                                ))}
                            </div>
                        )}
                    </div>
                )
            )}
        </div>
    );
}
