import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import {
    keepCurrentSession,
    parseAccountSessions,
    removeRevokedSession,
} from "../lib/account-sessions.ts";

const currentId = "11111111-1111-4111-8111-111111111111";
const otherId = "22222222-2222-4222-8222-222222222222";

test("session payload accepts only the minimal secret-free shape and marks one current session", () => {
    const sessions = parseAccountSessions([
        {
            sessionId: otherId,
            createdAtUtc: "2026-07-30T11:00:00Z",
            isCurrent: false,
        },
        {
            sessionId: currentId,
            createdAtUtc: "2026-07-30T10:00:00Z",
            isCurrent: true,
        },
    ]);

    assert.equal(sessions[0].sessionId, currentId);
    assert.equal(sessions[0].isCurrent, true);
    assert.deepEqual(
        Object.keys(sessions[0]).sort(),
        ["createdAtUtc", "isCurrent", "sessionId"],
    );

    assert.throws(() => parseAccountSessions([{
        sessionId: currentId,
        createdAtUtc: "2026-07-30T10:00:00Z",
        isCurrent: true,
        userAgent: "must-not-enter-state",
    }]), /invalid_account_sessions/);
    assert.throws(() => parseAccountSessions([]), /invalid_account_sessions/);
});

test("successful interaction state removes one target or every non-current session", () => {
    const sessions = parseAccountSessions([
        {
            sessionId: currentId,
            createdAtUtc: "2026-07-30T10:00:00Z",
            isCurrent: true,
        },
        {
            sessionId: otherId,
            createdAtUtc: "2026-07-30T11:00:00Z",
            isCurrent: false,
        },
    ]);

    assert.deepEqual(
        removeRevokedSession(sessions, otherId).map((session) => session.sessionId),
        [
            currentId,
        ],
    );
    assert.deepEqual(
        keepCurrentSession(sessions).map((session) => session.sessionId),
        [currentId],
    );
});

test("session panel uses only same-origin BFF routes and the existing current logout flow", async () => {
    const source = await readFile(
        new URL("../components/admin/ActiveSessionsPanel.tsx", import.meta.url),
        "utf8",
    );

    assert.match(source, /fetchAuthenticated\("\/api\/core\/auth\/sessions"/);
    assert.match(source, /\/api\/core\/auth\/sessions\/others/);
    assert.match(source, /encodeURIComponent\(sessionId\)/);
    assert.doesNotMatch(source, /\bfetch\s*\(\s*["'`]\/api\/core/);
    assert.match(source, /<form action="\/api\/auth\/logout" method="post">/);
    assert.match(source, /role="status"/);
    assert.match(source, /role="alert"/);
    assert.doesNotMatch(source, /localStorage|userAgent|ipAddress|keycloak/i);
});
