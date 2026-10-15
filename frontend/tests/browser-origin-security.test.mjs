import assert from "node:assert/strict";
import test from "node:test";
import { trustedBrowserOrigin, isTrustedBrowserMutation } from "../lib/browser-origin-security.ts";

test("configured public origin is canonical and cannot contain credentials, paths, or fragments", () => {
    assert.equal(trustedBrowserOrigin("https://gamehub.example/"), "https://gamehub.example");
    assert.equal(trustedBrowserOrigin("http://localhost:3000"), "http://localhost:3000");
    for (const value of [undefined,"https://user:password@gamehub.example","https://gamehub.example/path","https://gamehub.example/#hash","http://gamehub.example","file:///tmp/test"]) {
        assert.throws(() => trustedBrowserOrigin(value));
    }
});

test("sibling origins, null and missing origins cannot use same-site cookies to authorize a write", () => {
    for (const origin of [undefined,"null","https://sibling.gamehub.example","https://gamehub.example.attacker.invalid","http://gamehub.example"]) {
        const headers = new Headers({ "sec-fetch-site":"same-site" });
        if(origin !== undefined) headers.set("origin",origin);
        assert.equal(isTrustedBrowserMutation(headers,"https://gamehub.example"),false);
    }
});

test("same-origin forms and fetch work while forged forwarding metadata grants no trust", () => {
    assert.equal(isTrustedBrowserMutation(new Headers({origin:"https://gamehub.example","sec-fetch-site":"same-origin"}),"https://gamehub.example"),true);
    assert.equal(isTrustedBrowserMutation(new Headers({origin:"https://gamehub.example"}),"https://gamehub.example"),true);
    assert.equal(isTrustedBrowserMutation(new Headers({origin:"https://gamehub.example","sec-fetch-site":"cross-site"}),"https://gamehub.example"),false);
    assert.equal(isTrustedBrowserMutation(new Headers({origin:"https://attacker.invalid",host:"attacker.invalid","x-forwarded-host":"attacker.invalid"}),"https://gamehub.example"),false);
});
