import { Skeleton } from "@/components/ui/skeleton";

/**
 * Stats loading page — shown automatically by Next.js App Router
 * while the stats page data is streaming.
 *
 * Mirrors the real stats page layout: header, 4 stat cards, 2 chart areas.
 */
export default function StatsLoading() {
    return (
        <div className="min-h-screen bg-background">
            <main className="container mx-auto px-4 md:px-8 py-8 md:py-12 max-w-screen-xl animate-in fade-in duration-300">
                {/* Page header skeleton */}
                <header className="mb-10 border-b pb-8 space-y-4">
                    <div className="flex items-center gap-3">
                        <Skeleton className="h-8 w-8 rounded-md" />
                        <Skeleton className="h-9 w-56" />
                    </div>
                    <Skeleton className="h-4 w-96 max-w-full" />
                </header>

                {/* Hero stat cards — 4 cards in a row */}
                <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4 mb-10">
                    {Array.from({ length: 4 }).map((_, i) => (
                        <div key={i} className="rounded-xl border border-border bg-card p-6 space-y-3">
                            <Skeleton className="h-3 w-24" />
                            <Skeleton className="h-8 w-16" />
                            <Skeleton className="h-3 w-32" />
                        </div>
                    ))}
                </div>

                {/* Chart areas — 2 side by side */}
                <div className="grid gap-6 lg:grid-cols-2">
                    {Array.from({ length: 2 }).map((_, i) => (
                        <div key={i} className="rounded-xl border border-border bg-card p-6 space-y-4">
                            <Skeleton className="h-5 w-40" />
                            <Skeleton className="h-40 w-full rounded-lg" />
                        </div>
                    ))}
                </div>
            </main>
        </div>
    );
}
