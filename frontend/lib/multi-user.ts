import "server-only";

import {
    parseMultiUserMode,
    validateMultiUserPrerequisites,
} from "@/lib/multi-user-security";

/**
 * Reads the immutable operator switch from server configuration. The BFF never accepts this state
 * from a request, provider claim, cookie, or browser JavaScript.
 */
export function getFrontendMultiUserMode(): boolean {
    return parseMultiUserMode(process.env.MULTI_USER_ENABLED);
}

/**
 * Fails closed when the browser-facing authentication boundary cannot support the enabled mode.
 * Core independently validates mirrored values; this check protects BFF routes themselves.
 */
export function validateFrontendMultiUserConfiguration(): boolean {
    const enabled = getFrontendMultiUserMode();
    validateMultiUserPrerequisites(
        enabled,
        process.env.AUTH_ENABLED?.toLowerCase() === "true",
        process.env.AUTH_COOKIE_SECURE?.toLowerCase() === "true",
        process.env.OIDC_ENABLED?.toLowerCase() === "true",
    );
    return enabled;
}
