import Image from "next/image";
import { Platform, UnifiedGame } from "@/types/game";

interface GameCardProps {
    game: UnifiedGame;
}

// Helper to map platform enum string to a distinct brand color using Tailwind classes
function getPlatformColor(platform: Platform): string {
    switch (platform) {
        case "Steam":
            return "bg-blue-600 text-white";
        case "Psn":
            return "bg-indigo-600 text-white";
        case "Gog":
            return "bg-purple-600 text-white";
        case "Epic":
            return "bg-zinc-800 text-white dark:bg-zinc-300 dark:text-black";
        default:
            return "bg-zinc-500 text-white";
    }
}

export default function GameCard({ game }: GameCardProps) {
    // Determine if the game has at least one library item to derive the primary platform badge
    const primaryPlatform = game.platforms && game.platforms.length > 0 ? game.platforms[0].platform : null;

    return (
        <div className="group relative overflow-hidden rounded-xl border border-border bg-card text-card-foreground shadow-sm transition-all hover:shadow-md hover:-translate-y-1">
            {/* Cover Image Area */}
            <div className="relative aspect-[3/4] w-full overflow-hidden bg-muted">
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

                {/* Platform Badge Overlay */}
                {primaryPlatform && (
                    <div className="absolute right-2 top-2 z-10">
                        <span
                            className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-semibold shadow-sm ${getPlatformColor(
                                primaryPlatform
                            )}`}
                        >
                            {primaryPlatform}
                        </span>
                    </div>
                )}

                {/* Optional Rating Badge */}
                {game.rating && (
                    <div className="absolute left-2 top-2 z-10 flex h-7 items-center justify-center rounded-full bg-background/90 px-2 text-xs font-bold text-foreground shadow-sm backdrop-blur">
                        <span className={game.rating >= 75 ? "text-green-500" : game.rating >= 50 ? "text-yellow-500" : "text-red-500"}>
                            {Math.round(game.rating)}
                        </span>
                    </div>
                )}
            </div>

            {/* Info Area */}
            <div className="p-4">
                <h3 className="line-clamp-1 font-semibold leading-none tracking-tight" title={game.title}>
                    {game.title}
                </h3>

                {/* Tags / Genres */}
                <div className="mt-2 text-xs text-muted-foreground">
                    {game.genres ? (
                        <span className="line-clamp-1">{game.genres.split(",").slice(0, 3).join(" • ")}</span>
                    ) : (
                        <span>Unknown Genre</span>
                    )}
                </div>
            </div>
        </div>
    );
}
