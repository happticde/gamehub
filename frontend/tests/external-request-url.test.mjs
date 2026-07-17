import assert from "node:assert/strict";
import test from "node:test";
import { getExternalRequestUrl } from "../lib/external-request-url.ts";
import { createOidcTransaction, validateOidcCallback } from "../lib/oidc-security.ts";

const publicCallback = "https://gamehub.example.test/api/auth/oidc/callback";

function proxiedRequest({
    host = "gamehub.example.test",
    forwardedProtocol = "https",
    path = "/api/auth/oidc/callback?code=synthetic&state=synthetic",
} = {}) {
    const internalUrl = new URL(path, "https://0.0.0.0:3000");
    const headers = new Headers({ host });
    if (forwardedProtocol !== null) {
        headers.set("x-forwarded-proto", forwardedProtocol);
    }
    return { headers, nextUrl: internalUrl, url: internalUrl.toString() };
}

test("reconstructs the public HTTPS URL instead of the standalone bind address", () => {
    const result = getExternalRequestUrl(proxiedRequest());

    assert.equal(
        result.toString(),
        `${publicCallback}?code=synthetic&state=synthetic`,
    );
});

test("reconstructed callback preserves exact redirect validation", () => {
    const transaction = createOidcTransaction(publicCallback, "/library", "login");
    const result = getExternalRequestUrl(proxiedRequest({
        path: `/api/auth/oidc/callback?state=${transaction.state}`,
    }));
    result.search = "";

    assert.doesNotThrow(() => validateOidcCallback(
        transaction,
        transaction.state,
        result.toString(),
    ));
});

test("a different public host remains an invalid OIDC callback", () => {
    const transaction = createOidcTransaction(publicCallback, "/library", "login");
    const result = getExternalRequestUrl(proxiedRequest({ host: "attacker.example.test" }));
    result.search = "";

    assert.throws(
        () => validateOidcCallback(transaction, transaction.state, result.toString()),
        /invalid_oidc_redirect/,
    );
});

test("development requests fall back to the internal HTTP protocol but preserve Host", () => {
    const request = proxiedRequest({
        host: "localhost:3005",
        forwardedProtocol: null,
        path: "/login",
    });
    request.url = "http://0.0.0.0:3005/login";
    request.nextUrl = new URL(request.url);

    assert.equal(getExternalRequestUrl(request).toString(), "http://localhost:3005/login");
});

test("malformed or chained proxy authority headers fail closed", () => {
    assert.throws(
        () => getExternalRequestUrl(proxiedRequest({ forwardedProtocol: "https, http" })),
        /invalid_x_forwarded_proto/,
    );
    assert.throws(
        () => getExternalRequestUrl(proxiedRequest({ host: "gamehub.example.test, attacker.test" })),
        /invalid_host/,
    );
});
