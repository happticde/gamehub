import Image from "next/image";
import Link from "next/link";
import { UnifiedGame } from "@/types/game";
import { getPlatformColor } from "@/lib/platform-colors";
import { Star } from "lucide-react";

interface GameListRowProps {
    game: UnifiedGame;
}

export default function GameListRow({ game }: GameListRowProps) {
    const platforms = game.platforms
        ? Array.from(new Set(game.platforms.map(p => p.platform)))
        : [];
    const physicalPlatforms = Array.from(new Set(game.physicalCopies?.map(copy => copy.platform) ?? []));

    const developer = game.developers?.split(",")[0]?.trim();
    const releaseYear = game.firstReleaseDate ? new Date(game.firstReleaseDate).getFullYear() : null;
    const addedDate = game.enrichedAt ? new Date(game.enrichedAt).toLocaleDateString() : 'Awaiting sync';

    return (
        <Link href={`/library/${game.id}`} className="block group">
            <div className="flex items-center gap-4 rounded-lg border border-transparent p-2 transition-colors hover:bg-muted/50 hover:border-border">
                
                {/* Tiny Thumbnail */}
                <div className="relative h-12 w-9 shrink-0 overflow-hidden rounded bg-muted shadow-sm">
                    {game.coverUrl ? (
                         <Image
                         src={game.coverUrl}
                         alt={`${game.title} cover art`}
                         fill
                         className="object-cover"
                         sizes="36px"
                         unoptimized
                     />
                    ) : (
                        <div className="flex h-full items-center justify-center text-[8px] uppercase tracking-tighter text-muted-foreground/50">
                            No<br/>Art
                        </div>
                    )}
                </div>

                {/* Title & Meta Column */}
                <div className="min-w-0 flex-1">
                    <h3 className="truncate font-semibold text-foreground group-hover:text-primary transition-colors">
                        {game.title}
                    </h3>
                    <div className="mt-0.5 flex items-center gap-2 text-xs text-muted-foreground">
                        <span className="truncate max-w-[120px] sm:max-w-[200px]">
                            {developer || "Unknown Developer"}
                        </span>
                        {releaseYear && <span>• {releaseYear}</span>}
                    </div>
                </div>

                {/* Platforms Column (Hidden on tiny screens) */}
                <div className="hidden min-w-[120px] items-center gap-1 sm:flex flex-wrap flex-1">
                    {platforms.map(platform => (
                        <span
                            key={platform}
                            className={`inline-flex items-center rounded px-1.5 py-0.5 text-[10px] font-semibold uppercase ${getPlatformColor(platform)}`}
                        >
                            {platform}
                        </span>
                    ))}
                    {physicalPlatforms.length > 0 && (
                        <span className="basis-full border-t border-dashed border-primary/25 pt-1 text-[9px] font-medium uppercase tracking-wide text-primary">
                            Physical {physicalPlatforms.join(" · ")}
                        </span>
                    )}
                </div>

                {/* Play Status (Hidden on mobile) */}
                <div className="hidden w-[100px] shrink-0 md:block">
                    <span className="text-xs font-medium text-muted-foreground uppercase tracking-wider">
                        {game.playStatus}
                    </span>
                </div>

                {/* Rating Column */}
                <div className="flex w-[80px] shrink-0 items-center justify-end gap-2 text-sm md:w-[120px]">
                    {game.userRating ? (
                        <div className="flex items-center gap-0.5" title="Your Rating">
                            <Star className="h-3 w-3 fill-yellow-400 text-yellow-400" />
                            <span className="font-medium">{game.userRating}</span>
                        </div>
                    ) : game.rating ? (
                        <div className="flex items-center justify-center w-7 h-7 rounded bg-secondary text-xs font-bold text-secondary-foreground" title="Critic Rating">
                            {Math.round(game.rating)}
                        </div>
                    ) : (
                        <span className="text-muted-foreground/50">-</span>
                    )}
                </div>

                {/* Date Added (Hidden on tablets/mobile) */}
                <div className="hidden w-[100px] shrink-0 text-right text-xs text-muted-foreground lg:block">
                    {addedDate}
                </div>

            </div>
        </Link>
    );
}
