/** Resolve a server-owned origin without trusting Host or forwarding headers. */
export function trustedBrowserOrigin(configured: string | undefined): string {
    if (!configured) throw new Error("missing_public_origin");
    const url = new URL(configured);
    if (url.username || url.password || url.pathname !== "/" || url.search || url.hash
        || (url.protocol !== "https:" && url.protocol !== "http:")
        || (url.protocol === "http:" && !["localhost", "127.0.0.1", "[::1]"].includes(url.hostname))) {
        throw new Error("invalid_public_origin");
    }
    return url.origin;
}

/** SameSite cookies do not separate a trusted application from sibling subdomains. */
export function isTrustedBrowserMutation(headers: Pick<Headers, "get">, expectedOrigin: string): boolean {
    const origin = headers.get("origin");
    const fetchSite = headers.get("sec-fetch-site");
    return origin === expectedOrigin
        && (fetchSite === null || fetchSite === "same-origin" || fetchSite === "none");
}
