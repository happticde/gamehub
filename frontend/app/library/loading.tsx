import { Skeleton } from "@/components/ui/skeleton";
import { GameCardSkeleton } from "@/components/GameCardSkeleton";

/**
 * Library loading page — shown automatically by Next.js App Router
 * while the library page's server component is streaming.
 *
 * This uses React Suspense under the hood: Next.js wraps the page in
 * <Suspense fallback={<Loading />}> so this component appears during
 * data fetching and disappears when the real content is ready.
 */
export default function LibraryLoading() {
    return (
        <div className="min-h-screen bg-background">
            <main className="container mx-auto px-4 md:px-8 py-8 md:py-12 max-w-screen-2xl animate-in fade-in duration-300">
                {/* Page header skeleton */}
                <header className="mb-8 space-y-4">
                    <Skeleton className="h-9 w-48" />
                    <Skeleton className="h-4 w-72" />
                </header>

                {/* Filter bar skeleton */}
                <div className="mb-6 flex flex-wrap gap-3">
                    <Skeleton className="h-10 w-64" />
                    <Skeleton className="h-10 w-32" />
                    <Skeleton className="h-10 w-32" />
                </div>

                {/* Game grid skeleton — matches grid-cols-2 sm:3 lg:4 xl:5 */}
                <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 md:gap-6 lg:grid-cols-4 xl:grid-cols-5">
                    {Array.from({ length: 15 }).map((_, i) => (
                        <GameCardSkeleton key={i} />
                    ))}
                </div>
            </main>
        </div>
    );
}
