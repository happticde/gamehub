import Image from "next/image";
import Link from "next/link";
import { UnifiedGame } from "@/types/game";
import { getPlatformColor } from "@/lib/platform-colors";
import { Star } from "lucide-react";

interface GameCardProps {
    game: UnifiedGame;
}

export default function GameCard({ game }: GameCardProps) {
    // Determine the unique platforms this game is available on
    const platforms = game.platforms
        ? Array.from(new Set(game.platforms.map(p => p.platform)))
        : [];
    const physicalPlatforms = Array.from(new Set(game.physicalCopies?.map(copy => copy.platform) ?? []));

    // Extract the first developer from the comma-separated string (if available)
    const developer = game.developers?.split(",")[0]?.trim();

    // Extract release year
    const releaseYear = game.firstReleaseDate
        ? new Date(game.firstReleaseDate).getFullYear()
        : null;

    return (
        <Link href={`/library/${game.id}`} className="block h-full">
            <div className="group relative overflow-hidden rounded-xl border border-border bg-card text-card-foreground shadow-sm transition-all hover:shadow-md hover:-translate-y-1 h-full flex flex-col">
                {/* Cover Image Area */}
                <div className="relative aspect-[3/4] w-full overflow-hidden bg-muted shrink-0">
                    {game.coverUrl ? (
                        <Image
                            src={game.coverUrl}
                            alt={`${game.title} cover art`}
                            fill
                            className="object-cover transition-transform duration-500 group-hover:scale-105"
                            sizes="(max-width: 768px) 100vw, (max-width: 1200px) 50vw, 33vw"
                            unoptimized // Using unoptimized for external IGDB URLs to avoid Next.js image optimization limits initially
                        />
                    ) : (
                        <div className="flex h-full w-full flex-col items-center justify-center p-6 text-center text-muted-foreground">
                            <svg
                                className="mb-2 h-10 w-10 opacity-50"
                                fill="none"
                                height="24"
                                stroke="currentColor"
                                strokeLinecap="round"
                                strokeLinejoin="round"
                                strokeWidth="2"
                                viewBox="0 0 24 24"
                                width="24"
                                xmlns="http://www.w3.org/2000/svg"
                            >
                                <rect height="18" rx="2" ry="2" width="18" x="3" y="3" />
                                <circle cx="8.5" cy="8.5" r="1.5" />
                                <polyline points="21 15 16 10 5 21" />
                            </svg>
                            <span className="text-sm font-medium">No Cover Art</span>
                            {!game.enrichedAt && (
                                <span className="mt-1 text-xs opacity-70">Awaiting Sync</span>
                            )}
                        </div>
                    )}

                    {/* Platform Badges Overlay */}
                    {platforms.length > 0 && (
                        <div className="absolute right-2 top-2 z-10 flex flex-col items-end gap-1">
                            {platforms.map(platform => (
                                <span
                                    key={platform}
                                    className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-semibold shadow-sm ${getPlatformColor(
                                        platform
                                    )}`}
                                >
                                    {platform}
                                </span>
                            ))}
                        </div>
                    )}

                    {/* Optional Critic Rating Badge */}
                    {game.rating && (
                        <div className="absolute left-2 top-2 z-10 flex h-7 items-center justify-center rounded-full bg-background/90 px-2 text-xs font-bold text-foreground shadow-sm backdrop-blur">
                            <span className={game.rating >= 75 ? "text-green-500" : game.rating >= 50 ? "text-yellow-500" : "text-red-500"}>
                                {Math.round(game.rating)}
                            </span>
                        </div>
                    )}

                    {/* Play Status Badge */}
                    {game.playStatus && game.playStatus !== "Unplayed" && (
                        <div className="absolute bottom-2 left-2 z-10 flex h-6 items-center justify-center rounded-sm bg-primary/90 px-2 text-xs font-bold text-primary-foreground shadow-sm backdrop-blur uppercase tracking-wider">
                            {game.playStatus}
                        </div>
                    )}
                </div>

                {/* Info Area */}
                <div className="flex-1 p-4">
                    <h3 className="line-clamp-1 font-semibold leading-none tracking-tight" title={game.title}>
                        {game.title}
                    </h3>

                    {/* Developer + Year */}
                    {(developer || releaseYear) && (
                        <p className="mt-1.5 text-xs text-muted-foreground/80 line-clamp-1">
                            {developer}{developer && releaseYear && " · "}{releaseYear}
                        </p>
                    )}

                    {physicalPlatforms.length > 0 && (
                        <div className="mt-2 flex flex-wrap items-center gap-1 border-t border-dashed border-primary/25 pt-2">
                            <span className="mr-1 text-[10px] font-medium uppercase tracking-wide text-primary">Physical</span>
                            {physicalPlatforms.map((platform) => (
                                <span key={platform} className="rounded-full border border-primary/30 bg-primary/10 px-2 py-0.5 text-[10px] font-semibold text-primary">
                                    {platform}
                                </span>
                            ))}
                        </div>
                    )}

                    {/* Genres & Personal Rating */}
                    <div className="mt-2 flex items-center justify-between">
                        <div className="text-xs text-muted-foreground flex-1 pr-2">
                            {game.genres ? (
                                <span className="line-clamp-1">{game.genres.split(",").slice(0, 2).join(" • ")}</span>
                            ) : (
                                <span>Unknown Genre</span>
                            )}
                        </div>

                        {/* Personal 5-Star Rating */}
                        {game.userRating !== null && game.userRating !== undefined && (
                            <div className="flex items-center gap-0.5 shrink-0">
                                {[1, 2, 3, 4, 5].map(star => (
                                    <Star 
                                        key={star} 
                                        className={`w-3 h-3 ${game.userRating! >= star ? "fill-yellow-400 text-yellow-400" : "fill-none text-muted-foreground/30"}`} 
                                    />
                                ))}
                            </div>
                        )}
                    </div>
                </div>
            </div>
        </Link>
    );
}
