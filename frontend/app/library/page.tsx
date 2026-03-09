import { PagedResult, UnifiedGame } from "@/types/game";
import GameCard from "@/components/GameCard";
import LibraryFilters from "@/components/LibraryFilters";
import LibraryPagination from "@/components/LibraryPagination";

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

    // Parse page number (default to 1, ensure it's a positive integer)
    const pageParam = typeof resolvedParams.page === 'string' ? parseInt(resolvedParams.page, 10) : 1;
    const currentPage = !isNaN(pageParam) && pageParam > 0 ? pageParam : 1;

    // We use standard fetch syntax. The URL hits our own Next.js server route, 
    // which is then proxied via next.config.ts down to the C# Core API.
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

        // Sort params
        if (sortBy) query.append("sortBy", sortBy);
        if (sortOrder) query.append("sortOrder", sortOrder);

        // Developer / Publisher multi-value filters
        developers.forEach(d => query.append("developer", d));
        publishers.forEach(p => query.append("publisher", p));

        query.append("page", currentPage.toString());

        // We proxy it through Next.js
        const fetchUrl = `http://localhost:3000/api/core/library?${query.toString()}`;

        // Fetch all data in parallel for performance using Promise.all
        const [res, tagsRes, devsRes, pubsRes] = await Promise.all([
            fetch(fetchUrl, { cache: "no-store" }),
            fetch("http://localhost:3000/api/core/library/tags", { cache: "no-store", next: { revalidate: 300 } }),
            fetch("http://localhost:3000/api/core/library/developers", { cache: "no-store", next: { revalidate: 300 } }),
            fetch("http://localhost:3000/api/core/library/publishers", { cache: "no-store", next: { revalidate: 300 } }),
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

            {/* Filter controls — sort, search, platform/tag/dev/pub dropdowns */}
            <div className="mb-8">
                <LibraryFilters
                    availableTags={tagsList}
                    availableDevelopers={developersList}
                    availablePublishers={publishersList}
                />
            </div>

            {error ? (
                <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-6 text-center text-destructive">
                    <h3 className="font-semibold">Unable to connect to GameHub Core API.</h3>
                    <p className="mt-2 text-sm">{error}</p>
                    <p className="mt-4 text-xs opacity-70">Hint: Ensure your Docker Compose backend is running and the proxy in next.config.ts is correctly pointing to the host machine.</p>
                </div>
            ) : games.length === 0 ? (
                <div className="flex min-h-[400px] flex-col items-center justify-center rounded-lg border border-border bg-card p-10 text-center animate-in fade-in duration-700 shadow-sm">
                    <div className="rounded-full bg-muted p-4">
                        <svg className="h-8 w-8 text-muted-foreground" fill="none" strokeWidth="1.5" stroke="currentColor" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">
                            <path d="M2.25 15.75l5.159-5.159a2.25 2.25 0 013.182 0l5.159 5.159m-1.5-1.5l1.409-1.409a2.25 2.25 0 013.182 0l2.909 2.909m-18 3.75h16.5a1.5 1.5 0 001.5-1.5V6a1.5 1.5 0 00-1.5-1.5H3.75A1.5 1.5 0 002.25 6v12a1.5 1.5 0 001.5 1.5zm10.5-11.25h.008v.008h-.008V8.25zm.375 0a.375.375 0 11-.75 0 .375.375 0 01.75 0z" strokeLinecap="round" strokeLinejoin="round" />
                        </svg>
                    </div>
                    <h3 className="mt-4 text-lg font-semibold">Your library is empty</h3>
                    <p className="mt-2 max-w-sm text-sm text-muted-foreground">
                        It looks like you haven&apos;t synchronized any games yet. Head over to the Ingest Sync panel to pull from your external providers.
                    </p>
                </div>
            ) : (
                <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 md:gap-6 lg:grid-cols-4 xl:grid-cols-5 animate-in slide-in-from-bottom-4 fade-in duration-500">
                    {games.map((game) => (
                        <GameCard key={game.id} game={game} />
                    ))}
                </div>
            )}

            {/* Server component handles total page calculation blindly for the client */}
            {totalCount > 0 && (
                <LibraryPagination
                    currentPage={currentPage}
                    totalPages={Math.ceil(totalCount / pageSize)}
                />
            )}
        </div>
    );
}
