import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { parseIgdbProviderId } from "../lib/igdb-provider-id.ts";

test("direct IGDB IDs accept only positive Core-compatible integers", () => {
    assert.equal(parseIgdbProviderId("1942"), 1942);
    assert.equal(parseIgdbProviderId(" 1942 "), 1942);
    assert.equal(parseIgdbProviderId("0"), null);
    assert.equal(parseIgdbProviderId("-1"), null);
    assert.equal(parseIgdbProviderId("1.5"), null);
    assert.equal(parseIgdbProviderId("2147483648"), null);
    assert.equal(parseIgdbProviderId("not-an-id"), null);
});

test("Fix Match keeps direct ID entry separate from title search", async () => {
    const component = await readFile(
        new URL("../components/library/FixMatchDialog.tsx", import.meta.url),
        "utf8",
    );

    assert.match(component, /htmlFor="direct-igdb-id"/);
    assert.match(component, /parseIgdbProviderId\(directProviderId\)/);
    assert.match(component, /Core verifies the exact ID before changing any metadata/);
    assert.match(component, /library\/\$\{gameId\}\/enrichment\/override/);
    assert.match(component, /Search by title/);
    assert.doesNotMatch(component, /title or IGDB ID/i);
});
