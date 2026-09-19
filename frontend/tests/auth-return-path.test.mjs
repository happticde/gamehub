import assert from "node:assert/strict";
import test from "node:test";
import { safeReturnPath } from "../lib/auth-return-path.ts";

test("login return paths reject external URL and parser-normalization bypasses", () => {
    const unsafePaths = [
        null,
        undefined,
        "",
        "https://outside.example",
        "javascript:alert(1)",
        "//outside.example",
        "/\\outside.example",
        "/\t/outside.example",
        "/\r/outside.example",
        "/\n/outside.example",
        "/library\u0000",
        "/library\u007f",
        "/library/..//outside.example",
        "/login",
        "/library/../login?returnTo=/admin",
        "/%2e/login",
    ];

    for (const path of unsafePaths) {
        assert.equal(safeReturnPath(path), "/library", JSON.stringify(path));
        assert.equal(
            new URL(safeReturnPath(path), "https://gamehub.example").origin,
            "https://gamehub.example",
        );
    }
});

test("login return paths retain local destinations, query parameters, and fragments", () => {
    for (const path of [
        "/",
        "/library/42",
        "/library?search=Space%20Quest&page=2",
        "/admin?tab=security#sessions",
        "/library?search=https%3A%2F%2Fexample.test",
    ]) {
        assert.equal(safeReturnPath(path), path);
    }
    assert.equal(safeReturnPath("/library/../admin"), "/admin");
});
