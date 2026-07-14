"use client";

import { useRouter, usePathname, useSearchParams } from "next/navigation";
import { useCallback, useEffect, useRef, useState, useTransition } from "react";
import { Filter, Eye, EyeOff, Shuffle } from "lucide-react";
import { Input } from "@/components/ui/input";
import { MultiSelect } from "@/components/ui/multi-select";
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from "@/components/ui/select";
import {
    Dialog,
    DialogContent,
    DialogHeader,
    DialogTitle,
    DialogTrigger,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";

interface LibraryFiltersProps {
    availableTags: string[];
    availableDevelopers: string[];
    availablePublishers: string[];
}

const SORT_OPTIONS = [
    { label: "Title (A–Z)", value: "title_asc" },
    { label: "Title (Z–A)", value: "title_desc" },
    { label: "Rating (High → Low)", value: "rating_desc" },
    { label: "Rating (Low → High)", value: "rating_asc" },
    { label: "Release Date (Newest)", value: "releasedate_desc" },
    { label: "Release Date (Oldest)", value: "releasedate_asc" },
] as const;

export default function LibraryFilters({ availableTags, availableDevelopers, availablePublishers }: LibraryFiltersProps) {
    const router = useRouter();
    const pathname = usePathname();
    const searchParams = useSearchParams();
    const [isPending, startTransition] = useTransition();
    const [isPickingRandom, setIsPickingRandom] = useState(false);
    const pendingRefreshParams = useRef<string | null>(null);
    const currentParams = searchParams.toString();

    // Read current state from URL
    const defaultSearch = searchParams.get("search") || "";
    const defaultTags = searchParams.getAll("tags");
    const defaultPlatforms = searchParams.getAll("platforms");
    const defaultPhysicalPlatforms = searchParams.getAll("physicalPlatforms");
    const defaultPlayStatuses = searchParams.getAll("playStatus");
    const defaultDevelopers = searchParams.getAll("developers");
    const defaultPublishers = searchParams.getAll("publishers");

    const urlSortBy = searchParams.get("sortBy") || "title";
    const urlSortOrder = searchParams.get("sortOrder") || "asc";
    const defaultSort = `${urlSortBy}_${urlSortOrder}`;

    const defaultIncludeIgnored = searchParams.get("includeIgnored") === "true";

    const AVAILABLE_PLATFORMS = ["Steam", "Gog", "Psn", "Epic"];
    const AVAILABLE_PHYSICAL_PLATFORMS = ["PS3", "PS4", "PS5"];
    const AVAILABLE_PLAY_STATUSES = ["Unplayed", "Played", "Playing", "Completed", "Abandoned"];

    const [searchQuery, setSearchQuery] = useState(defaultSearch);

    // Determine if extra filters are active (to auto-expand the section)
    const hasActiveExtraFilters =
        defaultDevelopers.length > 0 || defaultPublishers.length > 0;
    const [showMore, setShowMore] = useState(hasActiveExtraFilters);

    // Count how many extra filters are available
    const extraFilterCount = [
        availableDevelopers.length > 0,
        availablePublishers.length > 0,
    ].filter(Boolean).length;

    // A search-param navigation can update the address bar while Next.js still shows a cached
    // React Server Component payload. Refresh only after useSearchParams confirms that the target
    // URL is active; refreshing immediately after router.push could refresh the previous URL.
    useEffect(() => {
        if (pendingRefreshParams.current !== currentParams) return;

        pendingRefreshParams.current = null;
        startTransition(() => {
            router.refresh();
        });
    }, [currentParams, router]);

    // Generic URL push helper
    const pushParams = useCallback(
        (updater: (params: URLSearchParams) => void) => {
            const params = new URLSearchParams(currentParams);
            updater(params);
            params.delete("page");

            const nextParams = params.toString();
            if (nextParams === currentParams) return;

            pendingRefreshParams.current = nextParams;
            const href = nextParams ? `${pathname}?${nextParams}` : pathname;
            startTransition(() => {
                router.push(href);
            });
        },
        [currentParams, pathname, router]
    );

    // --- Filter updaters ---

    const updateSearchUrl = useCallback(
        (value: string) => pushParams((p) => {
            if (value) p.set("search", value);
            else p.delete("search");
        }),
        [pushParams]
    );

    const updateTagsUrl = useCallback(
        (newTags: string[]) => pushParams((p) => {
            p.delete("tags");
            newTags.forEach(t => p.append("tags", t));
        }),
        [pushParams]
    );

    const updatePlatformsUrl = useCallback(
        (newPlatforms: string[]) => pushParams((p) => {
            p.delete("platforms");
            newPlatforms.forEach(pl => p.append("platforms", pl));
        }),
        [pushParams]
    );

    const updatePhysicalPlatformsUrl = useCallback(
        (newPlatforms: string[]) => pushParams((p) => {
            p.delete("physicalPlatforms");
            newPlatforms.forEach(platform => p.append("physicalPlatforms", platform));
        }),
        [pushParams]
    );

    const updatePlayStatusUrl = useCallback(
        (newStatuses: string[]) => pushParams((p) => {
            p.delete("playStatus");
            newStatuses.forEach(s => p.append("playStatus", s));
        }),
        [pushParams]
    );

    const updateSortUrl = useCallback(
        (composite: string) => pushParams((p) => {
            const lastUnderscore = composite.lastIndexOf("_");
            const sortBy = composite.substring(0, lastUnderscore);
            const sortOrder = composite.substring(lastUnderscore + 1);
            p.set("sortBy", sortBy);
            p.set("sortOrder", sortOrder);
        }),
        [pushParams]
    );

    const updateDevelopersUrl = useCallback(
        (newDevs: string[]) => pushParams((p) => {
            p.delete("developers");
            newDevs.forEach(d => p.append("developers", d));
        }),
        [pushParams]
    );

    const updatePublishersUrl = useCallback(
        (newPubs: string[]) => pushParams((p) => {
            p.delete("publishers");
            newPubs.forEach(pub => p.append("publishers", pub));
        }),
        [pushParams]
    );

    const updateIncludeIgnoredUrl = useCallback(
        (include: boolean) => pushParams((p) => {
            if (include) p.set("includeIgnored", "true");
            else p.delete("includeIgnored");
        }),
        [pushParams]
    );

    // Debounce text search
    useEffect(() => {
        const timer = setTimeout(() => {
            if (searchQuery !== (searchParams.get("search") || "")) {
                updateSearchUrl(searchQuery);
            }
        }, 400);
        return () => clearTimeout(timer);
    }, [searchQuery, searchParams, updateSearchUrl]);

    const handleSurpriseMe = async () => {
        setIsPickingRandom(true);
        try {
            const params = new URLSearchParams(searchParams.toString());
            params.delete("sortBy");
            params.delete("sortOrder");
            params.delete("page");
            
            const res = await fetch(`/api/core/library/random?${params.toString()}`);
            if (res.ok) {
                const data = await res.json();
                if (data.id) {
                    router.push(`/library/${data.id}`);
                }
            } else if (res.status === 404) {
                alert("No games found matching your current filters.");
            }
        } catch (e) {
            console.error("Error picking random game:", e);
        } finally {
            setIsPickingRandom(false);
        }
    };

    const FilterControls = (
        <>
            {/* Row 2: Core filters (always visible) */}
            <div className="flex w-full flex-col gap-3 sm:flex-row sm:items-center sm:flex-wrap">
                <div className="w-full space-y-1 sm:w-[200px]">
                    <span className="block text-xs font-medium text-muted-foreground">Digital libraries</span>
                    <MultiSelect
                        options={AVAILABLE_PLATFORMS}
                        selected={defaultPlatforms}
                        onChange={updatePlatformsUrl}
                        placeholder="Platforms..."
                        searchPlaceholder="Search platforms..."
                        emptyMessage="No platforms found."
                    />
                </div>

                <div className="w-full space-y-1 rounded-lg border border-dashed border-primary/30 bg-primary/5 p-2 sm:w-[200px]">
                    <span className="block text-xs font-medium text-primary">Physical collection</span>
                    <MultiSelect
                        options={AVAILABLE_PHYSICAL_PLATFORMS}
                        selected={defaultPhysicalPlatforms}
                        onChange={updatePhysicalPlatformsUrl}
                        placeholder="Physical platforms..."
                        searchPlaceholder="Search consoles..."
                        emptyMessage="No consoles found."
                    />
                </div>

                <div className="w-full sm:w-[200px]">
                    <MultiSelect
                        options={AVAILABLE_PLAY_STATUSES}
                        selected={defaultPlayStatuses}
                        onChange={updatePlayStatusUrl}
                        placeholder="Play Status..."
                        searchPlaceholder="Search statuses..."
                        emptyMessage="No statuses found."
                    />
                </div>

                <div className="w-full sm:w-[220px]">
                    <MultiSelect
                        options={availableTags}
                        selected={defaultTags}
                        onChange={updateTagsUrl}
                        placeholder="Tags..."
                        searchPlaceholder="Search tags..."
                        emptyMessage="No tags found."
                    />
                </div>

                {/* "More Filters" toggle */}
                {extraFilterCount > 0 && (
                    <button
                        type="button"
                        onClick={() => setShowMore(!showMore)}
                        className={`inline-flex items-center gap-1.5 rounded-lg border px-3 h-10 text-sm transition-colors ${showMore || hasActiveExtraFilters
                                ? "border-primary/50 bg-primary/10 text-primary"
                                : "border-border bg-background text-muted-foreground hover:bg-accent hover:text-accent-foreground"
                            }`}
                    >
                        <Filter className="h-3.5 w-3.5" />
                        More
                        {hasActiveExtraFilters && (
                            <span className="ml-0.5 flex h-5 w-5 items-center justify-center rounded-full bg-primary text-[10px] font-bold text-primary-foreground">
                                {defaultDevelopers.length + defaultPublishers.length}
                            </span>
                        )}
                    </button>
                )}

                {/* "Show Hidden Games" toggle */}
                <button
                    type="button"
                    onClick={() => updateIncludeIgnoredUrl(!defaultIncludeIgnored)}
                    className={`inline-flex items-center gap-1.5 rounded-lg border px-3 h-10 text-sm transition-colors ${defaultIncludeIgnored
                            ? "border-primary/50 bg-primary/10 text-primary"
                            : "border-border bg-background text-muted-foreground hover:bg-accent hover:text-accent-foreground"
                        }`}
                >
                    {defaultIncludeIgnored ? <Eye className="h-3.5 w-3.5" /> : <EyeOff className="h-3.5 w-3.5" />}
                    {defaultIncludeIgnored ? "Hide ignored" : "Show ignored"}
                </button>
            </div>

            {/* Row 3: Extra filters (collapsible) */}
            {showMore && extraFilterCount > 0 && (
                <div className="flex w-full flex-col gap-3 sm:flex-row sm:items-center sm:flex-wrap animate-in slide-in-from-top-2 fade-in duration-200">
                    {availableDevelopers.length > 0 && (
                        <div className="w-full sm:w-[220px]">
                            <MultiSelect
                                options={availableDevelopers}
                                selected={defaultDevelopers}
                                onChange={updateDevelopersUrl}
                                placeholder="Developers..."
                                searchPlaceholder="Search developers..."
                                emptyMessage="No developers found."
                            />
                        </div>
                    )}

                    {availablePublishers.length > 0 && (
                        <div className="w-full sm:w-[220px]">
                            <MultiSelect
                                options={availablePublishers}
                                selected={defaultPublishers}
                                onChange={updatePublishersUrl}
                                placeholder="Publishers..."
                                searchPlaceholder="Search publishers..."
                                emptyMessage="No publishers found."
                            />
                        </div>
                    )}
                </div>
            )}
        </>
    );

    const SearchAndSort = (
        <div className="flex w-full flex-row gap-2 items-center">
            <div className="relative flex-1 max-w-sm">
                <svg
                    className="absolute left-2.5 top-2.5 h-4 w-4 text-muted-foreground"
                    fill="none"
                    stroke="currentColor"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    strokeWidth="2"
                    viewBox="0 0 24 24"
                >
                    <circle cx="11" cy="11" r="8" />
                    <path d="m21 21-4.3-4.3" />
                </svg>
                <Input
                    type="search"
                    placeholder="Search library..."
                    className="pl-9 bg-background h-10"
                    value={searchQuery}
                    onChange={(e) => setSearchQuery(e.target.value)}
                />
            </div>

            <Button
                variant="default"
                onClick={handleSurpriseMe}
                disabled={isPickingRandom}
                className="h-10 px-3 md:px-4 shrink-0 gap-2 whitespace-nowrap bg-primary/90 hover:bg-primary shadow-sm"
                title="Pick a random game based on current filters"
            >
                {isPickingRandom ? (
                    <svg className="h-4 w-4 animate-spin" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                        <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                        <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
                    </svg>
                ) : (
                    <Shuffle className="h-4 w-4" />
                )}
                <span className="hidden sm:inline">Surprise Me</span>
            </Button>

            {/* Desktop sort */}
            <div className="hidden md:block w-[220px]">
                <Select value={defaultSort} onValueChange={updateSortUrl}>
                    <SelectTrigger className="w-full h-10">
                        <SelectValue placeholder="Sort by..." />
                    </SelectTrigger>
                    <SelectContent>
                        {SORT_OPTIONS.map((opt) => (
                            <SelectItem key={opt.value} value={opt.value}>
                                {opt.label}
                            </SelectItem>
                        ))}
                    </SelectContent>
                </Select>
            </div>

            {/* Mobile Filter Button */}
            <div className="md:hidden">
                <Dialog>
                    <DialogTrigger asChild>
                        <Button variant="outline" className="h-10 px-3 shrink-0 gap-2">
                            <Filter className="h-4 w-4" /> Filters
                        </Button>
                    </DialogTrigger>
                    <DialogContent className="max-h-[85vh] flex flex-col sm:max-w-[425px]">
                        <DialogHeader>
                            <DialogTitle>Filters & Sorting</DialogTitle>
                        </DialogHeader>
                        <div className="flex-1 overflow-y-auto space-y-4 py-4 pr-2">
                            <div className="flex flex-col gap-2">
                                <label className="text-sm font-medium">Sort By</label>
                                <Select value={defaultSort} onValueChange={updateSortUrl}>
                                    <SelectTrigger className="w-full h-10">
                                        <SelectValue placeholder="Sort by..." />
                                    </SelectTrigger>
                                    <SelectContent>
                                        {SORT_OPTIONS.map((opt) => (
                                            <SelectItem key={opt.value} value={opt.value}>
                                                {opt.label}
                                            </SelectItem>
                                        ))}
                                    </SelectContent>
                                </Select>
                            </div>
                            <hr />
                            {FilterControls}
                        </div>
                    </DialogContent>
                </Dialog>
            </div>

            {/* Loading spinner */}
            <div className={`flex h-10 w-10 items-center justify-center shrink-0 transition-opacity ${isPending ? 'opacity-100' : 'opacity-0'}`}>
                <svg className="h-5 w-5 animate-spin text-primary" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                    <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                    <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
                </svg>
            </div>
        </div>
    );

    return (
        <div className="flex w-full flex-col gap-3">
            {SearchAndSort}

            {/* Desktop Filters */}
            <div className="hidden md:flex w-full flex-col gap-3">
                {FilterControls}
            </div>
        </div>
    );
}
