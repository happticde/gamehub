const defaultCoreUrl = "http://host.docker.internal:5001";

/** Builds an internal Core API URL for server-side requests. */
export function getCoreApiUrl(path: string): string {
    const coreUrl = process.env.CORE_URL || defaultCoreUrl;
    const normalizedPath = path.startsWith("/") ? path : `/${path}`;

    return `${coreUrl.replace(/\/$/, "")}/api${normalizedPath}`;
}
