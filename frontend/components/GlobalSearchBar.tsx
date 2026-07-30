"use client";

import { useEffect, useRef, useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import Image from "next/image";
import Link from "next/link";
import { Search, Loader2, X, CornerDownLeft, Sparkles } from "lucide-react";
import { UnifiedGame, PagedResult } from "@/types/game";
import { getPlatformColor } from "@/lib/platform-colors";
import { cn } from "@/lib/utils";
import { fetchAuthenticated } from "@/lib/authenticated-fetch";
import {
    Dialog,
    DialogContent,
    DialogTrigger,
} from "@/components/ui/dialog";

export default function GlobalSearchBar() {
    const router = useRouter();
    const [query, setQuery] = useState("");
    const [results, setResults] = useState<UnifiedGame[]>([]);
    const [totalResults, setTotalResults] = useState(0);
    const [loading, setLoading] = useState(false);
    const [isOpen, setIsOpen] = useState(false);
    const [isMobileOpen, setIsMobileOpen] = useState(false);
    const [selectedIndex, setSelectedIndex] = useState(-1);
    const [, startTransition] = useTransition();

    const containerRef = useRef<HTMLDivElement>(null);
    const inputRef = useRef<HTMLInputElement>(null);

    // Debounced search fetching
    useEffect(() => {
        if (!query.trim()) {
            setResults([]);
            setTotalResults(0);
            setLoading(false);
            return;
        }

        setLoading(true);
        const timer = setTimeout(async () => {
            try {
                const res = await fetchAuthenticated(`/api/core/library?search=${encodeURIComponent(query)}&pageSize=5`);
                if (res.ok) {
                    const data: PagedResult<UnifiedGame> = await res.json();
                    setResults(data.items || []);
                    setTotalResults(data.totalCount || 0);
                }
            } catch (err) {
                console.error("Error fetching global search results:", err);
            } finally {
                setLoading(false);
            }
        }, 300);

        return () => clearTimeout(timer);
    }, [query]);

    // Handle clicks outside the dropdown to close it
    useEffect(() => {
        const handleClickOutside = (event: MouseEvent) => {
            if (containerRef.current && !containerRef.current.contains(event.target as Node)) {
                setIsOpen(false);
            }
        };
        document.addEventListener("mousedown", handleClickOutside);
        return () => document.removeEventListener("mousedown", handleClickOutside);
    }, []);

    // Global keyboard listener for focusing search bar (Cmd+K / Ctrl+K / slash)
    useEffect(() => {
        const handleKeyDown = (event: KeyboardEvent) => {
            const isEditing = 
                document.activeElement?.tagName === "INPUT" ||
                document.activeElement?.tagName === "TEXTAREA" ||
                (document.activeElement as HTMLElement)?.isContentEditable;

            if (isEditing) return;

            // Cmd+K, Ctrl+K, or Slash (/)
            if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "k") {
                event.preventDefault();
                inputRef.current?.focus();
                setIsOpen(true);
            } else if (event.key === "/") {
                event.preventDefault();
                inputRef.current?.focus();
                setIsOpen(true);
            }
        };

        window.addEventListener("keydown", handleKeyDown);
        return () => window.removeEventListener("keydown", handleKeyDown);
    }, []);

    // Reset selected index when results change
    useEffect(() => {
        setSelectedIndex(-1);
    }, [results]);

    const handleSearchSubmit = (searchTerm: string) => {
        if (!searchTerm.trim()) return;
        setIsOpen(false);
        setIsMobileOpen(false);
        inputRef.current?.blur();
        startTransition(() => {
            router.push(`/library?search=${encodeURIComponent(searchTerm.trim())}`);
        });
    };

    const handleKeyDown = (event: React.KeyboardEvent) => {
        if (event.key === "Escape") {
            setIsOpen(false);
            inputRef.current?.blur();
        } else if (event.key === "ArrowDown") {
            event.preventDefault();
            if (!isOpen) {
                setIsOpen(true);
            } else {
                setSelectedIndex((prev) => Math.min(prev + 1, results.length));
            }
        } else if (event.key === "ArrowUp") {
            event.preventDefault();
            setSelectedIndex((prev) => Math.max(prev - 1, -1));
        } else if (event.key === "Enter") {
            event.preventDefault();
            // If the "View all results" option at the bottom is selected
            if (selectedIndex === results.length) {
                handleSearchSubmit(query);
            } else if (selectedIndex >= 0 && results[selectedIndex]) {
                const selectedGame = results[selectedIndex];
                setIsOpen(false);
                router.push(`/library/${selectedGame.id}`);
            } else {
                handleSearchSubmit(query);
            }
        }
    };

    // Shared list renderer for search results
    const renderResultsList = (closeCallback: () => void, activeIndex: number) => {
        if (!query.trim()) {
            return (
                <div className="flex flex-col items-center justify-center py-10 px-4 text-center text-muted-foreground">
                    <Sparkles className="h-8 w-8 text-primary/40 mb-2 animate-pulse" />
                    <p className="text-sm font-medium">Search for games across your collection</p>
                    <p className="text-xs text-muted-foreground/60 mt-1">Start typing to search Steam, GOG, PSN, and Epic libraries.</p>
                </div>
            );
        }

        if (loading && results.length === 0) {
            return (
                <div className="flex flex-col gap-2 p-2">
                    {[1, 2, 3].map((n) => (
                        <div key={n} className="flex items-center gap-3 p-2 rounded-lg animate-pulse bg-muted/40">
                            <div className="h-12 w-9 rounded bg-muted shrink-0" />
                            <div className="flex-1 space-y-2">
                                <div className="h-4 bg-muted rounded w-2/3" />
                                <div className="h-3 bg-muted rounded w-1/3" />
                            </div>
                        </div>
                    ))}
                </div>
            );
        }

        if (results.length === 0) {
            return (
                <div className="py-8 text-center text-muted-foreground">
                    <p className="text-sm">No games found for &ldquo;{query}&rdquo;</p>
                </div>
            );
        }

        return (
            <div className="flex flex-col gap-1 p-1">
                {results.map((game, index) => {
                    const uniquePlatforms = game.platforms
                        ? Array.from(new Set(game.platforms.map(p => p.platform)))
                        : [];
                    const developer = game.developers?.split(",")[0]?.trim();
                    const releaseYear = game.firstReleaseDate ? new Date(game.firstReleaseDate).getFullYear() : null;

                    return (
                        <Link
                            key={game.id}
                            href={`/library/${game.id}`}
                            onClick={() => {
                                closeCallback();
                                setQuery("");
                            }}
                            className={cn(
                                "flex items-center gap-3 p-2 rounded-lg transition-all",
                                index === activeIndex 
                                    ? "bg-accent/80 text-foreground" 
                                    : "hover:bg-accent/40 text-muted-foreground hover:text-foreground"
                            )}
                        >
                            {/* Cover art thumbnail */}
                            <div className="relative h-12 w-9 rounded bg-muted overflow-hidden shrink-0 shadow-sm border border-border/20">
                                {game.coverUrl ? (
                                    <Image
                                        src={game.coverUrl}
                                        alt={game.title}
                                        fill
                                        sizes="36px"
                                        className="object-cover"
                                        unoptimized
                                    />
                                ) : (
                                    <div className="flex h-full w-full items-center justify-center text-[10px] text-muted-foreground font-semibold">
                                        Cover
                                    </div>
                                )}
                            </div>

                            {/* Game Info */}
                            <div className="flex-1 min-w-0">
                                <div className="flex items-center justify-between gap-2">
                                    <span className="font-semibold text-sm line-clamp-1 text-foreground">
                                        {game.title}
                                    </span>
                                    {game.userRating && (
                                        <span className="text-xs font-medium text-yellow-500 shrink-0">
                                            ★ {game.userRating}
                                        </span>
                                    )}
                                </div>
                                <div className="flex items-center gap-1.5 mt-1 text-xs line-clamp-1">
                                    {developer && <span className="opacity-90">{developer}</span>}
                                    {releaseYear && <span className="text-muted-foreground/60">({releaseYear})</span>}
                                    <span className="text-muted-foreground/40">•</span>
                                    <div className="flex gap-1">
                                        {uniquePlatforms.map((platform) => (
                                            <span
                                                key={platform}
                                                className={cn(
                                                    "inline-flex items-center rounded-sm px-1 py-0.2 text-[9px] font-medium tracking-tight",
                                                    getPlatformColor(platform)
                                                )}
                                            >
                                                {platform}
                                            </span>
                                        ))}
                                    </div>
                                </div>
                            </div>
                        </Link>
                    );
                })}

                {/* View all results option */}
                <button
                    onClick={() => handleSearchSubmit(query)}
                    className={cn(
                        "w-full text-left flex items-center justify-between p-2.5 mt-1 border-t border-border/40 text-xs font-medium transition-colors rounded-md",
                        selectedIndex === results.length
                            ? "bg-primary/10 text-primary"
                            : "text-muted-foreground hover:text-foreground hover:bg-accent/40"
                    )}
                >
                    <span>View all {totalResults} results for &ldquo;{query}&rdquo;</span>
                    <span className="flex items-center gap-1 opacity-60">
                        Enter <CornerDownLeft className="h-3 w-3" />
                    </span>
                </button>
            </div>
        );
    };

    return (
        <div className="flex items-center justify-center flex-1 max-w-md mx-auto md:px-4">
            {/* Desktop View Search Bar */}
            <div ref={containerRef} data-retro-search className="hidden md:block w-full relative">
                <div 
                    className={cn(
                        "flex items-center rounded-full bg-muted/40 hover:bg-muted/60 border border-border/40 px-3 h-10 transition-all",
                        isOpen && "bg-background border-primary/50 shadow-md ring-2 ring-primary/10"
                    )}
                >
                    <Search className="h-4 w-4 text-muted-foreground shrink-0 mr-2" />
                    <input
                        ref={inputRef}
                        type="text"
                        placeholder="Search games..."
                        className="w-full text-sm bg-transparent border-0 outline-none placeholder:text-muted-foreground focus:outline-none focus:ring-0 h-full py-2"
                        value={query}
                        onChange={(e) => {
                            setQuery(e.target.value);
                            setIsOpen(true);
                        }}
                        onFocus={() => setIsOpen(true)}
                        onKeyDown={handleKeyDown}
                    />

                    {loading && (
                        <Loader2 className="h-4 w-4 text-muted-foreground shrink-0 animate-spin mr-1.5" />
                    )}

                    {query ? (
                        <button
                            onClick={() => {
                                setQuery("");
                                inputRef.current?.focus();
                            }}
                            className="p-1 rounded-full text-muted-foreground hover:text-foreground hover:bg-muted/80 transition-colors mr-1 shrink-0"
                        >
                            <X className="h-3 w-3" />
                        </button>
                    ) : (
                        <kbd className="hidden sm:inline-flex select-none items-center gap-0.5 rounded border border-border bg-background px-1.5 ml-2 font-mono text-[10px] font-medium text-muted-foreground/80 shadow-sm shrink-0">
                            <span className="text-[9px]">⌘</span>K
                        </kbd>
                    )}
                </div>

                {/* Dropdown Results Overlay */}
                {isOpen && (query.trim() || loading) && (
                    <div data-retro-search-results className="absolute top-full left-0 right-0 mt-2 bg-popover/98 backdrop-blur supports-[backdrop-filter]:bg-popover/90 border border-border rounded-xl shadow-2xl p-1 z-50 animate-in fade-in-0 slide-in-from-top-2 duration-150 max-h-[420px] overflow-y-auto">
                        {renderResultsList(() => setIsOpen(false), selectedIndex)}
                    </div>
                )}
            </div>

            {/* Mobile View Search Button & Dialog */}
            <div className="md:hidden flex items-center">
                <Dialog open={isMobileOpen} onOpenChange={setIsMobileOpen}>
                    <DialogTrigger asChild>
                        <button
                            type="button"
                            className="flex h-9 w-9 items-center justify-center rounded-md text-muted-foreground hover:text-foreground hover:bg-accent transition-colors"
                            aria-label="Search games"
                        >
                            <Search className="h-4 w-4" />
                        </button>
                    </DialogTrigger>
                    <DialogContent data-retro-search data-retro-search-results showCloseButton={false} className="max-h-[85vh] flex flex-col max-w-[calc(100%-2rem)] p-0 overflow-hidden bg-background/98 backdrop-blur-md border border-border rounded-xl">
                        <div className="flex items-center border-b border-border px-4 py-3 gap-3">
                            <Search className="h-4 w-4 text-muted-foreground shrink-0" />
                            <input
                                type="text"
                                placeholder="Search games..."
                                className="w-full text-sm bg-transparent border-0 outline-none placeholder:text-muted-foreground focus:outline-none"
                                value={query}
                                onChange={(e) => setQuery(e.target.value)}
                                onKeyDown={(e) => {
                                    if (e.key === "Enter") {
                                        e.preventDefault();
                                        handleSearchSubmit(query);
                                    }
                                }}
                                autoFocus
                            />
                            {loading && (
                                <Loader2 className="h-4 w-4 text-muted-foreground animate-spin shrink-0" />
                            )}
                            {query && (
                                <button
                                    onClick={() => setQuery("")}
                                    className="p-1 rounded-full text-muted-foreground hover:text-foreground hover:bg-accent transition-colors shrink-0"
                                >
                                    <X className="h-3.5 w-3.5" />
                                </button>
                            )}
                            <button
                                type="button"
                                onClick={() => setIsMobileOpen(false)}
                                className="text-xs font-bold uppercase tracking-wider text-muted-foreground hover:text-foreground transition-colors shrink-0 pl-2 border-l border-border/40"
                            >
                                ESC
                            </button>
                        </div>
                        <div className="flex-1 overflow-y-auto p-2 min-h-[250px] max-h-[60vh]">
                            {renderResultsList(() => setIsMobileOpen(false), -1)}
                        </div>
                    </DialogContent>
                </Dialog>
            </div>
        </div>
    );
}
