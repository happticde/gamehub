"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { Loader2, RefreshCw, Unplug } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { AlertDialog, AlertDialogTrigger, AlertDialogContent, AlertDialogHeader,
    AlertDialogTitle, AlertDialogDescription, AlertDialogFooter, AlertDialogCancel } from "@/components/ui/alert-dialog";
import { fetchAuthenticated } from "@/lib/authenticated-fetch";
import { McpConnection, parseMcpConnections } from "@/lib/mcp-connections";

const endpoint = "/api/core/auth/mcp-connections";
const formatDate = (value: string) => new Intl.DateTimeFormat("de-DE", {
    dateStyle: "medium", timeStyle: "short",
}).format(new Date(value));

/** Browser management uses the existing cookie boundary and waits for confirmed revocation. */
export function McpConnectionsPanel() {
    const [connections, setConnections] = useState<McpConnection[]>([]);
    const [loading, setLoading] = useState(true);
    const [pending, setPending] = useState(false);
    const [selected, setSelected] = useState<string | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [success, setSuccess] = useState<string | null>(null);
    const request = useRef<AbortController | null>(null);
    const refreshButton = useRef<HTMLButtonElement>(null);
    const revoked = useRef<string | null>(null);

    const load = useCallback(() => {
        if (request.current) return;
        const controller = new AbortController();
        request.current = controller;
        return fetchAuthenticated(endpoint, {
            cache: "no-store", signal: AbortSignal.any([controller.signal, AbortSignal.timeout(15000)]),
        }).then(async (response) => {
            if (!response.ok) throw new Error("list_failed");
            const rows = parseMcpConnections(await response.json());
            if (!controller.signal.aborted) setConnections(rows);
        }).catch(() => {
            if (!controller.signal.aborted) setError("Verbindungen konnten nicht geladen werden. Bitte aktualisiere die Liste.");
        }).finally(() => {
            if (request.current === controller) request.current = null;
            if (!controller.signal.aborted) setLoading(false);
        });
    }, []);

    useEffect(() => {
        void load();
        return () => { request.current?.abort(); request.current = null; };
    }, [load]);

    const revoke = async (id: string) => {
        if (request.current) return;
        const controller = new AbortController();
        request.current = controller;
        setPending(true);
        setError(null);
        try {
            const response = await fetchAuthenticated(`${endpoint}/${encodeURIComponent(id)}`, {
                method: "DELETE", signal: AbortSignal.any([controller.signal, AbortSignal.timeout(15000)]),
            });
            if (response.status !== 204) throw new Error("revoke_failed");
            if (!controller.signal.aborted) {
                revoked.current = id;
                setSelected(null);
                setConnections((rows) => rows.filter((row) => row.id !== id));
                setSuccess("Die Verbindung wurde widerrufen. Für erneuten Zugriff ist eine neue Anmeldung nötig.");
            }
        } catch {
            if (!controller.signal.aborted) setError("Der Widerruf konnte nicht bestätigt werden. Du kannst ihn erneut versuchen oder die Liste aktualisieren.");
        } finally {
            if (request.current === controller) request.current = null;
            if (!controller.signal.aborted) setPending(false);
        }
    };

    return <Card>
        <CardHeader>
            <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
                <div>
                    <CardTitle className="flex items-center gap-2 text-lg"><Unplug className="h-5 w-5 text-primary" />Assistenten-Verbindungen</CardTitle>
                    <CardDescription className="mt-2">Verwalte den Zugriff deiner Assistenten über Model Context Protocol (MCP). Jede Verbindung hat eigene Berechtigungen.</CardDescription>
                </div>
                <Button ref={refreshButton} type="button" variant="outline" size="sm" onClick={() => {
                    setLoading(true); setError(null); setSuccess(null); void load();
                }} disabled={loading || pending || selected !== null}>
                    <RefreshCw className={loading ? "animate-spin motion-reduce:animate-none" : undefined} />Aktualisieren
                </Button>
            </div>
        </CardHeader>
        <CardContent className="space-y-4" aria-busy={loading || pending}>
            {success && <p role="status" className="rounded-lg border p-3 text-sm">{success}</p>}
            {error && selected === null && <p role="alert" className="rounded-lg border border-destructive/50 p-3 text-sm text-destructive">{error}</p>}
            {loading ? <p role="status" className="flex min-h-24 items-center justify-center text-sm text-muted-foreground"><Loader2 className="mr-2 h-5 w-5 animate-spin motion-reduce:animate-none" />Verbindungen werden geladen…</p>
                : connections.length === 0 ? !error && <p className="rounded-lg border p-6 text-sm text-muted-foreground">Keine aktiven Verbindungen. Melde deinen Assistenten an und bestätige seinen Zugriff im Browser.</p>
                    : <ul className="space-y-3">{connections.map((connection) => <li key={connection.id} className="flex flex-col gap-4 rounded-lg border p-4 sm:flex-row sm:items-start sm:justify-between">
                        <div className="min-w-0 space-y-2">
                            <p className="break-words font-medium">{connection.clientId}</p>
                            <p className="break-all text-xs text-muted-foreground">Verbindung {connection.id}</p>
                            <div className="flex flex-wrap gap-2"><Badge variant="secondary">Bibliothek lesen</Badge>{connection.scopes.includes("tracking:write") && <Badge variant="outline">Spielstatus und Bewertung ändern</Badge>}</div>
                            <p className="break-all text-sm text-muted-foreground">Instanz: {connection.resource}</p>
                            <p className="text-sm text-muted-foreground">Erstellt: {formatDate(connection.createdAtUtc)}<br />Läuft ab: {formatDate(connection.expiresAtUtc)}</p>
                        </div>
                        <AlertDialog open={selected === connection.id} onOpenChange={(open) => {
                            if (pending) return;
                            setSelected(open ? connection.id : null); setError(null); setSuccess(null);
                        }}>
                            <AlertDialogTrigger asChild><Button type="button" variant="outline" size="sm" disabled={pending || loading} aria-label={`Verbindung ${connection.id} widerrufen`}><Unplug />Zugriff widerrufen</Button></AlertDialogTrigger>
                            <AlertDialogContent className="max-h-[90dvh] overflow-y-auto" onCloseAutoFocus={(event) => {
                                if (revoked.current === connection.id) { event.preventDefault(); refreshButton.current?.focus(); }
                            }}>
                                <AlertDialogHeader>
                                    <AlertDialogTitle>Zugriff dieser Verbindung widerrufen?</AlertDialogTitle>
                                    <AlertDialogDescription className="break-words">{connection.clientId} · Verbindung {connection.id}. Nach dem Widerruf werden neue Zugriffe und Erneuerungen abgelehnt. Andere Verbindungen und deine Browser-Anmeldung bleiben bestehen. Für erneuten Zugriff musst du diese Verbindung neu anmelden.</AlertDialogDescription>
                                </AlertDialogHeader>
                                {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
                                <AlertDialogFooter>
                                    <AlertDialogCancel disabled={pending}>Abbrechen</AlertDialogCancel>
                                    <Button type="button" variant="destructive" disabled={pending} onClick={() => void revoke(connection.id)}>{pending && <Loader2 className="animate-spin motion-reduce:animate-none" />}Zugriff widerrufen</Button>
                                </AlertDialogFooter>
                            </AlertDialogContent>
                        </AlertDialog>
                    </li>)}</ul>}
        </CardContent>
    </Card>;
}
