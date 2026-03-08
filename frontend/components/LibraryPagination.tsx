"use client";

import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useTransition } from "react";
import {
    Pagination,
    PaginationContent,
    PaginationItem,
    PaginationLink,
    PaginationNext,
    PaginationPrevious,
} from "@/components/ui/pagination";

interface LibraryPaginationProps {
    currentPage: number;
    totalPages: number;
}

export default function LibraryPagination({ currentPage, totalPages }: LibraryPaginationProps) {
    const router = useRouter();
    const pathname = usePathname();
    const searchParams = useSearchParams();
    const [isPending, startTransition] = useTransition();

    // Helper to generate the new URL string for a specific page
    const createPageURL = (pageNumber: number) => {
        const params = new URLSearchParams(searchParams.toString());
        params.set("page", pageNumber.toString());
        return `${pathname}?${params.toString()}`;
    };

    // Handle client-side navigation with a React Transition
    // This allows the UI to stay responsive while the server fetches new data
    const handleNavigation = (
        e: React.MouseEvent<HTMLAnchorElement>,
        pageNumber: number
    ) => {
        e.preventDefault();
        if (pageNumber === currentPage) return;

        startTransition(() => {
            router.push(createPageURL(pageNumber));
        });
    };

    if (totalPages <= 1) return null;

    // Generate an array of page numbers to show
    // (e.g., if on page 5: show 3, 4, [5], 6, 7)
    const getVisiblePages = () => {
        const MAX_VISIBLE = 5;
        let start = Math.max(1, currentPage - Math.floor(MAX_VISIBLE / 2));
        const end = Math.min(totalPages, start + MAX_VISIBLE - 1);

        // Adjust start if we're near the end to always show MAX_VISIBLE buttons
        if (end - start + 1 < MAX_VISIBLE) {
            start = Math.max(1, end - MAX_VISIBLE + 1);
        }

        const pages = [];
        for (let i = start; i <= end; i++) {
            pages.push(i);
        }
        return pages;
    };

    const visiblePages = getVisiblePages();

    return (
        <div className={`mt-8 ${isPending ? "opacity-50 pointer-events-none" : ""}`}>
            <Pagination>
                <PaginationContent>
                    <PaginationItem>
                        <PaginationPrevious
                            href={createPageURL(currentPage - 1)}
                            onClick={(e) => handleNavigation(e, currentPage - 1)}
                            className={currentPage <= 1 ? "pointer-events-none opacity-50" : "cursor-pointer"}
                            // Prevent screenreader from reading it if disabled
                            aria-disabled={currentPage <= 1}
                            tabIndex={currentPage <= 1 ? -1 : undefined}
                        />
                    </PaginationItem>

                    {visiblePages.map((page) => (
                        <PaginationItem key={page}>
                            <PaginationLink
                                href={createPageURL(page)}
                                onClick={(e) => handleNavigation(e, page)}
                                isActive={page === currentPage}
                                className="cursor-pointer"
                            >
                                {page}
                            </PaginationLink>
                        </PaginationItem>
                    ))}

                    <PaginationItem>
                        <PaginationNext
                            href={createPageURL(currentPage + 1)}
                            onClick={(e) => handleNavigation(e, currentPage + 1)}
                            className={currentPage >= totalPages ? "pointer-events-none opacity-50" : "cursor-pointer"}
                            aria-disabled={currentPage >= totalPages}
                            tabIndex={currentPage >= totalPages ? -1 : undefined}
                        />
                    </PaginationItem>
                </PaginationContent>
            </Pagination>
        </div>
    );
}
