import { Skeleton } from "@/components/ui/skeleton";

/**
 * GameCardSkeleton — matches the exact layout shape of <GameCard>.
 *
 * Shows a 3:4 aspect-ratio cover placeholder, a title line, and a subtitle line.
 * Used in loading.tsx to give the user a visual hint of what's coming.
 */
export function GameCardSkeleton() {
    return (
        <div className="group relative overflow-hidden rounded-xl border border-border bg-card">
            {/* Cover image placeholder — matches aspect-[3/4] */}
            <Skeleton className="aspect-[3/4] w-full" />

            {/* Bottom info area */}
            <div className="p-3 space-y-2">
                {/* Title */}
                <Skeleton className="h-4 w-3/4" />
                {/* Genre / subtitle */}
                <Skeleton className="h-3 w-1/2" />
            </div>
        </div>
    );
}
