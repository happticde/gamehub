/** Secret-free GameHub session-family shape returned by Core. */
export interface AccountSession {
    sessionId: string;
    createdAtUtc: string;
    isCurrent: boolean;
}

const sessionKeys = new Set(["sessionId", "createdAtUtc", "isCurrent"]);
const guidPattern =
    /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

/**
 * Validates the deliberately minimal Core response before it reaches component state.
 * Rejecting extra keys prevents accidental future exposure of provider or request metadata.
 */
export function parseAccountSessions(value: unknown): AccountSession[] {
    if (!Array.isArray(value) || value.length > 1000) {
        throw new Error("invalid_account_sessions");
    }

    const ids = new Set<string>();
    const sessions = value.map((candidate) => {
        if (!candidate || typeof candidate !== "object" || Array.isArray(candidate)) {
            throw new Error("invalid_account_sessions");
        }

        const record = candidate as Record<string, unknown>;
        if (Object.keys(record).some((key) => !sessionKeys.has(key))
            || typeof record.sessionId !== "string"
            || !guidPattern.test(record.sessionId)
            || ids.has(record.sessionId)
            || typeof record.createdAtUtc !== "string"
            || !Number.isFinite(Date.parse(record.createdAtUtc))
            || typeof record.isCurrent !== "boolean") {
            throw new Error("invalid_account_sessions");
        }

        ids.add(record.sessionId);
        return {
            sessionId: record.sessionId,
            createdAtUtc: record.createdAtUtc,
            isCurrent: record.isCurrent,
        };
    });

    if (sessions.filter((session) => session.isCurrent).length !== 1) {
        throw new Error("invalid_account_sessions");
    }

    return sessions.sort((left, right) =>
        Number(right.isCurrent) - Number(left.isCurrent)
        || Date.parse(right.createdAtUtc) - Date.parse(left.createdAtUtc));
}

/** Removes a successfully revoked non-current family from local UI state. */
export function removeRevokedSession(
    sessions: AccountSession[],
    revokedSessionId: string,
): AccountSession[] {
    return sessions.filter((session) => session.sessionId !== revokedSessionId);
}

/** Keeps only the current family after Core revokes all other sessions. */
export function keepCurrentSession(sessions: AccountSession[]): AccountSession[] {
    return sessions.filter((session) => session.isCurrent);
}

/** Formats the server-supplied UTC creation time in the browser's locale and time zone. */
export function formatSessionCreatedAt(createdAtUtc: string): string {
    return new Intl.DateTimeFormat(undefined, {
        dateStyle: "medium",
        timeStyle: "short",
    }).format(new Date(createdAtUtc));
}
