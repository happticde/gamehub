/** Accepts only application-local destinations, including after URL normalization. */
export function safeReturnPath(value: string | null | undefined): string {
    const fallback = "/library";
    if (!value?.startsWith("/") || value.startsWith("//") || value.includes("\\")) {
        return fallback;
    }

    // URL parsers remove tabs/newlines and treat backslashes as separators. Reject them before
    // parsing so an apparently local path cannot become a protocol-relative external URL.
    if (Array.from(value).some((character) => {
        const code = character.charCodeAt(0);
        return code < 32 || code === 127;
    })) {
        return fallback;
    }

    const localOrigin = "https://gamehub.invalid";
    const destination = new URL(value, localOrigin);
    if (destination.origin !== localOrigin
        || destination.pathname.startsWith("//")
        || destination.pathname.startsWith("/login")) {
        return fallback;
    }

    return `${destination.pathname}${destination.search}${destination.hash}`;
}
