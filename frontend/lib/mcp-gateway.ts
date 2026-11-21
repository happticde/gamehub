import "server-only";
import { getManagedMcpConfig, managedMcpHeaders } from "@/lib/mcp-managed-config.mjs";
import { parseMcpRemoteStart } from "@/lib/mcp-remote-contract";

/** Only exact protocol routes exposed by this gateway. The public edge keeps /mcp private. */
export function isMcpGatewayRoute(path: string, method: string): boolean {
    if (path === "/mcp") return ["GET", "POST", "DELETE"].includes(method);
    if (path === "/.well-known/oauth-protected-resource/mcp" || path === "/.well-known/oauth-authorization-server") {
        return method === "GET";
    }
    if (path === "/connect/authorize") return method === "GET";
    if (["/connect/device", "/connect/token", "/connect/revoke"].includes(path)) return method === "POST";
    if (method === "GET") return /^\/api\/library(?:\/(?:[1-9][0-9]*|random|stats|tags))?$/.test(path);
    return method === "PATCH" && /^\/api\/library\/[1-9][0-9]*\/personal$/.test(path);
}

/** Fixed destinations, no cookies/operator identity, no retries/redirects; streams preserve cancellation. */
export async function forwardMcpRequest(request: Request): Promise<Response> {
    const url = new URL(request.url);
    if (!isMcpGatewayRoute(url.pathname, request.method)) return new Response(null, { status: 404 });
    try {
        const config = getManagedMcpConfig();
        if (!config) return new Response(null, { status: 404 });
        // Native Node fetch owns its upstream Host. Validate the incoming public authority here;
        // Core restores that authority only after checking the generated transport proof.
        if (request.headers.get("host")?.toLowerCase() !== new URL(config.instance).host.toLowerCase()
            || (request.headers.has("origin") && request.headers.get("origin") !== config.instance)) {
            return new Response(null, { status: 403 });
        }
        const library = url.pathname.startsWith("/api/library");
        const remote = url.pathname === "/mcp";
        const authorize = url.pathname === "/connect/authorize";
        const authorization = request.headers.get("authorization");
        if (library && (!authorization || !/^Bearer [!-~]+$/.test(authorization))) return new Response(null, { status: 401 });
        const forwardAuthorization = library || (remote && /^Bearer(?: |$)/i.test(authorization ?? ""));
        if (!library && !authorize && url.search) return new Response(null, { status: 400 });
        if (authorize && url.search.length > 4096) return new Response(null, { status: 400 });
        const headers = new Headers(managedMcpHeaders(config));
        // Allowlist instead of deleting known dangerous headers: cookies, tenant selectors,
        // browser credentials, forwarding and supplied service keys can never enter Core.
        for (const name of ["accept", "content-type", ...(forwardAuthorization ? ["authorization"] : []),
            ...(remote ? ["origin", "mcp-protocol-version", "mcp-method", "mcp-name"] : [])]) {
            const value = request.headers.get(name);
            if (value) headers.set(name, value);
        }
        const destination = new URL(url.pathname + (library || authorize ? url.search : ""), config.core);
        const init: RequestInit & { duplex: "half" } = {
            method: request.method, headers, body: request.body, redirect: "manual", cache: "no-store",
            // An upstream 401 must reach the client without Fetch trying to replay the streamed body.
            credentials: "omit",
            signal: AbortSignal.any([request.signal, AbortSignal.timeout(15_000)]),
            // Node's native fetch requires duplex for an incoming streamed body.
            duplex: "half",
        };
        const upstream = await fetch(destination, init);
        if (upstream.status >= 300 && upstream.status < 400) {
            await upstream.body?.cancel();
            if (authorize && upstream.status === 302) {
                const location = parseMcpRemoteStart(upstream.headers.get("location") ?? "", config.instance);
                return new Response(null, { status: 302, headers: {
                    Location: location.href, "Cache-Control": "no-store", "Referrer-Policy": "no-referrer",
                } });
            }
            return new Response(null, { status: 502 });
        }
        const outgoing = new Headers({ "Cache-Control": "no-store", Pragma: "no-cache" });
        for (const name of ["content-type", "www-authenticate", "retry-after"]) {
            const value = upstream.headers.get(name);
            if (value) outgoing.set(name, value);
        }
        return new Response(upstream.body, { status: upstream.status, headers: outgoing });
    } catch { return new Response(JSON.stringify({ error: "temporarily_unavailable" }), {
        status: 503, headers: { "Content-Type": "application/json", "Cache-Control": "no-store" },
    }); }
}
