"use client";

import { useRouter, usePathname, useSearchParams } from "next/navigation";
import { useCallback, useEffect, useState, useTransition } from "react";
import { Input } from "@/components/ui/input";
import { MultiSelect } from "@/components/ui/multi-select";
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from "@/components/ui/select";

/**
 * Props for the LibraryFilters component.
 * All available filter options are passed down from the server component
 * so we don't need to fetch them client-side.
 */
interface LibraryFiltersProps {
    availableTags: string[];
    availableDevelopers: string[];
    availablePublishers: string[];
}

/** Predefined sort options — each maps to a sortBy + sortOrder pair */
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

    // Initialize local state from URL so it persists across refreshes
    const defaultSearch = searchParams.get("search") || "";
    // In Next.js URLSearchParams, `getAll` pulls every instance of `?tags=X` into an array
    const defaultTags = searchParams.getAll("tags");
    const defaultPlatforms = searchParams.getAll("platforms");

    // Compose the current sort value from sortBy + sortOrder params
    const urlSortBy = searchParams.get("sortBy") || "title";
    const urlSortOrder = searchParams.get("sortOrder") || "asc";
    const defaultSort = `${urlSortBy}_${urlSortOrder}`;

    const defaultDeveloper = searchParams.get("developer") || "";
    const defaultPublisher = searchParams.get("publisher") || "";

    // Hardcode platforms since the C# backend uses a fixed Platform enum
    const AVAILABLE_PLATFORMS = ["Steam", "Gog", "Psn", "Epic"];

    const [searchQuery, setSearchQuery] = useState(defaultSearch);

    // Generic helper to push updated params to the URL via a React Transition
    const pushParams = useCallback(
        (updater: (params: URLSearchParams) => void) => {
            const params = new URLSearchParams(searchParams.toString());
            updater(params);
            // Reset to page 1 whenever any filter changes
            params.delete("page");
            startTransition(() => {
                router.push(`${pathname}?${params.toString()}`);
            });
        },
        [pathname, router, searchParams]
    );

    // --- Individual filter updaters ---

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

    const updateSortUrl = useCallback(
        (composite: string) => pushParams((p) => {
            // composite is e.g. "rating_desc" — split into sortBy + sortOrder
            const lastUnderscore = composite.lastIndexOf("_");
            const sortBy = composite.substring(0, lastUnderscore);
            const sortOrder = composite.substring(lastUnderscore + 1);
            p.set("sortBy", sortBy);
            p.set("sortOrder", sortOrder);
        }),
        [pushParams]
    );

    const updateDeveloperUrl = useCallback(
        (value: string) => pushParams((p) => {
            if (value && value !== "__all__") p.set("developer", value);
            else p.delete("developer");
        }),
        [pushParams]
    );

    const updatePublisherUrl = useCallback(
        (value: string) => pushParams((p) => {
            if (value && value !== "__all__") p.set("publisher", value);
            else p.delete("publisher");
        }),
        [pushParams]
    );

    // Debounce the text input to avoid spamming the API on every keystroke
    useEffect(() => {
        const timer = setTimeout(() => {
            // Only push if it actually changed from what's in the URL
            if (searchQuery !== (searchParams.get("search") || "")) {
                updateSearchUrl(searchQuery);
            }
        }, 400);

        return () => clearTimeout(timer);
    }, [searchQuery, searchParams, updateSearchUrl]);

    return (
        <div className="flex w-full flex-col gap-3">
            {/* Row 1: Search + Sort */}
            <div className="flex w-full flex-col gap-3 sm:flex-row sm:items-center">
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
                        className="pl-9 bg-background"
                        value={searchQuery}
                        onChange={(e) => setSearchQuery(e.target.value)}
                    />
                </div>

                {/* Sort By dropdown */}
                <div className="w-full sm:w-[220px]">
                    <Select value={defaultSort} onValueChange={updateSortUrl}>
                        <SelectTrigger className="w-full">
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

                {/* Loading spinner when Next.js is fetching new server data */}
                <div className={`flex h-9 w-9 items-center justify-center shrink-0 transition-opacity ${isPending ? 'opacity-100' : 'opacity-0'}`}>
                    <svg className="h-5 w-5 animate-spin text-primary" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                        <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                        <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
                    </svg>
                </div>
            </div>

            {/* Row 2: Multi-select filters + Developer/Publisher dropdowns */}
            <div className="flex w-full flex-col gap-3 sm:flex-row sm:items-center sm:flex-wrap">
                <div className="w-full sm:w-[200px]">
                    <MultiSelect
                        options={AVAILABLE_PLATFORMS}
                        selected={defaultPlatforms}
                        onChange={updatePlatformsUrl}
                        placeholder="Platforms..."
                    />
                </div>

                <div className="w-full sm:w-[220px]">
                    <MultiSelect
                        options={availableTags}
                        selected={defaultTags}
                        onChange={updateTagsUrl}
                        placeholder="Tags..."
                    />
                </div>

                {/* Developer filter (single select) */}
                {availableDevelopers.length > 0 && (
                    <div className="w-full sm:w-[200px]">
                        <Select value={defaultDeveloper || "__all__"} onValueChange={updateDeveloperUrl}>
                            <SelectTrigger className="w-full">
                                <SelectValue placeholder="Developer..." />
                            </SelectTrigger>
                            <SelectContent>
                                <SelectItem value="__all__">All Developers</SelectItem>
                                {availableDevelopers.map((dev) => (
                                    <SelectItem key={dev} value={dev}>
                                        {dev}
                                    </SelectItem>
                                ))}
                            </SelectContent>
                        </Select>
                    </div>
                )}

                {/* Publisher filter (single select) */}
                {availablePublishers.length > 0 && (
                    <div className="w-full sm:w-[200px]">
                        <Select value={defaultPublisher || "__all__"} onValueChange={updatePublisherUrl}>
                            <SelectTrigger className="w-full">
                                <SelectValue placeholder="Publisher..." />
                            </SelectTrigger>
                            <SelectContent>
                                <SelectItem value="__all__">All Publishers</SelectItem>
                                {availablePublishers.map((pub) => (
                                    <SelectItem key={pub} value={pub}>
                                        {pub}
                                    </SelectItem>
                                ))}
                            </SelectContent>
                        </Select>
                    </div>
                )}
            </div>
        </div>
    );
}
