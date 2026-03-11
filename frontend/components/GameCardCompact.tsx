import Image from "next/image";
import Link from "next/link";
import { UnifiedGame } from "@/types/game";
import { getPlatformColor } from "@/lib/platform-colors";

interface GameCardCompactProps {
    game: UnifiedGame;
}

export default function GameCardCompact({ game }: GameCardCompactProps) {
    const platforms = game.platforms
        ? Array.from(new Set(game.platforms.map(p => p.platform)))
        : [];

    return (
        <Link href={`/library/${game.id}`} className="block h-full group">
            <div className="relative overflow-hidden rounded-md border border-border bg-card shadow-sm transition-all hover:scale-105 hover:z-10 hover:shadow-lg focus:outline-none focus:ring-2 focus:ring-primary h-full aspect-[3/4]">
                {/* Cover Image */}
                {game.coverUrl ? (
                    <Image
                        src={game.coverUrl}
                        alt={`${game.title} cover`}
                        fill
                        className="object-cover"
                        sizes="(max-width: 768px) 33vw, (max-width: 1200px) 20vw, 15vw"
                        unoptimized
                    />
                ) : (
                    <div className="flex h-full w-full items-center justify-center bg-muted p-2 text-center">
                        <span className="text-xs font-medium text-muted-foreground line-clamp-3">
                            {game.title}
                        </span>
                    </div>
                )}

                {/* Optional Critic Rating Badge (Top Left) */}
                {game.rating && (
                    <div className="absolute left-1.5 top-1.5 z-10 flex h-6 w-6 items-center justify-center rounded-full bg-background/90 text-[10px] font-bold shadow-sm backdrop-blur">
                        <span className={game.rating >= 75 ? "text-green-500" : game.rating >= 50 ? "text-yellow-500" : "text-red-500"}>
                            {Math.round(game.rating)}
                        </span>
                    </div>
                )}

                {/* Play Status Badge (Bottom Right) */}
                {game.playStatus && game.playStatus !== "Unplayed" && (
                    <div className="absolute bottom-1.5 right-1.5 z-10 flex h-5 items-center justify-center rounded bg-primary/90 px-1.5 text-[10px] font-bold text-primary-foreground shadow-sm backdrop-blur uppercase">
                        {game.playStatus}
                    </div>
                )}

                {/* Hover Overlay with Title and Platforms */}
                <div className="absolute inset-0 z-20 flex flex-col justify-end bg-gradient-to-t from-black/90 via-black/40 to-transparent p-3 opacity-0 transition-opacity duration-300 group-hover:opacity-100">
                    <h3 className="line-clamp-2 text-sm font-semibold text-white drop-shadow-md leading-tight">
                        {game.title}
                    </h3>
                    
                    {platforms.length > 0 && (
                        <div className="mt-2 flex flex-wrap gap-1">
                            {platforms.map(platform => (
                                <span
                                    key={platform}
                                    className={`inline-flex items-center rounded-sm px-1.5 py-0.5 text-[9px] font-bold shadow-sm uppercase ${getPlatformColor(platform)}`}
                                >
                                    {platform}
                                </span>
                            ))}
                        </div>
                    )}
                </div>
            </div>
        </Link>
    );
}
