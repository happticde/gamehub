import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { createLatestRequestGate } from "../lib/latest-request-gate.ts";

test("a stale search completion cannot replace the newest search state", async () => {
    const gate = createLatestRequestGate();
    let visibleResult = "";

    const settle = async (generation: number, value: string, delay: number) => {
        await new Promise((resolve) => setTimeout(resolve, delay));
        if (gate.isLatest(generation)) visibleResult = value;
    };

    const older = gate.begin();
    const olderCompletion = settle(older, "older response", 20);
    const newer = gate.begin();
    const newerCompletion = settle(newer, "newer response", 0);
    await Promise.all([olderCompletion, newerCompletion]);

    assert.equal(visibleResult, "newer response");
});

test("physical capture uses the shared debounced IGDB search with safe result states", async () => {
    const component = await readFile(
        new URL("../components/physical/PhysicalGameCapture.tsx", import.meta.url),
        "utf8",
    );
    const searchHook = await readFile(
        new URL("../hooks/useDebouncedMetadataSearch.ts", import.meta.url),
        "utf8",
    );
    const resultCard = await readFile(
        new URL("../components/library/MetadataSearchResultCard.tsx", import.meta.url),
        "utf8",
    );

    assert.match(searchHook, /library\/enrichment\/search/);
    assert.match(searchHook, /setTimeout[\s\S]*350/);
    assert.match(searchHook, /AbortController/);
    assert.match(searchHook, /isLatest\(generation\)/);
    assert.match(component, /search\.pending/);
    assert.match(component, /No IGDB results found/);
    assert.match(component, /search\.error/);
    assert.match(resultCard, /result\.coverUrl/);
    assert.match(resultCard, /getFullYear/);
});

test("shared IGDB result cards keep their action inside narrow mobile layouts", async () => {
    const resultCard = await readFile(
        new URL("../components/library/MetadataSearchResultCard.tsx", import.meta.url),
        "utf8",
    );

    assert.match(resultCard, /grid-cols-\[4rem_minmax\(0,1fr\)\]/);
    assert.match(resultCard, /sm:grid-cols-\[4rem_minmax\(0,1fr\)_auto\]/);
    assert.match(resultCard, /col-span-2[^"]*sm:col-span-1/);
    assert.match(resultCard, /w-full sm:w-auto/);
});

test("selection, platform choice, and final confirmation are all explicit", async () => {
    const component = await readFile(
        new URL("../components/physical/PhysicalGameCapture.tsx", import.meta.url),
        "utf8",
    );

    assert.match(component, /useState<MetadataSearchResult \| null>\(null\)/);
    assert.match(component, /onAction=\{\(\) => selectResult\(result\)\}/);
    assert.match(component, /useState<PhysicalGamePlatform \| null>\(null\)/);
    assert.match(component, /onValueChange=\{\(value\) => setPlatform/);
    assert.doesNotMatch(component, /setPlatform\([^)]*platforms/);
    assert.match(component, /step !== "confirm" \|\| !selectedResult \|\| !platform/);
    assert.match(component, /metadataProviderId: selectedResult\.providerId/);
    assert.match(component, /confirmed: true/);
    assert.match(component, /Confirm and add physical game/);
});

test("back and cancel remain write-free and barcode catalogue UI is absent", async () => {
    const component = await readFile(
        new URL("../components/physical/PhysicalGameCapture.tsx", import.meta.url),
        "utf8",
    );

    assert.equal(component.match(/method: "POST"/g)?.length, 1);
    assert.match(component, /Back to search/);
    assert.match(component, /Back to platform/);
    assert.match(component, />Cancel</);
    assert.doesNotMatch(component, /barcode|gtin|product catalogue|external catalogue|CameraBarcodeScanner/i);
    assert.doesNotMatch(component, /@zxing/i);
});
