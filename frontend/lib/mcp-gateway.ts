import "server-only";
import { getManagedMcpConfig, managedMcpHeaders } from "@/lib/mcp-managed-config.mjs";

/** Only delivered protocol endpoints. Remote transport/discovery/authorization are added by #76–#78. */
export function isMcpGatewayRoute(path: string, method: string): boolean {
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
        const authorization = request.headers.get("authorization");
        if (library && (!authorization || !/^Bearer [!-~]+$/.test(authorization))) return new Response(null, { status: 401 });
        if (!library && url.search) return new Response(null, { status: 400 });
        const headers = new Headers(managedMcpHeaders(config));
        // Allowlist instead of deleting known dangerous headers: cookies, tenant selectors,
        // browser credentials, forwarding and supplied service keys can never enter Core.
        for (const name of ["accept", "content-type", ...(library ? ["authorization"] : [])]) {
            const value = request.headers.get(name);
            if (value) headers.set(name, value);
        }
        const destination = new URL(url.pathname + (library ? url.search : ""), config.core);
        const init: RequestInit & { duplex: "half" } = {
            method: request.method, headers, body: request.body, redirect: "manual", cache: "no-store",
            signal: AbortSignal.any([request.signal, AbortSignal.timeout(15_000)]),
            // Node's native fetch requires duplex for an incoming streamed body.
            duplex: "half",
        };
        const upstream = await fetch(destination, init);
        if (upstream.status >= 300 && upstream.status < 400) {
            await upstream.body?.cancel();
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
