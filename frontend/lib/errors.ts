/**
 * Returns a safe message for values caught from promises or other fallible operations.
 * JavaScript permits throwing any value, so callers should keep caught values as unknown.
 */
export function getErrorMessage(error: unknown, fallback: string): string {
    return error instanceof Error && error.message ? error.message : fallback;
}
