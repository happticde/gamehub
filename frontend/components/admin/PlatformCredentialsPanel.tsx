"use client";

import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import {
    AlertDialog,
    AlertDialogAction,
    AlertDialogCancel,
    AlertDialogContent,
    AlertDialogDescription,
    AlertDialogFooter,
    AlertDialogHeader,
    AlertDialogTitle,
    AlertDialogTrigger,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { getPlatformColor } from "@/lib/platform-colors";
import type {
    Platform,
    PlatformCredentialManagement,
    PlatformCredentialStatus,
} from "@/types/game";
import {
    CheckCircle2,
    ExternalLink,
    KeyRound,
    Loader2,
    PlugZap,
    Plus,
    RefreshCw,
    ShieldCheck,
    Trash2,
} from "lucide-react";

type CredentialPayload = Record<string, unknown>;
type GogConnectResult = {
    user: string;
    cookie: string;
    expired: boolean;
};

/**
 * Tenant credential management through Core's authenticated BFF route. Secret values live only
 * in component state until submission; Core and Ingest never return them to the browser.
 */
export function PlatformCredentialsPanel() {
    const [state, setState] = useState<PlatformCredentialManagement | null>(null);
    const [loading, setLoading] = useState(true);
    const [busyPlatform, setBusyPlatform] = useState<Platform | null>(null);
    const [message, setMessage] = useState<string | null>(null);
    const [error, setError] = useState<string | null>(null);

    const loadStatus = useCallback(async () => {
        setLoading(true);
        setError(null);
        try {
            const response = await fetch("/api/core/platform-credentials", { cache: "no-store" });
            if (!response.ok) throw new Error(await readError(response));
            setState(await response.json());
        } catch (caught) {
            setError(errorMessage(caught));
        } finally {
            setLoading(false);
        }
    }, []);

    useEffect(() => {
        void loadStatus();
    }, [loadStatus]);

    const save = async (platform: Platform, credentials: CredentialPayload): Promise<boolean> => {
        setBusyPlatform(platform);
        setMessage(null);
        setError(null);
        try {
            const response = await fetch(`/api/core/platform-credentials/${platform}`, {
                method: "PUT",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ schemaVersion: 1, credentials }),
            });
            if (!response.ok) throw new Error(await readError(response));
            setState(await response.json());
            setMessage(`${platformLabel(platform)} credential stored securely.`);
            return true;
        } catch (caught) {
            setError(errorMessage(caught));
            return false;
        } finally {
            setBusyPlatform(null);
        }
    };

    const remove = async (platform: Platform) => {
        setBusyPlatform(platform);
        setMessage(null);
        setError(null);
        try {
            const response = await fetch(`/api/core/platform-credentials/${platform}`, {
                method: "DELETE",
            });
            if (!response.ok) throw new Error(await readError(response));
            setState(await response.json());
            setMessage(`${platformLabel(platform)} credential removed.`);
        } catch (caught) {
            setError(errorMessage(caught));
        } finally {
            setBusyPlatform(null);
        }
    };

    if (loading && !state) {
        return (
            <div className="flex items-center justify-center p-12 text-muted-foreground">
                <Loader2 className="mr-2 h-5 w-5 animate-spin" />
                Loading credential status…
            </div>
        );
    }

    const statusFor = (platform: Platform) =>
        state?.credentials.find((credential) => credential.platform === platform);

    return (
        <div className="space-y-6 animate-in fade-in slide-in-from-bottom-2 duration-300">
            <Card>
                <CardHeader className="pb-3">
                    <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
                        <div>
                            <CardTitle className="flex items-center gap-2 text-lg">
                                <ShieldCheck className="h-5 w-5 text-primary" />
                                Encrypted platform vault
                            </CardTitle>
                            <CardDescription className="mt-1">
                                Credentials are encrypted per tenant and are never displayed again.
                            </CardDescription>
                        </div>
                        <Badge variant={state?.platformSyncEnabled ? "default" : "secondary"}>
                            Platform sync {state?.platformSyncEnabled ? "enabled" : "disabled"}
                        </Badge>
                    </div>
                </CardHeader>
                <CardContent className="text-sm text-muted-foreground">
                    Sync is enabled by Core only after at least one supported credential has been
                    stored and verified. Removing the final credential disables sync first.
                </CardContent>
            </Card>

            {(message || error) && (
                <div
                    role="status"
                    className={`rounded-lg border px-4 py-3 text-sm ${
                        error
                            ? "border-destructive/40 bg-destructive/10 text-destructive"
                            : "border-green-500/30 bg-green-500/10 text-green-700 dark:text-green-300"
                    }`}
                >
                    {error ?? message}
                </div>
            )}

            <div className="grid gap-4 lg:grid-cols-3">
                <SteamCredentialCard
                    status={statusFor("Steam")}
                    busy={busyPlatform === "Steam"}
                    onSave={(payload) => save("Steam", payload)}
                    onDelete={() => remove("Steam")}
                />
                <GogCredentialCard
                    status={statusFor("Gog")}
                    busy={busyPlatform === "Gog"}
                    onSave={(payload) => save("Gog", payload)}
                    onDelete={() => remove("Gog")}
                />
                <PsnCredentialCard
                    status={statusFor("Psn")}
                    busy={busyPlatform === "Psn"}
                    onSave={(payload) => save("Psn", payload)}
                    onDelete={() => remove("Psn")}
                />
            </div>

            <Card className="border-dashed">
                <CardHeader>
                    <CardTitle className="text-base">Epic Games</CardTitle>
                    <CardDescription>
                        Stored Epic retrieval is not supported yet, so Epic cannot activate platform sync.
                    </CardDescription>
                </CardHeader>
            </Card>
        </div>
    );
}

interface CredentialCardProps {
    platform: Platform;
    description: string;
    status?: PlatformCredentialStatus;
    busy: boolean;
    onDelete: () => Promise<void>;
    children: ReactNode;
}

function CredentialCard({
    platform,
    description,
    status,
    busy,
    onDelete,
    children,
}: CredentialCardProps) {
    return (
        <Card className="overflow-hidden">
            <CardHeader>
                <div className="flex items-start justify-between gap-3">
                    <div>
                        <CardTitle className="flex items-center gap-2 text-base">
                            <span
                                className="h-3 w-3 rounded-full"
                                style={{ backgroundColor: getPlatformColor(platform) }}
                            />
                            {platformLabel(platform)}
                        </CardTitle>
                        <CardDescription className="mt-1">{description}</CardDescription>
                    </div>
                    <Badge variant={status?.isConfigured ? "default" : "outline"}>
                        {status?.isConfigured ? "Configured" : "Not configured"}
                    </Badge>
                </div>
                {status?.updatedAtUtc && (
                    <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
                        <CheckCircle2 className="h-3.5 w-3.5 text-green-500" />
                        Updated {new Date(status.updatedAtUtc).toLocaleString()}
                    </p>
                )}
            </CardHeader>
            <CardContent className="space-y-4">
                {children}
                {status?.isConfigured && (
                    <AlertDialog>
                        <AlertDialogTrigger asChild>
                            <Button variant="outline" className="w-full" disabled={busy}>
                                <Trash2 className="mr-2 h-4 w-4" />
                                Remove credential
                            </Button>
                        </AlertDialogTrigger>
                        <AlertDialogContent>
                            <AlertDialogHeader>
                                <AlertDialogTitle>Remove {platformLabel(platform)} credential?</AlertDialogTitle>
                                <AlertDialogDescription>
                                    The encrypted secret will be deleted. If this is the final configured
                                    platform, Core will disable platform sync before deletion.
                                </AlertDialogDescription>
                            </AlertDialogHeader>
                            <AlertDialogFooter>
                                <AlertDialogCancel>Cancel</AlertDialogCancel>
                                <AlertDialogAction onClick={() => void onDelete()}>
                                    Remove credential
                                </AlertDialogAction>
                            </AlertDialogFooter>
                        </AlertDialogContent>
                    </AlertDialog>
                )}
            </CardContent>
        </Card>
    );
}

interface FormProps {
    status?: PlatformCredentialStatus;
    busy: boolean;
    onSave: (credentials: CredentialPayload) => Promise<boolean>;
    onDelete: () => Promise<void>;
}

function SteamCredentialCard({ status, busy, onSave, onDelete }: FormProps) {
    const [apiKey, setApiKey] = useState("");
    const [steamId, setSteamId] = useState("");

    const submit = async () => {
        if (await onSave({ apiKey, steamId })) {
            setApiKey("");
            setSteamId("");
        }
    };

    return (
        <CredentialCard
            platform="Steam"
            description="Steam Web API key and 64-bit Steam ID"
            status={status}
            busy={busy}
            onDelete={onDelete}
        >
            <CredentialInput label="Steam ID (64-bit)" value={steamId} onChange={setSteamId} />
            <CredentialInput label="Steam Web API key" value={apiKey} onChange={setApiKey} secret />
            <SaveButton busy={busy} disabled={!apiKey.trim() || !steamId.trim()} onClick={submit} />
        </CredentialCard>
    );
}

function GogCredentialCard({ status, busy, onSave, onDelete }: FormProps) {
    const [cookie, setCookie] = useState("");
    const [user, setUser] = useState("");
    const [helperPayload, setHelperPayload] = useState("");
    const [handoffUrl, setHandoffUrl] = useState("");
    const [handoffPolling, setHandoffPolling] = useState(false);
    const [handoffMessage, setHandoffMessage] = useState<string | null>(null);
    const [handoffError, setHandoffError] = useState<string | null>(null);
    const [helperError, setHelperError] = useState<string | null>(null);
    const [pageOrigin, setPageOrigin] = useState("http://localhost:3005");
    const handoffAttemptRef = useRef(0);

    useEffect(() => () => {
        handoffAttemptRef.current += 1;
    }, []);

    useEffect(() => {
        setPageOrigin(window.location.origin);
    }, []);

    const submit = async () => {
        if (await onSave({ cookie, user })) {
            setCookie("");
            setUser("");
        }
    };

    const saveHelperPayload = async () => {
        setHelperError(null);

        const result = parseGogConnectPayload(helperPayload);
        if (result instanceof Error) {
            setHelperError(result.message);
            return;
        }

        if (result.expired) {
            setHelperError("The GOG session is already expired. Reconnect GOG and try again.");
            return;
        }

        if (await onSave({ cookie: result.cookie, user: result.user })) {
            setHelperPayload("");
        }
    };

    const launchProtocolHelper = () => {
        const url = new URL("gamehub-helper://connect/gog");
        url.searchParams.set("origin", window.location.origin);
        setHandoffError(null);
        setHelperError(null);
        setHandoffMessage("If the local protocol handler is installed, your browser will ask to open GameHub Helper. Paste the handoff URL from the helper Terminal window below.");
        window.location.href = url.toString();
    };

    const pollHandoffUrl = async () => {
        const parsedUrl = parseGogHandoffUrl(handoffUrl);
        if (parsedUrl instanceof Error) {
            setHandoffError(parsedUrl.message);
            return;
        }

        const attempt = handoffAttemptRef.current + 1;
        handoffAttemptRef.current = attempt;
        setHandoffPolling(true);
        setHandoffError(null);
        setHelperError(null);
        setHandoffMessage("Waiting for the local helper to capture your GOG session…");

        try {
            const deadline = Date.now() + 10 * 60 * 1000;
            while (Date.now() < deadline && handoffAttemptRef.current === attempt) {
                const response = await fetch(parsedUrl, { cache: "no-store" });
                if (handoffAttemptRef.current !== attempt) return;

                if (response.status === 202) {
                    setHandoffMessage("Helper is still waiting for the visible GOG sign-in…");
                    await delay(1500);
                    continue;
                }

                if (response.status === 410) {
                    throw new Error("This local helper result was already imported. Run the helper again for a fresh handoff URL.");
                }
                if (!response.ok) {
                    throw new Error(`The local helper returned status ${response.status}.`);
                }

                const result = parseGogConnectObject(await response.json());
                if (result instanceof Error) {
                    throw result;
                }
                if (result.expired) {
                    throw new Error("The GOG session is already expired. Reconnect GOG and try again.");
                }

                setHandoffMessage("GOG session captured. Storing it in the encrypted vault…");
                if (await onSave({ cookie: result.cookie, user: result.user })) {
                    setHandoffUrl("");
                    setHelperPayload("");
                    setHandoffMessage("GOG session imported from the local helper.");
                    return;
                }

                setCookie(result.cookie);
                setUser(result.user);
                setHandoffError("The helper result was captured, but storing it failed. The manual fields below are filled so you can retry.");
                return;
            }

            throw new Error("Timed out waiting for the local helper result.");
        } catch (caught) {
            if (handoffAttemptRef.current === attempt) {
                setHandoffError(localHandoffErrorMessage(caught));
                setHandoffMessage(null);
            }
        } finally {
            if (handoffAttemptRef.current === attempt) {
                setHandoffPolling(false);
            }
        }
    };

    const stopHandoffPolling = () => {
        handoffAttemptRef.current += 1;
        setHandoffPolling(false);
        setHandoffMessage(null);
    };

    return (
        <CredentialCard
            platform="Gog"
            description="Guided connect flow with manual credential fallback"
            status={status}
            busy={busy}
            onDelete={onDelete}
        >
            <div className="rounded-lg border bg-muted/30 p-3">
                <div className="flex items-start gap-2">
                    <PlugZap className="mt-0.5 h-4 w-4 text-primary" />
                    <div className="space-y-2">
                        <div>
                            <h4 className="text-sm font-medium text-foreground">
                                {status?.isConfigured ? "Reconnect GOG" : "Connect GOG"}
                            </h4>
                            <p className="mt-1 text-xs leading-5 text-muted-foreground">
                                After installing the local protocol handler, launch the helper here,
                                sign in with GOG in the visible browser, then paste the local
                                handoff URL printed by the helper Terminal window.
                            </p>
                            <code className="mt-2 block break-all rounded bg-background px-2 py-1.5 font-mono text-[11px] text-foreground">
                                gamehub-cli helper install-protocol-handler
                            </code>
                            <p className="mt-2 text-xs leading-5 text-muted-foreground">
                                The launch URL contains only this Admin page origin, never the gog-al cookie.
                            </p>
                        </div>
                        <Button
                            variant="default"
                            size="sm"
                            className="w-full"
                            disabled={busy || handoffPolling}
                            onClick={launchProtocolHelper}
                        >
                            <PlugZap className="mr-2 h-4 w-4" />
                            Launch local helper
                        </Button>
                        <div>
                            <p className="text-xs leading-5 text-muted-foreground">
                                Terminal fallback if the protocol handler is not installed:
                            </p>
                            <code className="mt-1 block break-all rounded bg-background px-2 py-1.5 font-mono text-[11px] text-foreground">
                                cd services/cli && go run . connect gog --handoff-server --origin {pageOrigin}
                            </code>
                        </div>
                        <Input
                            aria-label="GOG local helper handoff URL"
                            className="font-mono text-xs"
                            placeholder="http://127.0.0.1:12345/connect/gog/result?nonce=..."
                            value={handoffUrl}
                            onChange={(event) => {
                                setHandoffUrl(event.target.value);
                                setHandoffError(null);
                            }}
                            disabled={handoffPolling}
                        />
                        {(handoffMessage || handoffError) && (
                            <p
                                className={`rounded-md border px-3 py-2 text-xs ${
                                    handoffError
                                        ? "border-destructive/30 bg-destructive/10 text-destructive"
                                        : "border-primary/30 bg-primary/10 text-primary"
                                }`}
                            >
                                {handoffError ?? handoffMessage}
                            </p>
                        )}
                        <Button
                            variant="outline"
                            size="sm"
                            className="w-full"
                            disabled={busy || handoffPolling || !handoffUrl.trim()}
                            onClick={() => void pollHandoffUrl()}
                        >
                            {handoffPolling ? (
                                <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                            ) : (
                                <PlugZap className="mr-2 h-4 w-4" />
                            )}
                            {handoffPolling ? "Waiting for helper…" : "Import from local helper"}
                        </Button>
                        {handoffPolling && (
                            <Button
                                variant="ghost"
                                size="sm"
                                className="w-full"
                                onClick={stopHandoffPolling}
                            >
                                Stop waiting
                            </Button>
                        )}
                    </div>
                </div>
            </div>

            <div className="rounded-lg border bg-muted/20 p-3">
                <div className="flex items-start gap-2">
                    <KeyRound className="mt-0.5 h-4 w-4 text-muted-foreground" />
                    <div className="space-y-2">
                        <div>
                            <h4 className="text-sm font-medium text-foreground">
                                JSON fallback
                            </h4>
                            <p className="mt-1 text-xs leading-5 text-muted-foreground">
                                If the loopback handoff is unavailable, run the helper without
                                --handoff-server and paste its one-time JSON result here.
                            </p>
                        </div>
                        <Textarea
                            aria-label="GOG helper result"
                            className="min-h-24 font-mono text-xs"
                            placeholder='{"provider":"gog","user":"gog-user","cookies":{"gog-al":"..."}}'
                            value={helperPayload}
                            onChange={(event) => {
                                setHelperPayload(event.target.value);
                                setHelperError(null);
                            }}
                        />
                        {helperError && (
                            <p className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">
                                {helperError}
                            </p>
                        )}
                        <Button
                            className="w-full"
                            disabled={busy || !helperPayload.trim()}
                            onClick={() => void saveHelperPayload()}
                        >
                            {busy ? (
                                <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                            ) : status?.isConfigured ? (
                                <RefreshCw className="mr-2 h-4 w-4" />
                            ) : (
                                <PlugZap className="mr-2 h-4 w-4" />
                            )}
                            {busy
                                ? "Saving…"
                                : status?.isConfigured
                                    ? "Store refreshed GOG session"
                                    : "Store JSON helper result"}
                        </Button>
                    </div>
                </div>
            </div>

            <div className="space-y-3 border-t pt-4">
                <div className="flex items-center justify-between gap-2">
                    <p className="text-xs font-medium text-muted-foreground">Manual fallback</p>
                    <Button
                        variant="ghost"
                        size="sm"
                        className="h-7 px-2 text-xs"
                        onClick={() => {
                            window.open("https://www.gog.com/account", "_blank", "noopener,noreferrer");
                        }}
                    >
                        <ExternalLink className="mr-1.5 h-3.5 w-3.5" />
                        Open GOG
                    </Button>
                </div>
                <CredentialInput label="GOG username" value={user} onChange={setUser} />
                <CredentialInput label="gog-al cookie" value={cookie} onChange={setCookie} secret />
                <SaveButton busy={busy} disabled={!cookie.trim() || !user.trim()} onClick={submit} />
            </div>
        </CredentialCard>
    );
}

function PsnCredentialCard({ status, busy, onSave, onDelete }: FormProps) {
    const [cookies, setCookies] = useState([{ key: "", value: "" }]);
    const validCookies = cookies.filter((cookie) => cookie.key.trim() && cookie.value.trim());

    const submit = async () => {
        const payload = Object.fromEntries(
            validCookies.map((cookie) => [cookie.key.trim(), cookie.value]),
        );
        if (await onSave({ cookies: payload })) setCookies([{ key: "", value: "" }]);
    };

    return (
        <CredentialCard
            platform="Psn"
            description="PlayStation Store browser cookies"
            status={status}
            busy={busy}
            onDelete={onDelete}
        >
            {cookies.map((cookie, index) => (
                <div key={index} className="grid grid-cols-[1fr_1.4fr_auto] gap-2">
                    <Input
                        aria-label={`Cookie ${index + 1} name`}
                        placeholder="Cookie name"
                        value={cookie.key}
                        onChange={(event) => setCookies((current) => current.map((item, itemIndex) =>
                            itemIndex === index ? { ...item, key: event.target.value } : item))}
                    />
                    <Input
                        aria-label={`Cookie ${index + 1} value`}
                        placeholder="Cookie value"
                        type="password"
                        autoComplete="off"
                        value={cookie.value}
                        onChange={(event) => setCookies((current) => current.map((item, itemIndex) =>
                            itemIndex === index ? { ...item, value: event.target.value } : item))}
                    />
                    <Button
                        variant="ghost"
                        size="icon"
                        aria-label={`Remove cookie ${index + 1}`}
                        disabled={cookies.length === 1}
                        onClick={() => setCookies((current) => current.filter((_, itemIndex) => itemIndex !== index))}
                    >
                        <Trash2 className="h-4 w-4" />
                    </Button>
                </div>
            ))}
            <Button
                variant="outline"
                size="sm"
                className="w-full"
                onClick={() => setCookies((current) => [...current, { key: "", value: "" }])}
            >
                <Plus className="mr-2 h-4 w-4" /> Add cookie
            </Button>
            <SaveButton busy={busy} disabled={validCookies.length === 0} onClick={submit} />
        </CredentialCard>
    );
}

function CredentialInput({
    label,
    value,
    onChange,
    secret = false,
}: {
    label: string;
    value: string;
    onChange: (value: string) => void;
    secret?: boolean;
}) {
    return (
        <label className="block space-y-1.5 text-xs font-medium text-muted-foreground">
            {label}
            <Input
                type={secret ? "password" : "text"}
                autoComplete="off"
                value={value}
                onChange={(event) => onChange(event.target.value)}
            />
        </label>
    );
}

function SaveButton({
    busy,
    disabled,
    onClick,
}: {
    busy: boolean;
    disabled: boolean;
    onClick: () => Promise<void>;
}) {
    return (
        <Button className="w-full" disabled={busy || disabled} onClick={() => void onClick()}>
            {busy ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <KeyRound className="mr-2 h-4 w-4" />}
            {busy ? "Saving…" : "Store encrypted credential"}
        </Button>
    );
}

async function readError(response: Response): Promise<string> {
    try {
        const body = await response.json() as { message?: string };
        return body.message ?? `Request failed with status ${response.status}.`;
    } catch {
        return `Request failed with status ${response.status}.`;
    }
}

function errorMessage(error: unknown): string {
    return error instanceof Error ? error.message : "The credential request failed.";
}

function localHandoffErrorMessage(error: unknown): string {
    const message = errorMessage(error);
    if (message.toLowerCase() === "failed to fetch") {
        return "The browser could not read the local helper. Make sure the helper is still running, the handoff URL has not expired, and the helper --origin matches this Admin page origin.";
    }
    return message;
}

function parseGogConnectPayload(value: string): GogConnectResult | Error {
    let parsed: unknown;
    try {
        parsed = JSON.parse(value);
    } catch {
        return new Error("Paste the JSON result returned by the local GOG helper.");
    }

    if (!isObjectRecord(parsed)) {
        return new Error("The GOG helper result must be a JSON object.");
    }

    return parseGogConnectObject(parsed);
}

function parseGogConnectObject(parsed: Record<string, unknown>): GogConnectResult | Error {
    const provider = readOptionalText(parsed.provider ?? parsed.platform);
    if (provider && provider.toLowerCase() !== "gog") {
        return new Error("This helper result is not for GOG.");
    }

    const status = readOptionalText(parsed.status ?? parsed.error)?.toLowerCase();
    const user = readOptionalText(parsed.user ?? parsed.username);
    const cookie = readOptionalText(
        parsed.cookie ?? parsed.gogAl ?? parsed.gogAlCookie ?? parsed["gog-al"],
    )
        ?? readCookieValue(parsed.cookies, "gog-al");

    if (!user) {
        return new Error("The GOG helper result is missing the user field.");
    }
    if (!cookie) {
        return new Error("The GOG helper result is missing the gog-al cookie.");
    }

    const expiresAt = readOptionalText(parsed.expiresAtUtc ?? parsed.expiresAt);
    const isExpired = status === "session_expired"
        || status === "expired"
        || (expiresAt ? Date.parse(expiresAt) <= Date.now() : false);

    return { user, cookie, expired: isExpired };
}

function parseGogHandoffUrl(value: string): string | Error {
    const match = value.trim().match(/https?:\/\/\S+/);
    if (!match) {
        return new Error("Paste the local handoff URL printed by the GOG helper.");
    }

    let url: URL;
    try {
        url = new URL(match[0]);
    } catch {
        return new Error("The local handoff URL is not valid.");
    }

    const isLoopbackHost = url.hostname === "127.0.0.1" || url.hostname === "localhost";
    if (url.protocol !== "http:" || !isLoopbackHost) {
        return new Error("The helper handoff URL must be an http://127.0.0.1 or http://localhost URL.");
    }
    if (url.pathname !== "/connect/gog/result" || !url.searchParams.get("nonce")) {
        return new Error("The helper handoff URL does not match the GOG loopback result endpoint.");
    }

    url.searchParams.set("origin", window.location.origin);
    return url.toString();
}

function delay(milliseconds: number): Promise<void> {
    return new Promise((resolve) => window.setTimeout(resolve, milliseconds));
}

function readCookieValue(value: unknown, name: string): string | null {
    if (!isObjectRecord(value)) return null;
    return readOptionalText(value[name]);
}

function readOptionalText(value: unknown): string | null {
    return typeof value === "string" && value.trim() ? value.trim() : null;
}

function isObjectRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === "object" && value !== null && !Array.isArray(value);
}

function platformLabel(platform: Platform): string {
    if (platform === "Gog") return "GOG";
    if (platform === "Psn") return "PlayStation";
    return platform;
}
