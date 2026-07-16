export const LIBRARY_PAGE_SIZES = [25, 50, 100] as const;
export const DEFAULT_LIBRARY_PAGE_SIZE = 50;

export type LibraryPageSize = (typeof LIBRARY_PAGE_SIZES)[number];
export type LibrarySearchParam = string | string[] | undefined;

const MAX_CORE_PAGE = 2_147_483_647;

export function isLibraryPageSizeParamValid(value: LibrarySearchParam): boolean {
    if (value === undefined) return true;
    if (typeof value !== "string" || !/^\d+$/.test(value)) return false;

    const parsed = Number(value);
    return parsed.toString() === value
        && LIBRARY_PAGE_SIZES.some((pageSize) => pageSize === parsed);
}

export function parseLibraryPageSize(value: LibrarySearchParam): LibraryPageSize {
    if (!isLibraryPageSizeParamValid(value) || value === undefined) {
        return DEFAULT_LIBRARY_PAGE_SIZE;
    }

    return Number(value) as LibraryPageSize;
}

export function isLibraryPageParamValid(value: LibrarySearchParam): boolean {
    if (value === undefined) return true;
    if (typeof value !== "string" || !/^[1-9]\d*$/.test(value)) return false;

    const parsed = Number(value);
    return Number.isSafeInteger(parsed) && parsed <= MAX_CORE_PAGE;
}

export function parseLibraryPage(value: LibrarySearchParam): number {
    if (!isLibraryPageParamValid(value) || value === undefined) return 1;
    return Number(value);
}

export function toUrlSearchParams(
    source: Record<string, LibrarySearchParam>,
): URLSearchParams {
    const params = new URLSearchParams();

    for (const [key, value] of Object.entries(source)) {
        if (Array.isArray(value)) {
            value.forEach((item) => params.append(key, item));
        } else if (value !== undefined) {
            params.set(key, value);
        }
    }

    return params;
}

/**
 * Produces canonical pagination state while preserving every active filter and sort parameter.
 * Page one is represented without a page parameter, while page size remains explicit.
 */
export function updateLibraryPaginationParams(
    source: URLSearchParams,
    page: number,
    pageSize: LibraryPageSize,
): URLSearchParams {
    const params = new URLSearchParams(source.toString());
    params.set("pageSize", pageSize.toString());

    if (page <= 1) params.delete("page");
    else params.set("page", page.toString());

    return params;
}
