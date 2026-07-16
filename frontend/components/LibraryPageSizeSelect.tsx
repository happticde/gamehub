"use client";

import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useTransition } from "react";
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from "@/components/ui/select";
import {
    LIBRARY_PAGE_SIZES,
    LibraryPageSize,
    parseLibraryPageSize,
    updateLibraryPaginationParams,
} from "@/lib/library-pagination";

interface LibraryPageSizeSelectProps {
    pageSize: LibraryPageSize;
}

export default function LibraryPageSizeSelect({ pageSize }: LibraryPageSizeSelectProps) {
    const router = useRouter();
    const pathname = usePathname();
    const searchParams = useSearchParams();
    const [isPending, startTransition] = useTransition();

    const updatePageSize = (value: string) => {
        const nextPageSize = parseLibraryPageSize(value);
        const params = updateLibraryPaginationParams(searchParams, 1, nextPageSize);
        const href = `${pathname}?${params.toString()}`;

        startTransition(() => {
            router.push(href);
        });
    };

    return (
        <div
            className={`flex h-10 items-center gap-2 rounded-md border border-border bg-background px-2 transition-opacity ${
                isPending ? "pointer-events-none opacity-60" : ""
            }`}
        >
            <span className="whitespace-nowrap text-xs font-medium text-muted-foreground">
                Per page
            </span>
            <Select
                value={pageSize.toString()}
                onValueChange={updatePageSize}
                disabled={isPending}
            >
                <SelectTrigger
                    size="sm"
                    className="w-[68px] border-0 bg-transparent shadow-none focus-visible:ring-2"
                    aria-label="Games per page"
                >
                    <SelectValue />
                </SelectTrigger>
                <SelectContent>
                    {LIBRARY_PAGE_SIZES.map((option) => (
                        <SelectItem key={option} value={option.toString()}>
                            {option}
                        </SelectItem>
                    ))}
                </SelectContent>
            </Select>
        </div>
    );
}
