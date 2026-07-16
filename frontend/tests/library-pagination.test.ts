import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import {
    DEFAULT_LIBRARY_PAGE_SIZE,
    isLibraryPageParamValid,
    isLibraryPageSizeParamValid,
    parseLibraryPage,
    parseLibraryPageSize,
    toUrlSearchParams,
    updateLibraryPaginationParams,
} from "../lib/library-pagination.ts";

test("library pagination accepts only the visible page-size choices", () => {
    for (const value of ["25", "50", "100"]) {
        assert.equal(isLibraryPageSizeParamValid(value), true);
        assert.equal(parseLibraryPageSize(value), Number(value));
    }

    for (const value of ["0", "30", "500", "050", "invalid", ["25", "50"]]) {
        assert.equal(isLibraryPageSizeParamValid(value), false);
        assert.equal(parseLibraryPageSize(value), DEFAULT_LIBRARY_PAGE_SIZE);
    }
});

test("library page parsing rejects malformed and out-of-range Core integers", () => {
    assert.equal(parseLibraryPage("12"), 12);
    assert.equal(isLibraryPageParamValid("12"), true);

    for (const value of ["0", "-1", "1.5", "01", "2147483648", "invalid", ["1", "2"]]) {
        assert.equal(isLibraryPageParamValid(value), false);
        assert.equal(parseLibraryPage(value), 1);
    }
});

test("changing page size preserves filters and sorting while resetting page one", () => {
    const source = toUrlSearchParams({
        search: "witcher",
        tags: ["RPG", "Fantasy"],
        sortBy: "rating",
        sortOrder: "desc",
        page: "4",
        pageSize: "50",
    });

    const updated = updateLibraryPaginationParams(source, 1, 25);

    assert.equal(updated.get("search"), "witcher");
    assert.deepEqual(updated.getAll("tags"), ["RPG", "Fantasy"]);
    assert.equal(updated.get("sortBy"), "rating");
    assert.equal(updated.get("sortOrder"), "desc");
    assert.equal(updated.get("pageSize"), "25");
    assert.equal(updated.has("page"), false);
});

test("library page wires the selected size through Core and the toolbar", async () => {
    const page = await readFile(
        new URL("../app/library/page.tsx", import.meta.url),
        "utf8",
    );

    assert.match(page, /query\.append\("pageSize", requestedPageSize\.toString\(\)\)/);
    assert.match(page, /pageSizeControl={<LibraryPageSizeSelect pageSize={pageSize} \/>}/);
    assert.match(page, /effectivePage !== currentPage/);
});
