"use client";

/**
 * /stats — Statistics & Analytics Dashboard
 *
 * This page fetches comprehensive library stats from the backend and displays
 * them as visual charts and cards. All charts are pure CSS — no charting library needed.
 *
 * Key React concepts used here:
 *   - useState / useEffect for data fetching and loading states
 *   - Conditional rendering (&& and ternary) for loading/empty states
 *   - Array .map() to render dynamic lists of chart bars and cards
 *   - CSS transitions via Tailwind for animated bar widths
 */

import { useState, useEffect } from "react";
import Image from "next/image";
import Link from "next/link";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import {
    Gamepad2,
    Layers,
    Star,
    BarChart3,
    Loader2,
    TrendingUp,
    PlayCircle,
} from "lucide-react";
import type { LibraryStats } from "@/types/game";
import { fetchAuthenticated } from "@/lib/authenticated-fetch";
import { getErrorMessage } from "@/lib/errors";

/** Maps a platform name to a distinct hex color for chart bars */
function getPlatformHex(platform: string): string {
    switch (platform.toLowerCase()) {
        case "steam": return "#2563eb";       // blue-600
        case "gog": return "#9333ea";         // purple-600
        case "psn": return "#4f46e5";         // indigo-600
        case "epic": return "#71717a";        // zinc-500
        default: return "#71717a";
    }
}

export default function StatsPage() {
    const [stats, setStats] = useState<LibraryStats | null>(null);
    const [isLoading, setIsLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);

    useEffect(() => {
        async function fetchStats() {
            try {
                const res = await fetchAuthenticated("/api/core/library/stats");
                if (!res.ok) {
                    setError(`Failed to load stats (Status ${res.status})`);
                    return;
                }
                const libraryStats: LibraryStats = await res.json();
                setStats(libraryStats);
            } catch (caughtError: unknown) {
                setError(getErrorMessage(caughtError, "Failed to fetch stats"));
            } finally {
                setIsLoading(false);
            }
        }
        fetchStats();
    }, []);

    // Loading state
    if (isLoading) {
        return (
            <div className="min-h-screen bg-background flex items-center justify-center">
                <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
            </div>
        );
    }

    // Error state
    if (error || !stats) {
        return (
            <div className="min-h-screen bg-background">
                <main className="container mx-auto px-4 md:px-8 py-12 max-w-screen-xl">
                    <div className="rounded-xl border border-destructive/50 bg-destructive/10 p-6 text-destructive">
                        <h3 className="font-semibold mb-2">Failed to load statistics</h3>
                        <p className="text-sm font-mono">{error}</p>
                        <p className="text-sm mt-4 text-muted-foreground">
                            Make sure the Core API is running on port 5001.
                        </p>
                    </div>
                </main>
            </div>
        );
    }

    // Find the max values for scaling chart bars
    const maxPlatformCount = Math.max(...stats.platformCounts.map(p => p.count), 1);
    const maxGenreCount = Math.max(...stats.genreCounts.map(g => g.count), 1);
    const maxPlayStatusCount = Math.max(...(stats.playStatusCounts || []).map(p => p.count), 1);
    const maxRatingCount = Math.max(...stats.ratingDistribution.map(r => r.count), 1);

    return (
        <div className="min-h-screen bg-background">
            <main className="container mx-auto px-4 md:px-8 py-8 md:py-12 max-w-screen-xl animate-in fade-in slide-in-from-bottom-4 duration-500">
                {/* Page header */}
                <header className="mb-10 border-b pb-8">
                    <h1 className="text-2xl md:text-4xl font-extrabold tracking-tight lg:text-5xl flex items-center gap-3 md:gap-4">
                        <BarChart3 className="h-7 w-7 md:h-10 md:w-10 text-primary shrink-0" />
                        Library Statistics
                    </h1>
                    <p className="mt-4 text-lg text-muted-foreground max-w-2xl">
                        Visual breakdown of your unified game collection across platforms, genres, and ratings.
                    </p>
                </header>

                {/* ---- Hero Stats Cards ---- */}
                <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4 mb-10">
                    <HeroStatCard
                        title="Total Games"
                        value={stats.totalGames}
                        icon={<Gamepad2 className="h-5 w-5 text-indigo-400" />}
                    />
                    <HeroStatCard
                        title="Platform Links"
                        value={stats.totalPlatformLinks}
                        subtitle={`${stats.multiPlatformCount} multi-platform`}
                        icon={<Layers className="h-5 w-5 text-purple-400" />}
                    />
                    <HeroStatCard
                        title="Average Rating"
                        value={stats.averageRating}
                        subtitle="out of 100"
                        icon={<Star className="h-5 w-5 text-yellow-400" />}
                    />
                    <HeroStatCard
                        title="Enriched"
                        value={`${stats.enrichedCount}/${stats.totalGames}`}
                        subtitle={`${stats.totalGames > 0 ? Math.round((stats.enrichedCount / stats.totalGames) * 100) : 0}% complete`}
                        icon={<TrendingUp className="h-5 w-5 text-green-400" />}
                    />
                </div>

                {/* ---- Charts Grid ---- */}
                <div className="grid gap-8 lg:grid-cols-3 mb-10">
                    {/* Platform Distribution */}
                    <Card>
                        <CardHeader>
                            <CardTitle className="text-base">Platform Distribution</CardTitle>
                        </CardHeader>
                        <CardContent className="space-y-3">
                            {stats.platformCounts.length === 0 ? (
                                <p className="text-sm text-muted-foreground">No platform data yet.</p>
                            ) : (
                                stats.platformCounts.map((p) => (
                                    <div key={p.platform} className="space-y-1">
                                        <div className="flex items-center justify-between text-sm">
                                            <span className="font-medium">{p.platform}</span>
                                            <span className="text-muted-foreground tabular-nums">{p.count}</span>
                                        </div>
                                        <div className="h-3 w-full rounded-full bg-muted overflow-hidden">
                                            <div
                                                className="h-full rounded-full transition-all duration-700 ease-out"
                                                style={{
                                                    width: `${(p.count / maxPlatformCount) * 100}%`,
                                                    backgroundColor: getPlatformHex(p.platform),
                                                }}
                                            />
                                        </div>
                                    </div>
                                ))
                            )}
                        </CardContent>
                    </Card>

                    {/* Genre Breakdown */}
                    <Card>
                        <CardHeader>
                            <CardTitle className="text-base">Top Genres</CardTitle>
                        </CardHeader>
                        <CardContent className="space-y-2.5">
                            {stats.genreCounts.length === 0 ? (
                                <p className="text-sm text-muted-foreground">No genre data yet. Enrich your library first.</p>
                            ) : (
                                stats.genreCounts.map((g, i) => (
                                    <div key={g.name} className="space-y-0.5">
                                        <div className="flex items-center justify-between text-sm">
                                            <span className="font-medium truncate max-w-[200px]">{g.name}</span>
                                            <span className="text-muted-foreground tabular-nums">{g.count}</span>
                                        </div>
                                        <div className="h-2.5 w-full rounded-full bg-muted overflow-hidden">
                                            <div
                                                className="h-full rounded-full transition-all duration-700 ease-out"
                                                style={{
                                                    width: `${(g.count / maxGenreCount) * 100}%`,
                                                    // Gradient from indigo to purple based on index
                                                    backgroundColor: `oklch(0.65 0.18 ${270 + i * 8})`,
                                                }}
                                            />
                                        </div>
                                    </div>
                                ))
                            )}
                        </CardContent>
                    </Card>

                    {/* Play Status */}
                    <Card>
                        <CardHeader>
                            <CardTitle className="text-base flex items-center gap-2">
                                <PlayCircle className="h-4 w-4" />
                                Play Status
                            </CardTitle>
                        </CardHeader>
                        <CardContent className="space-y-3">
                            {!stats.playStatusCounts || stats.playStatusCounts.length === 0 ? (
                                <p className="text-sm text-muted-foreground">No play status data yet.</p>
                            ) : (
                                stats.playStatusCounts.map((p) => (
                                    <div key={p.status} className="space-y-1">
                                        <div className="flex items-center justify-between text-sm">
                                            <span className="font-medium">{p.status}</span>
                                            <span className="text-muted-foreground tabular-nums">{p.count}</span>
                                        </div>
                                        <div className="h-3 w-full rounded-full bg-muted overflow-hidden">
                                            <div
                                                className="h-full rounded-full transition-all duration-700 ease-out"
                                                style={{
                                                    width: `${(p.count / maxPlayStatusCount) * 100}%`,
                                                    // Distinct colors per status
                                                    backgroundColor: p.status === 'Completed' ? '#10b981' : 
                                                                   p.status === 'Playing' ? '#3b82f6' :
                                                                   p.status === 'Played' ? '#6366f1' :
                                                                   p.status === 'Abandoned' ? '#ef4444' : '#71717a',
                                                }}
                                            />
                                        </div>
                                    </div>
                                ))
                            )}
                        </CardContent>
                    </Card>
                </div>

                {/* ---- Rating Distribution ---- */}
                <Card className="mb-10">
                    <CardHeader>
                        <CardTitle className="text-base">Rating Distribution</CardTitle>
                    </CardHeader>
                    <CardContent>
                        {stats.ratingDistribution.length === 0 ? (
                            <p className="text-sm text-muted-foreground">No rated games yet.</p>
                        ) : (
                            <div className="flex items-end gap-1 sm:gap-2 h-32 sm:h-40 overflow-x-auto">
                                {stats.ratingDistribution.map((r) => (
                                    <div key={r.bucket} className="flex-1 flex flex-col items-center gap-1">
                                        <span className="text-xs text-muted-foreground tabular-nums">{r.count}</span>
                                        <div className="w-full flex items-end" style={{ height: "120px" }}>
                                            <div
                                                className="w-full rounded-t-md transition-all duration-700 ease-out"
                                                style={{
                                                    height: `${(r.count / maxRatingCount) * 100}%`,
                                                    minHeight: r.count > 0 ? "4px" : "0",
                                                    background: `linear-gradient(to top, oklch(0.55 0.20 270), oklch(0.70 0.15 310))`,
                                                }}
                                            />
                                        </div>
                                        <span className="text-[10px] text-muted-foreground whitespace-nowrap">{r.bucket}</span>
                                    </div>
                                ))}
                            </div>
                        )}
                    </CardContent>
                </Card>

                {/* ---- Top Rated & Recently Added ---- */}
                <div className="grid min-w-0 gap-8 lg:grid-cols-2">
                    {/* Top Rated Games */}
                    <Card className="min-w-0 overflow-hidden">
                        <CardHeader>
                            <CardTitle className="text-base">Top Rated Games</CardTitle>
                        </CardHeader>
                        <CardContent>
                            {stats.topRated.length === 0 ? (
                                <p className="text-sm text-muted-foreground">No rated games yet.</p>
                            ) : (
                                <div className="space-y-3">
                                    {stats.topRated.map((game, i) => (
                                        <Link
                                            key={game.id}
                                            href={`/library/${game.id}`}
                                            className="group flex min-w-0 items-center gap-3 rounded-lg p-2 -mx-2 transition-colors hover:bg-muted/50"
                                        >
                                            {/* Rank */}
                                            <span className="text-lg font-bold text-muted-foreground w-6 text-right tabular-nums">
                                                {i + 1}
                                            </span>

                                            {/* Cover */}
                                            {game.coverUrl ? (
                                                <Image
                                                    src={game.coverUrl}
                                                    alt={`${game.title} cover art`}
                                                    width={36}
                                                    height={48}
                                                    className="h-12 w-9 rounded object-cover flex-shrink-0"
                                                    unoptimized
                                                />
                                            ) : (
                                                <div className="h-12 w-9 rounded bg-muted flex-shrink-0 flex items-center justify-center">
                                                    <Gamepad2 className="h-4 w-4 text-muted-foreground" />
                                                </div>
                                            )}

                                            {/* Info */}
                                            <div className="flex-1 min-w-0">
                                                <p className="font-medium text-sm truncate group-hover:text-foreground">
                                                    {game.title}
                                                </p>
                                                <p className="text-xs text-muted-foreground truncate">
                                                    {game.genres || "Unknown genre"}
                                                </p>
                                            </div>

                                            {/* Rating badge */}
                                            <span className={`
                                                text-sm font-bold tabular-nums px-2 py-0.5 rounded-md
                                                ${game.rating >= 85
                                                    ? "bg-green-500/15 text-green-500"
                                                    : game.rating >= 70
                                                        ? "bg-yellow-500/15 text-yellow-500"
                                                        : "bg-red-500/15 text-red-500"
                                                }
                                            `}>
                                                {Math.round(game.rating)}
                                            </span>
                                        </Link>
                                    ))}
                                </div>
                            )}
                        </CardContent>
                    </Card>

                    {/* Recently Added */}
                    <Card className="min-w-0 overflow-hidden">
                        <CardHeader>
                            <CardTitle className="text-base">Recently Enriched</CardTitle>
                        </CardHeader>
                        <CardContent>
                            {stats.recentlyAdded.length === 0 ? (
                                <p className="text-sm text-muted-foreground">No enriched games yet.</p>
                            ) : (
                                <div className="space-y-3">
                                    {stats.recentlyAdded.map((game) => (
                                        <Link
                                            key={game.id}
                                            href={`/library/${game.id}`}
                                            className="group flex min-w-0 items-center gap-3 rounded-lg p-2 -mx-2 transition-colors hover:bg-muted/50"
                                        >
                                            {/* Cover */}
                                            {game.coverUrl ? (
                                                <Image
                                                    src={game.coverUrl}
                                                    alt={`${game.title} cover art`}
                                                    width={36}
                                                    height={48}
                                                    className="h-12 w-9 rounded object-cover flex-shrink-0"
                                                    unoptimized
                                                />
                                            ) : (
                                                <div className="h-12 w-9 rounded bg-muted flex-shrink-0 flex items-center justify-center">
                                                    <Gamepad2 className="h-4 w-4 text-muted-foreground" />
                                                </div>
                                            )}

                                            {/* Info */}
                                            <div className="flex-1 min-w-0">
                                                <p className="font-medium text-sm truncate group-hover:text-foreground">
                                                    {game.title}
                                                </p>
                                                <div className="flex items-center gap-1.5 mt-0.5">
                                                    {game.platforms.map((p) => (
                                                        <span
                                                            key={p}
                                                            className="inline-flex items-center rounded-full bg-secondary px-2 py-0.5 text-[10px] text-secondary-foreground"
                                                        >
                                                            {p}
                                                        </span>
                                                    ))}
                                                </div>
                                            </div>

                                            {/* Timestamp */}
                                            <span className="text-xs text-muted-foreground whitespace-nowrap">
                                                {new Date(game.enrichedAt).toLocaleDateString(undefined, {
                                                    month: "short",
                                                    day: "numeric",
                                                })}
                                            </span>
                                        </Link>
                                    ))}
                                </div>
                            )}
                        </CardContent>
                    </Card>
                </div>
            </main>
        </div>
    );
}

// =========================================================================
// Helper sub-components
// =========================================================================

/** Large number card for the hero stats row */
function HeroStatCard({
    title,
    value,
    subtitle,
    icon,
}: {
    title: string;
    value: number | string;
    subtitle?: string;
    icon: React.ReactNode;
}) {
    return (
        <Card className="relative overflow-hidden">
            <CardHeader className="flex flex-row items-center justify-between pb-2">
                <CardTitle className="text-sm font-medium text-muted-foreground">
                    {title}
                </CardTitle>
                {icon}
            </CardHeader>
            <CardContent>
                <div className="text-3xl font-bold tabular-nums">{value}</div>
                {subtitle && (
                    <p className="text-xs text-muted-foreground mt-1">{subtitle}</p>
                )}
            </CardContent>
        </Card>
    );
}
