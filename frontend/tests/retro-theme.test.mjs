import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const readFrontendFile = (path) => readFile(new URL(`../${path}`, import.meta.url), "utf8");

test("retro effects stay behind the application and use readable tokens", async () => {
    const [styles, layout] = await Promise.all([
        readFrontendFile("app/globals.css"),
        readFrontendFile("app/layout.tsx"),
    ]);

    assert.doesNotMatch(styles, /\.retro-terminal::before/);
    assert.match(styles, /--muted-foreground: oklch\(0\.68 0\.10 140\)/);
    assert.match(styles, /--border: oklch\(0\.55 0\.14 140 \/ 70%\)/);
    assert.match(styles, /retro-terminal-vignette[\s\S]*?z-index: 0/);
    assert.match(layout, /className="relative z-10 flex min-h-screen flex-col"/);
});

test("retro overrides are scoped to explicit component contracts", async () => {
    const styles = await readFrontendFile("app/globals.css");

    assert.doesNotMatch(styles, /\.retro-terminal \.group/);
    assert.doesNotMatch(styles, /\.retro-terminal \.rounded-xl\.border/);
    assert.doesNotMatch(styles, /div\[class\*="bg-popover"\]/);
    assert.match(styles, /\[data-retro-search-results\]/);
});

test("responsive visibility regressions remain fixed", async () => {
    const [compactCard, stats, landing, library] = await Promise.all([
        readFrontendFile("components/GameCardCompact.tsx"),
        readFrontendFile("app/stats/page.tsx"),
        readFrontendFile("app/page.tsx"),
        readFrontendFile("app/library/page.tsx"),
    ]);

    assert.doesNotMatch(compactCard, /group-hover:opacity-100/);
    assert.match(compactCard, /focus-visible:ring-2/);
    assert.match(stats, /<Card className="min-w-0 overflow-hidden">/);
    assert.match(landing, /md:text-\[0\.65rem\] lg:text-xs xl:text-sm/);
    assert.match(library, /md:sticky md:bottom-6/);
    assert.doesNotMatch(library, /className="sticky bottom-6/);
});
