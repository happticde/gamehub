/** Minimal request shape needed to reconstruct the browser-visible URL behind a reverse proxy. */
export interface ExternalRequestUrlInput {
    headers: Pick<Headers, "get">;
    nextUrl: Pick<URL, "pathname" | "search">;
    url: string;
}

/**
 * Reconstructs the browser-visible request URL from reverse-proxy headers.
 *
 * Next.js standalone builds `request.url` from its bind address (`0.0.0.0:3000`) instead of the
 * public authority. Caddy preserves the original Host header and supplies X-Forwarded-Proto, so
 * those values are the deployment boundary for redirects and exact OIDC callback validation.
 * Malformed or chained values fail closed instead of being reflected into a Location header.
 */
export function getExternalRequestUrl(request: ExternalRequestUrlInput): URL {
    const internalUrl = new URL(request.url);
    const host = requireSingleHeaderValue("host", request.headers.get("host"));
    const forwardedProtocol = optionalSingleHeaderValue(
        "x-forwarded-proto",
        request.headers.get("x-forwarded-proto"),
    );
    const protocol = (forwardedProtocol ?? internalUrl.protocol.slice(0, -1)).toLowerCase();
    if (protocol !== "http" && protocol !== "https") {
        throw new Error("invalid_external_request_protocol");
    }

    const origin = new URL(`${protocol}://${host}`);
    if (origin.username || origin.password || origin.pathname !== "/" || origin.search || origin.hash) {
        throw new Error("invalid_external_request_host");
    }

    origin.pathname = request.nextUrl.pathname;
    origin.search = request.nextUrl.search;
    return origin;
}

function requireSingleHeaderValue(name: string, value: string | null): string {
    const parsed = optionalSingleHeaderValue(name, value);
    if (!parsed) throw new Error(`missing_${name.replaceAll("-", "_")}`);
    return parsed;
}

function optionalSingleHeaderValue(name: string, value: string | null): string | undefined {
    if (value === null) return undefined;
    const trimmed = value.trim();
    if (!trimmed || trimmed.includes(",") || /[\r\n]/.test(trimmed)) {
        throw new Error(`invalid_${name.replaceAll("-", "_")}`);
    }
    return trimmed;
}
