import Image from "next/image";
import Link from "next/link";
import { ArrowLeft, Calendar, Building2, Star, Tag, Layers } from "lucide-react";
import { Platform, PlayStatus } from "@/types/game";
import { getPlatformColor } from "@/lib/platform-colors";
import PersonalTrackingClient from "@/components/PersonalTrackingClient";
import FixMatchDialog from "@/components/library/FixMatchDialog";
import ScreenshotCarousel from "@/components/library/ScreenshotCarousel";
import CoverImageClient from "@/components/library/CoverImageClient";

// Extended payload type for the game detail view
interface ExpandedGame {
    id: number;
    title: string;
    slug: string;
    coverUrl: string | null;
    summary: string | null;
    genres: string[];
    platforms: string[];
    artworks: string[];
    screenshots: string[];
    developers: string[];
    publishers: string[];
    tags: string[];
    firstReleaseDate: string | null;
    rating: number | null;
    playStatus: PlayStatus;
    userRating: number | null;
    isIgnored: boolean;
    metadataSource: string | null;
    enrichedAt: string | null;
    items: {
        platform: Platform;
        externalId: string;
        lastSynced: string;
    }[];
}

export default async function GameDetailPage({
    params,
}: {
    params: Promise<{ id: string }>;
}) {
    // Next.js 15: params is a promise
    const { id } = await params;

    let game: ExpandedGame | null = null;
    let error: string | null = null;

    try {
        // Fetch from our local proxy. We use localhost:3000 to hit the Next.js Node server,
        // which then rewrites it to the internal Docker C# application.
        const res = await fetch(`http://localhost:3000/api/core/library/${id}`, { cache: "no-store" });
        if (!res.ok) {
            if (res.status === 404) {
                return (
                    <div className="flex min-h-[50vh] flex-col items-center justify-center p-8 text-center">
                        <h1 className="text-3xl font-bold">Game Not Found</h1>
                        <p className="mt-2 text-muted-foreground">We couldn&apos;t find a game with ID {id}.</p>
                        <Link href="/library" className="mt-6 inline-flex items-center gap-2 rounded-md bg-primary px-4 py-2 text-sm font-medium text-primary-foreground hover:bg-primary/90">
                            <ArrowLeft className="h-4 w-4" /> Back to Library
                        </Link>
                    </div>
                );
            }
            throw new Error(`Failed to fetch game details: ${res.statusText}`);
        }
        game = await res.json();
    } catch (e: any) {
        error = e.message || "An unexpected error occurred.";
        console.error("Fetch Game Detail Error:", error);
    }

    if (error || !game) {
        return (
            <div className="container mx-auto p-8">
                <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-6 text-center text-destructive">
                    <h3 className="font-semibold">Unable to load game details.</h3>
                    <p className="mt-2 text-sm">{error}</p>
                    <Link href="/library" className="mt-6 inline-flex items-center gap-2 border border-destructive/30 rounded-md px-4 py-2 text-sm hover:bg-destructive/20">
                        <ArrowLeft className="h-4 w-4" /> Return to Library
                    </Link>
                </div>
            </div>
        );
    }

    // Prepare Hero Image (First artwork, fallback to cover, fallback to gradient)
    const heroImage = game.artworks?.length > 0 ? game.artworks[0] : null;

    return (
        <div className="min-h-screen bg-background pb-16 animate-in fade-in duration-700">
            {/* Cinematic Hero Banner Area */}
            <div className="relative h-[40vh] md:h-[50vh] min-h-[280px] md:min-h-[400px] w-full bg-zinc-950 overflow-hidden">
                {heroImage ? (
                    <>
                        <Image
                            src={heroImage}
                            alt={`${game.title} backdrop`}
                            fill
                            className="object-cover opacity-60 mix-blend-screen"
                            priority
                            unoptimized
                        />
                        {/* Smooth gradient fade to background color */}
                        <div className="absolute inset-0 bg-gradient-to-t from-background via-background/60 to-transparent" />
                        <div className="absolute inset-x-0 bottom-0 h-32 bg-gradient-to-t from-background to-transparent" />
                    </>
                ) : (
                    <div className="absolute inset-0 bg-gradient-to-br from-indigo-950 via-background to-zinc-950" />
                )}

                {/* Hero Content Overlay */}
                <div className="absolute bottom-0 left-0 w-full">
                    <div className="container mx-auto flex items-end gap-6 px-4 pb-8 md:px-8">

                        {/* Floating Cover Art */}
                        <CoverImageClient coverUrl={game.coverUrl} title={game.title} />

                        {/* Title and Key Metrics */}
                        <div className="flex-1 space-y-3 drop-shadow-md">
                            <div className="flex flex-wrap items-center gap-2">
                                <Link
                                    href="/library"
                                    className="mb-2 flex items-center text-sm font-medium text-white/70 transition-colors hover:text-white"
                                >
                                    <ArrowLeft className="mr-1 h-4 w-4" /> Back to Library
                                </Link>
                            </div>

                            <h1 className="text-3xl font-extrabold tracking-tight text-white sm:text-4xl md:text-5xl lg:text-7xl">
                                {game.title}
                            </h1>

                            <div className="flex flex-wrap items-center gap-4 text-sm font-medium text-white/90">
                                {game.developers?.length > 0 && (
                                    <span className="flex items-center gap-1.5 backdrop-blur-sm bg-black/20 rounded-full px-3 py-1">
                                        <Building2 className="h-4 w-4" />
                                        {game.developers[0]}
                                    </span>
                                )}

                                {game.firstReleaseDate && (
                                    <span className="flex items-center gap-1.5 backdrop-blur-sm bg-black/20 rounded-full px-3 py-1">
                                        <Calendar className="h-4 w-4" />
                                        {new Date(game.firstReleaseDate).getFullYear()}
                                    </span>
                                )}

                                {game.rating !== null && (
                                    <span className="flex items-center gap-1.5 backdrop-blur-sm bg-black/20 rounded-full px-3 py-1">
                                        <Star className={`h-4 w-4 ${game.rating >= 75 ? "text-green-400 fill-green-400" : "text-yellow-400 fill-yellow-400"}`} />
                                        {Math.round(game.rating)} / 100
                                    </span>
                                )}
                            </div>
                        </div>
                    </div>
                </div>
            </div>

            {/* Main Content Area */}
            <div className="container mx-auto px-4 pt-16 md:px-8 md:pt-24 flex flex-col gap-12 lg:flex-row lg:gap-16">

                {/* Left Column (Main Information) */}
                <div className="flex-1 space-y-10">

                    {/* Summary */}
                    <section>
                        <h2 className="text-2xl font-bold tracking-tight mb-4">About</h2>
                        {game.summary ? (
                            <p className="text-lg leading-relaxed text-muted-foreground whitespace-pre-wrap">
                                {game.summary}
                            </p>
                        ) : (
                            <p className="text-muted-foreground italic">No description available from metadata provider.</p>
                        )}
                    </section>

                    {/* Screenshots Gallery */}
                    <ScreenshotCarousel screenshots={game.screenshots} />
                </div>

                {/* Right Column (Sidebar Details) */}
                <div className="w-full lg:w-80 shrink-0 space-y-8">

                    {/* Personal Tracking Client */}
                    <PersonalTrackingClient 
                        gameId={game.id} 
                        initialStatus={game.playStatus} 
                        initialRating={game.userRating} 
                        initialIsIgnored={game.isIgnored}
                    />

                    {/* Your Library Status */}
                    <div className="rounded-xl border border-border bg-card p-6 shadow-sm">
                        <div className="flex items-center justify-between border-b border-border pb-3 mb-4">
                            <h3 className="flex items-center gap-2 font-semibold text-lg">
                                <Layers className="h-5 w-5 text-primary" />
                                Library
                            </h3>
                            <FixMatchDialog gameId={game.id} initialTitle={game.title} />
                        </div>
                        {game.items.length > 0 ? (
                            <ul className="space-y-3">
                                {game.items.map((item, i) => (
                                    <li key={i} className="flex flex-col gap-1">
                                        <div className="flex items-center justify-between">
                                            <span className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-semibold shadow-sm ${getPlatformColor(item.platform.toString())}`}>
                                                {item.platform}
                                            </span>
                                        </div>
                                        <span className="text-xs text-muted-foreground ml-1">
                                            ID: {item.externalId}
                                        </span>
                                    </li>
                                ))}
                            </ul>
                        ) : (
                            <p className="text-sm text-muted-foreground">Not owned on any linked platform.</p>
                        )}
                        <div className="mt-6 text-xs text-muted-foreground/60 border-t border-border pt-4">
                            Added: {game.enrichedAt ? new Date(game.enrichedAt).toLocaleDateString() : 'Unknown'}
                        </div>
                    </div>

                    {/* Genres & Tags */}
                    {game.genres?.length > 0 && (
                        <div>
                            <h3 className="flex items-center gap-2 font-semibold mb-3 text-muted-foreground">
                                <Tag className="h-4 w-4" /> Genres
                            </h3>
                            <div className="flex flex-wrap gap-2">
                                {game.genres.map(g => (
                                    <span key={g} className="px-3 py-1 bg-muted text-muted-foreground rounded-md text-sm font-medium border border-border/50">
                                        {g}
                                    </span>
                                ))}
                            </div>
                        </div>
                    )}

                    {/* Original Platforms */}
                    {game.platforms?.length > 0 && (
                        <div>
                            <h3 className="font-semibold mb-3 text-muted-foreground text-sm uppercase tracking-wider">Original Release Platforms</h3>
                            <div className="flex flex-wrap gap-2">
                                {game.platforms.map(p => (
                                    <span key={p} className="text-sm text-foreground bg-secondary px-2 py-1 rounded">
                                        {p}
                                    </span>
                                ))}
                            </div>
                        </div>
                    )}
                </div>
            </div>
        </div>
    );
}
