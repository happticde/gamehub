import { constants, closeSync, fstatSync, lstatSync, openSync, readFileSync, readdirSync } from "node:fs";
import { dirname, isAbsolute, join, parse } from "node:path";

/** Read a bounded, private Linux file. Never cache private state across replacement or print failures. */
export function readManagedMcpState(directory) {
    try {
        if (process.platform !== "linux" || !isAbsolute(directory)) throw new Error();
        let path = directory;
        while (path !== parse(path).root) {
            if (lstatSync(path).isSymbolicLink()) throw new Error();
            path = dirname(path);
        }
        const folder = lstatSync(directory);
        if (!folder.isDirectory() || (folder.mode & 0o777) !== 0o700
            || folder.uid !== process.getuid()) throw new Error();
        if (readdirSync(directory).some(name => !["state.json", "operation.lock"].includes(name))) throw new Error();
        const descriptor = openSync(join(directory, "state.json"), constants.O_RDONLY | constants.O_NOFOLLOW);
        let state;
        try {
            const file = fstatSync(descriptor);
            if (!file.isFile() || file.uid !== process.getuid() || (file.mode & 0o777) !== 0o600 || file.size > 4096) throw new Error();
            state = JSON.parse(readFileSync(descriptor, "utf8"));
        } finally { closeSync(descriptor); }
        const origin = new URL(state.PublicOrigin);
        if (state.Version !== 1 || state.Enabled !== true || !/^[a-f0-9]{32}$/.test(state.Generation)
            || [state.Generation, state.PublicOrigin, state.ConsentKey, state.TransportKey].some(value => typeof value !== "string")
            || origin.protocol !== "https:" || origin.origin !== state.PublicOrigin
            || !/^[!-~]{32,512}$/.test(state.ConsentKey) || !/^[!-~]{32,512}$/.test(state.TransportKey)
            || state.ConsentKey === state.TransportKey
            || Object.keys(state).sort().join(",") !== "ConsentKey,Enabled,Generation,PublicOrigin,TransportKey,Version") throw new Error();
        return state;
    } catch { throw new Error("managed_storage_invalid"); }
}

/** Managed settings use existing provider/origin/Core configuration; manual MCP settings cannot overlap. */
export function getManagedMcpConfig(env = process.env) {
    if (env.MCP_MANAGED_ENABLED !== undefined && !["true", "false"].includes(env.MCP_MANAGED_ENABLED)) throw new Error("invalid_enablement");
    if (env.MCP_MANAGED_ENABLED !== "true") return null;
    if (["MCP_DEVICE_ENABLED", "MCP_ISSUER", "MCP_BFF_SERVICE_KEY", "MCP_CORE_ORIGIN"].some(key => env[key] !== undefined)) {
        throw new Error("configuration_conflict");
    }
    const state = readManagedMcpState(env.MCP_MANAGED_DIRECTORY ?? "/var/lib/gamehub-mcp-frontend");
    let destination, callback, provider;
    try { destination = new URL(env.CORE_URL ?? "http://core:8080"); }
    catch { throw new Error("invalid_core_destination"); }
    try { callback = new URL(env.OIDC_REDIRECT_URI ?? ""); provider = new URL(env.OIDC_ISSUER ?? ""); }
    catch { throw new Error("provider_prerequisites"); }
    if (env.AUTH_ENABLED !== "true" || env.AUTH_COOKIE_SECURE !== "true" || env.OIDC_ENABLED !== "true") throw new Error("provider_prerequisites");
    const cookieKey = Buffer.from(env.OIDC_COOKIE_KEY ?? "", "base64");
    if (!env.OIDC_CLIENT_ID?.trim() || cookieKey.length !== 32 || cookieKey.toString("base64") !== env.OIDC_COOKIE_KEY
        || !/^[!-~]{32,512}$/.test(env.OIDC_BFF_SERVICE_KEY ?? "")
        || !(env.OIDC_SCOPES ?? "openid profile email").split(/\s+/).includes("openid")
        || provider.protocol !== "https:" || provider.username || provider.password || provider.hash
        || callback.protocol !== "https:" || callback.username || callback.password || callback.hash
        || ![undefined, "", "false", "true"].includes(env.MULTI_USER_ENABLED?.trim().toLowerCase())
        || ![undefined, "Disabled", "PersonalTenantJit"].includes(env.OIDC_PROVISIONING_MODE)) throw new Error("provider_prerequisites");
    if (env.PUBLIC_ORIGIN !== state.PublicOrigin || callback.origin !== state.PublicOrigin
        || provider.origin === state.PublicOrigin) throw new Error("origin_mismatch");
    if (!["http:", "https:"].includes(destination.protocol) || destination.username || destination.password
        || destination.pathname !== "/" || destination.search || destination.hash) throw new Error("invalid_core_destination");
    if ([env.OIDC_BFF_SERVICE_KEY, env.OIDC_COOKIE_KEY, env.OIDC_CLIENT_SECRET, env.CORE_API_KEY]
        .some(key => key?.trim() === state.TransportKey || key?.trim() === state.ConsentKey)) throw new Error("configuration_conflict");
    return { instance: state.PublicOrigin, key: state.ConsentKey, core: destination,
        transportKey: state.TransportKey, generation: state.Generation };
}

/** Fixed internal proof. Never add these headers to a caller-selected destination. */
export function managedMcpHeaders(config) {
    return { "X-GameHub-Mcp-Transport-Key": config.transportKey, "X-GameHub-Mcp-Generation": config.generation };
}
