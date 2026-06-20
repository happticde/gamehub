import { PagedResult, UnifiedGame } from "@/types/game";
import GameCard from "@/components/GameCard";
import LibraryFilters from "@/components/LibraryFilters";
import LibraryPagination from "@/components/LibraryPagination";
import LibraryViewControls from "@/components/LibraryViewControls";
import { fetchCoreApi } from "@/lib/core-api";

/**
 * Library page — a React Server Component (RSC).
 *
 * This function runs on the Next.js Node server before the page is sent to the browser.
 * All filter/sort state is stored in URL search params so it works with browser history,
 * bookmarking, and server-side rendering.
 */
export default async function LibraryPage({
    searchParams,
}: {
    searchParams: Promise<{ [key: string]: string | string[] | undefined }>;
}) {

    // Await the search parameters (required in Next.js 15 app router)
    const resolvedParams = await searchParams;
    const search = typeof resolvedParams.search === 'string' ? resolvedParams.search : undefined;

    // Tags can be undefined, a single string `?tags=RPG`, or an array `?tags=RPG&tags=Action`
    let tags: string[] = [];
    if (resolvedParams.tags) {
        tags = Array.isArray(resolvedParams.tags) ? resolvedParams.tags : [resolvedParams.tags];
    }

    // Platforms can be undefined, a single string `?platforms=Steam`, or an array `?platforms=Steam&platforms=Gog`
    let platforms: string[] = [];
    if (resolvedParams.platforms) {
        platforms = Array.isArray(resolvedParams.platforms) ? resolvedParams.platforms : [resolvedParams.platforms];
    }

    let playStatus: string[] = [];
    if (resolvedParams.playStatus) {
        playStatus = Array.isArray(resolvedParams.playStatus) ? resolvedParams.playStatus : [resolvedParams.playStatus];
    }

    // Sort parameters
    const sortBy = typeof resolvedParams.sortBy === 'string' ? resolvedParams.sortBy : undefined;
    const sortOrder = typeof resolvedParams.sortOrder === 'string' ? resolvedParams.sortOrder : undefined;

    // Developer / Publisher multi-value filters
    let developers: string[] = [];
    if (resolvedParams.developers) {
        developers = Array.isArray(resolvedParams.developers) ? resolvedParams.developers : [resolvedParams.developers];
    }
    let publishers: string[] = [];
    if (resolvedParams.publishers) {
        publishers = Array.isArray(resolvedParams.publishers) ? resolvedParams.publishers : [resolvedParams.publishers];
    }

    const pageParam = typeof resolvedParams.page === 'string' ? parseInt(resolvedParams.page, 10) : 1;
    const currentPage = !isNaN(pageParam) && pageParam > 0 ? pageParam : 1;

    const includeIgnored = resolvedParams.includeIgnored === 'true';

    let games: UnifiedGame[] = [];
    let tagsList: string[] = [];
    let developersList: string[] = [];
    let publishersList: string[] = [];
    let totalCount = 0;
    let pageSize = 50;
    let error: string | null = null;

    try {
        // Construct the query string to pass down to our C# API
        const query = new URLSearchParams();
        if (search) query.append("search", search);

        // Append all tags array values exactly as ASP.NET MVC expects them
        tags.forEach(t => query.append("tags", t));

        // Append all platforms array values exactly as ASP.NET MVC expects them
        platforms.forEach(p => query.append("platforms", p));

        // Append play statuses exactly as ASP.NET MVC expects them
        playStatus.forEach(s => query.append("playStatus", s));

        // Sort params
        if (sortBy) query.append("sortBy", sortBy);
        if (sortOrder) query.append("sortOrder", sortOrder);

        // Developer / Publisher multi-value filters
        developers.forEach(d => query.append("developer", d));
        publishers.forEach(p => query.append("publisher", p));

        query.append("page", currentPage.toString());
        if (includeIgnored) query.append("includeIgnored", "true");

        // Fetch all data in parallel for performance using Promise.all
        const [res, tagsRes, devsRes, pubsRes] = await Promise.all([
            fetchCoreApi(`/library?${query.toString()}`, { cache: "no-store" }),
            // These lists are tenant-owned too. no-store prevents a shared Next.js data cache
            // from ever reusing one tenant's filters for another authenticated session.
            fetchCoreApi("/library/tags", { cache: "no-store" }),
            fetchCoreApi("/library/developers", { cache: "no-store" }),
            fetchCoreApi("/library/publishers", { cache: "no-store" }),
        ]);

        if (!res.ok) {
            throw new Error(`Failed to fetch data: ${res.status} ${res.statusText}`);
        }

        const result: PagedResult<UnifiedGame> = await res.json();
        games = result.items || [];
        totalCount = result.totalCount || 0;
        pageSize = result.pageSize || 50;

        if (tagsRes.ok) tagsList = await tagsRes.json();
        if (devsRes.ok) developersList = await devsRes.json();
        if (pubsRes.ok) publishersList = await pubsRes.json();
    } catch (e: any) {
        error = e.message || "An unexpected error occurred while fetching your library.";
        console.error("Fetch Library Error:", error);
    }

    return (
        <div className="container mx-auto px-4 py-8 md:px-8 md:py-12">
            <div className="mb-8 flex flex-col items-start justify-between gap-4 md:flex-row md:items-end">
                <div>
                    <h1 className="text-3xl font-bold tracking-tight md:text-4xl">Unified Library</h1>
                    <p className="mt-2 text-muted-foreground">
                        Explore your aggregated collection of {totalCount > 0 ? totalCount : "0"} synchronized games.
                    </p>
                </div>
            </div>

            {error ? (
                <div className="mb-8 rounded-lg border border-destructive/50 bg-destructive/10 p-6 text-center text-destructive">
                    <h3 className="font-semibold">Unable to connect to GameHub Core API.</h3>
                    <p className="mt-2 text-sm">{error}</p>
                    <p className="mt-4 text-xs opacity-70">Hint: Ensure your Docker Compose backend is running and the proxy in next.config.ts is correctly pointing to the host machine.</p>
                </div>
            ) : null}
            
            {!error && (
                <LibraryViewControls 
                    games={games} 
                    filters={
                        <LibraryFilters
                            availableTags={tagsList}
                            availableDevelopers={developersList}
                            availablePublishers={publishersList}
                        />
                    }
                />
            )}

            {/* Server component handles total page calculation blindly for the client */}
            {totalCount > 0 && (
                <div className="sticky bottom-6 z-40 flex justify-center pointer-events-none mt-8">
                    <div className="pointer-events-auto rounded-full border border-border bg-background/95 backdrop-blur-md px-4 py-2 shadow-lg">
                        <LibraryPagination
                            currentPage={currentPage}
                            totalPages={Math.ceil(totalCount / pageSize)}
                            className="m-0"
                        />
                    </div>
                </div>
            )}
        </div>
    );
}
