"use client";

import { useRouter, usePathname, useSearchParams } from "next/navigation";
import { useCallback, useEffect, useState, useTransition } from "react";
import { Input } from "@/components/ui/input";
import { MultiSelect } from "@/components/ui/multi-select";

interface LibraryFiltersProps {
    availableTags: string[];
}

export default function LibraryFilters({ availableTags }: LibraryFiltersProps) {
    const router = useRouter();
    const pathname = usePathname();
    const searchParams = useSearchParams();
    const [isPending, startTransition] = useTransition();

    // Initialize local state from URL so it persists across refreshes
    const defaultSearch = searchParams.get("search") || "";
    // In Next.js URLSearchParams, `getAll` pulls every instance of `?tags=X` into an array
    const defaultTags = searchParams.getAll("tags");

    const [searchQuery, setSearchQuery] = useState(defaultSearch);

    // Memoized function to update text search
    const updateSearchUrl = useCallback(
        (value: string) => {
            const params = new URLSearchParams(searchParams.toString());
            if (value) {
                params.set("search", value);
            } else {
                params.delete("search");
            }

            params.delete("page");

            startTransition(() => {
                router.push(`${pathname}?${params.toString()}`);
            });
        },
        [pathname, router, searchParams]
    );

    // Memoized function to update the tags array
    const updateTagsUrl = useCallback(
        (newTags: string[]) => {
            const params = new URLSearchParams(searchParams.toString());

            // Wipe the old tags parameters clean
            params.delete("tags");

            // Append every selected tag to the URL (`?tags=RPG&tags=Action`)
            newTags.forEach(t => params.append("tags", t));

            // Reset page to 1 when filters change
            params.delete("page");

            startTransition(() => {
                router.push(`${pathname}?${params.toString()}`);
            });
        },
        [pathname, router, searchParams]
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
        <div className="flex w-full flex-col gap-4 sm:flex-row sm:items-center">
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

            <div className="w-full sm:w-[280px]">
                <MultiSelect
                    options={availableTags}
                    selected={defaultTags}
                    onChange={updateTagsUrl}
                    placeholder="Select tags..."
                />
            </div>

            {/* Loading spinner when Next.js is fetching new server data */}
            <div className={`flex h-9 w-9 items-center justify-center transition-opacity ${isPending ? 'opacity-100' : 'opacity-0'}`}>
                <svg className="h-5 w-5 animate-spin text-primary" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                    <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                    <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
                </svg>
            </div>
        </div>
    );
}
