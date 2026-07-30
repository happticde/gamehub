"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { Loader2, LogOut, RefreshCw, ShieldCheck } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
} from "@/components/ui/card";
import {
    AccountSession,
    formatSessionCreatedAt,
    keepCurrentSession,
    parseAccountSessions,
    removeRevokedSession,
} from "@/lib/account-sessions";

const loadErrorMessage =
    "Die aktiven Sitzungen konnten nicht geladen werden. Bitte versuche es erneut.";
const revokeErrorMessage =
    "Die Sitzung konnte nicht sicher abgemeldet werden. Bitte versuche es erneut.";

/**
 * Tenant-facing session management. Every request stays on the existing same-origin Core BFF;
 * access and refresh credentials remain in HttpOnly cookies and never enter component state.
 */
export function ActiveSessionsPanel() {
    const [sessions, setSessions] = useState<AccountSession[]>([]);
    const [isLoading, setIsLoading] = useState(true);
    const [pendingAction, setPendingAction] = useState<string | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [success, setSuccess] = useState<string | null>(null);

    const loadSessions = useCallback(async () => {
        setIsLoading(true);
        setError(null);
        try {
            const response = await fetch("/api/core/auth/sessions", {
                cache: "no-store",
            });
            if (!response.ok) throw new Error("session_list_failed");
            setSessions(parseAccountSessions(await response.json()));
        } catch {
            setError(loadErrorMessage);
        } finally {
            setIsLoading(false);
        }
    }, []);

    useEffect(() => {
        void loadSessions();
    }, [loadSessions]);

    const otherSessionCount = useMemo(
        () => sessions.filter((session) => !session.isCurrent).length,
        [sessions],
    );

    const revokeSession = async (sessionId: string) => {
        setPendingAction(sessionId);
        setError(null);
        setSuccess(null);
        try {
            const response = await fetch(
                `/api/core/auth/sessions/${encodeURIComponent(sessionId)}`,
                { method: "DELETE" },
            );
            if (!response.ok) throw new Error("session_revocation_failed");
            setSessions((current) => removeRevokedSession(current, sessionId));
            setSuccess("Die Sitzung wurde abgemeldet.");
        } catch {
            setError(revokeErrorMessage);
        } finally {
            setPendingAction(null);
        }
    };

    const revokeOtherSessions = async () => {
        setPendingAction("others");
        setError(null);
        setSuccess(null);
        try {
            const response = await fetch("/api/core/auth/sessions/others", {
                method: "DELETE",
            });
            if (!response.ok) throw new Error("other_session_revocation_failed");
            setSessions(keepCurrentSession);
            setSuccess("Alle anderen Sitzungen wurden abgemeldet.");
        } catch {
            setError(revokeErrorMessage);
        } finally {
            setPendingAction(null);
        }
    };

    return (
        <div className="space-y-6 animate-in fade-in slide-in-from-bottom-2 duration-300">
            <Card>
                <CardHeader>
                    <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
                        <div>
                            <CardTitle className="flex items-center gap-2 text-lg">
                                <ShieldCheck className="h-5 w-5 text-primary" />
                                Aktive Sitzungen
                            </CardTitle>
                            <CardDescription className="mt-2">
                                Verwalte deine aktiven GameHub-Sitzungen. Es werden keine Geräte-,
                                IP- oder Provider-Sitzungsdetails angezeigt.
                            </CardDescription>
                        </div>
                        <Button
                            type="button"
                            variant="outline"
                            size="sm"
                            onClick={() => void loadSessions()}
                            disabled={isLoading || pendingAction !== null}
                        >
                            <RefreshCw className={isLoading ? "animate-spin" : undefined} />
                            Aktualisieren
                        </Button>
                    </div>
                </CardHeader>
                <CardContent className="space-y-4">
                    {success && (
                        <div
                            role="status"
                            className="rounded-lg border border-emerald-500/40 bg-emerald-500/10 p-3 text-sm text-emerald-700 dark:text-emerald-300"
                        >
                            {success}
                        </div>
                    )}
                    {error && (
                        <div
                            role="alert"
                            className="rounded-lg border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive"
                        >
                            {error}
                        </div>
                    )}

                    {isLoading ? (
                        <div className="flex items-center justify-center p-12 text-muted-foreground">
                            <Loader2 className="mr-2 h-5 w-5 animate-spin" />
                            Sitzungen werden geladen...
                        </div>
                    ) : sessions.length === 0 ? (
                        <div className="rounded-lg border p-6 text-center text-sm text-muted-foreground">
                            Keine aktive Sitzung verfügbar.
                        </div>
                    ) : (
                        <ul className="space-y-3">
                            {sessions.map((session) => (
                                <li
                                    key={session.sessionId}
                                    className="flex flex-col gap-3 rounded-lg border p-4 sm:flex-row sm:items-center sm:justify-between"
                                >
                                    <div className="min-w-0">
                                        <div className="flex items-center gap-2">
                                            <span className="font-medium">GameHub-Sitzung</span>
                                            {session.isCurrent && (
                                                <Badge variant="secondary">Aktuell</Badge>
                                            )}
                                        </div>
                                        <p className="mt-1 text-sm text-muted-foreground">
                                            Erstellt am {formatSessionCreatedAt(session.createdAtUtc)}
                                        </p>
                                    </div>

                                    {session.isCurrent ? (
                                        <form action="/api/auth/logout" method="post">
                                            <Button
                                                type="submit"
                                                variant="outline"
                                                size="sm"
                                                disabled={pendingAction !== null}
                                            >
                                                <LogOut />
                                                Aktuelle Sitzung abmelden
                                            </Button>
                                        </form>
                                    ) : (
                                        <Button
                                            type="button"
                                            variant="outline"
                                            size="sm"
                                            onClick={() => void revokeSession(session.sessionId)}
                                            disabled={pendingAction !== null}
                                        >
                                            {pendingAction === session.sessionId
                                                ? <Loader2 className="animate-spin" />
                                                : <LogOut />}
                                            Sitzung abmelden
                                        </Button>
                                    )}
                                </li>
                            ))}
                        </ul>
                    )}

                    <div className="flex justify-end border-t pt-4">
                        <Button
                            type="button"
                            variant="destructive"
                            onClick={() => void revokeOtherSessions()}
                            disabled={
                                isLoading
                                || pendingAction !== null
                                || otherSessionCount === 0
                            }
                        >
                            {pendingAction === "others"
                                ? <Loader2 className="animate-spin" />
                                : <LogOut />}
                            Alle anderen Sitzungen abmelden
                        </Button>
                    </div>
                </CardContent>
            </Card>
        </div>
    );
}
