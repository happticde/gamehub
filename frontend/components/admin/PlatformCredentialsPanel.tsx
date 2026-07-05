"use client";

import { useCallback, useEffect, useRef, useState, type ReactNode, type RefObject } from "react";
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
import {
    parseGogConnectObject,
    parseGogConnectPayload,
    parseHandoffUrl,
    parseHelperLaunchStatus,
    parsePsnConnectObject,
    parsePsnConnectPayload,
} from "@/lib/platform-connect";
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
type ConnectProvider = "gog" | "psn";

const helperLaunchStatusBaseUrl = "http://127.0.0.1:53935";

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

            <div className="grid gap-5 lg:grid-cols-2 2xl:grid-cols-3">
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
            <CardHeader className="space-y-3 pb-4">
                <div className="flex items-start justify-between gap-3">
                    <div className="min-w-0">
                        <CardTitle className="flex items-center gap-2 text-base">
                            <span
                                className="h-3 w-3 rounded-full"
                                style={{ backgroundColor: getPlatformColor(platform) }}
                            />
                            {platformLabel(platform)}
                        </CardTitle>
                        <CardDescription className="mt-1">{description}</CardDescription>
                    </div>
                    <Badge className="shrink-0" variant={status?.isConfigured ? "default" : "outline"}>
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
            <Button variant="link" size="sm" className="h-auto justify-start p-0 text-xs" asChild>
                <a
                    href="https://steamcommunity.com/dev/apikey"
                    target="_blank"
                    rel="noopener noreferrer"
                >
                    <ExternalLink className="mr-1.5 h-3.5 w-3.5" />
                    Create or manage your Steam Web API key
                </a>
            </Button>
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
    const [helperLaunching, setHelperLaunching] = useState(false);
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
        if (result.provider !== "gog") {
            setHelperError("This helper result is not for GOG.");
            return;
        }

        if (await onSave({ cookie: result.cookie, user: result.user })) {
            setHelperPayload("");
        }
    };

    const launchProtocolHelper = async () => {
        const launchId = newHelperLaunchId();
        const url = new URL("gamehub-helper://connect/gog");
        url.searchParams.set("origin", window.location.origin);
        url.searchParams.set("launchId", launchId);

        const attempt = handoffAttemptRef.current + 1;
        handoffAttemptRef.current = attempt;
        setHandoffError(null);
        setHelperError(null);
        setHandoffUrl("");
        setHelperLaunching(true);
        setHandoffMessage("Waiting for the local helper to start. If your browser asks, allow it to open GameHub Helper.");
        window.location.href = url.toString();

        try {
            const launchedHandoffUrl = await waitForHelperLaunchStatus("gog", launchId, attempt, handoffAttemptRef);
            if (handoffAttemptRef.current !== attempt) return;

            setHandoffUrl(launchedHandoffUrl);
            setHandoffMessage("Local helper started. Waiting for the visible GOG sign-in…");
            await pollHandoffUrl(launchedHandoffUrl);
        } catch (caught) {
            if (handoffAttemptRef.current === attempt) {
                setHandoffError(localLaunchErrorMessage(caught));
                setHandoffMessage(null);
            }
        } finally {
            if (handoffAttemptRef.current === attempt) {
                setHelperLaunching(false);
            }
        }
    };

    async function pollHandoffUrl(candidateUrl = handoffUrl) {
        const parsedUrl = parseHandoffUrl(candidateUrl, "gog", window.location.origin);
        if (parsedUrl instanceof Error) {
            setHandoffError(parsedUrl.message);
            return;
        }

        const attempt = handoffAttemptRef.current + 1;
        handoffAttemptRef.current = attempt;
        setHandoffPolling(true);
        setHelperLaunching(false);
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
                    throw new Error(await readLocalHelperFailure(response));
                }

                const result = parseGogConnectObject(await response.json());
                if (result instanceof Error) {
                    throw result;
                }
                if (result.expired) {
                    throw new Error("The GOG session is already expired. Reconnect GOG and try again.");
                }
                if (result.provider !== "gog") {
                    throw new Error("This helper result is not for GOG.");
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
    }

    const stopHandoffPolling = () => {
        handoffAttemptRef.current += 1;
        setHandoffPolling(false);
        setHelperLaunching(false);
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
            <CredentialSection
                icon={<PlugZap className="h-4 w-4 text-primary" />}
                title={status?.isConfigured ? "Reconnect GOG" : "Connect GOG"}
                description="Launch the local helper, sign in with GOG in the visible browser, and import the one-time handoff automatically."
                tone="primary"
            >
                <p className="text-xs leading-5 text-muted-foreground">
                    The launch URL contains only this Admin page origin and a one-time launch ID,
                    never the gog-al cookie.
                </p>
                <Button
                    variant="default"
                    size="sm"
                    className="w-full"
                    disabled={busy || handoffPolling || helperLaunching}
                    onClick={() => void launchProtocolHelper()}
                >
                    {helperLaunching ? (
                        <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                    ) : (
                        <PlugZap className="mr-2 h-4 w-4" />
                    )}
                    {helperLaunching ? "Starting helper…" : "Connect GOG"}
                </Button>
                <HelperStatusMessage message={handoffMessage} error={handoffError} />
                {(handoffPolling || helperLaunching) && (
                    <Button
                        variant="ghost"
                        size="sm"
                        className="w-full"
                        onClick={stopHandoffPolling}
                    >
                        Stop waiting
                    </Button>
                )}
                <CommandFallback
                    title="Advanced helper options"
                    commands={[
                        'cd services/cli && go build -o gamehub-cli . && ./gamehub-cli helper install-protocol-handler --cli-path "$PWD/gamehub-cli"',
                        `cd services/cli && go run . connect gog --handoff-server --origin ${pageOrigin}`,
                    ]}
                >
                    <Input
                        aria-label="GOG local helper handoff URL"
                        className="font-mono text-xs"
                        placeholder="Paste local helper handoff URL"
                        value={handoffUrl}
                        onChange={(event) => {
                            setHandoffUrl(event.target.value);
                            setHandoffError(null);
                        }}
                        disabled={handoffPolling || helperLaunching}
                    />
                    <Button
                        variant="outline"
                        size="sm"
                        className="w-full"
                        disabled={busy || handoffPolling || helperLaunching || !handoffUrl.trim()}
                        onClick={() => void pollHandoffUrl()}
                    >
                        {handoffPolling ? (
                            <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                        ) : (
                            <PlugZap className="mr-2 h-4 w-4" />
                        )}
                        {handoffPolling ? "Waiting for helper…" : "Import pasted handoff URL"}
                    </Button>
                </CommandFallback>
            </CredentialSection>

            <HiddenFallback
                title="JSON fallback"
                description="If loopback handoff is unavailable, paste the helper's one-time JSON result here."
            >
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
            </HiddenFallback>

            <HiddenFallback
                title="Manual fallback"
                description="Use only when the helper cannot capture the session for you."
                action={
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
                }
            >
                <CredentialInput label="GOG username" value={user} onChange={setUser} />
                <CredentialInput label="gog-al cookie" value={cookie} onChange={setCookie} secret />
                <SaveButton busy={busy} disabled={!cookie.trim() || !user.trim()} onClick={submit} />
            </HiddenFallback>
        </CredentialCard>
    );
}

function PsnCredentialCard({ status, busy, onSave, onDelete }: FormProps) {
    const [cookies, setCookies] = useState([{ key: "", value: "" }]);
    const [helperPayload, setHelperPayload] = useState("");
    const [handoffUrl, setHandoffUrl] = useState("");
    const [handoffPolling, setHandoffPolling] = useState(false);
    const [helperLaunching, setHelperLaunching] = useState(false);
    const [handoffMessage, setHandoffMessage] = useState<string | null>(null);
    const [handoffError, setHandoffError] = useState<string | null>(null);
    const [helperError, setHelperError] = useState<string | null>(null);
    const [pageOrigin, setPageOrigin] = useState("http://localhost:3005");
    const handoffAttemptRef = useRef(0);
    const validCookies = cookies.filter((cookie) => cookie.key.trim() && cookie.value.trim());

    useEffect(() => () => {
        handoffAttemptRef.current += 1;
    }, []);

    useEffect(() => {
        setPageOrigin(window.location.origin);
    }, []);

    const submit = async () => {
        const payload = Object.fromEntries(
            validCookies.map((cookie) => [cookie.key.trim(), cookie.value]),
        );
        if (await onSave({ cookies: payload })) setCookies([{ key: "", value: "" }]);
    };

    const saveHelperPayload = async () => {
        setHelperError(null);

        const result = parsePsnConnectPayload(helperPayload);
        if (result instanceof Error) {
            setHelperError(result.message);
            return;
        }

        if (result.expired) {
            setHelperError("The PlayStation session is already expired. Reconnect PlayStation and try again.");
            return;
        }
        if (result.provider !== "psn") {
            setHelperError("This helper result is not for PlayStation.");
            return;
        }

        if (await onSave({ cookies: result.cookies })) {
            setHelperPayload("");
        }
    };

    const launchProtocolHelper = async () => {
        const launchId = newHelperLaunchId();
        const url = new URL("gamehub-helper://connect/psn");
        url.searchParams.set("origin", window.location.origin);
        url.searchParams.set("launchId", launchId);

        const attempt = handoffAttemptRef.current + 1;
        handoffAttemptRef.current = attempt;
        setHandoffError(null);
        setHelperError(null);
        setHandoffUrl("");
        setHelperLaunching(true);
        setHandoffMessage("Waiting for the local helper to attach to Chrome. If your browser asks, allow it to open GameHub Helper.");
        window.location.href = url.toString();

        try {
            const launchedHandoffUrl = await waitForHelperLaunchStatus("psn", launchId, attempt, handoffAttemptRef);
            if (handoffAttemptRef.current !== attempt) return;

            setHandoffUrl(launchedHandoffUrl);
            setHandoffMessage("Local helper attached. Waiting for PlayStation session cookies from Chrome…");
            await pollHandoffUrl(launchedHandoffUrl);
        } catch (caught) {
            if (handoffAttemptRef.current === attempt) {
                setHandoffError(localLaunchErrorMessage(caught));
                setHandoffMessage(null);
            }
        } finally {
            if (handoffAttemptRef.current === attempt) {
                setHelperLaunching(false);
            }
        }
    };

    async function pollHandoffUrl(candidateUrl = handoffUrl) {
        const parsedUrl = parseHandoffUrl(candidateUrl, "psn", window.location.origin);
        if (parsedUrl instanceof Error) {
            setHandoffError(parsedUrl.message);
            return;
        }

        const attempt = handoffAttemptRef.current + 1;
        handoffAttemptRef.current = attempt;
        setHandoffPolling(true);
        setHelperLaunching(false);
        setHandoffError(null);
        setHelperError(null);
        setHandoffMessage("Waiting for the local helper to capture your PlayStation session from Chrome…");

        try {
            const deadline = Date.now() + 10 * 60 * 1000;
            while (Date.now() < deadline && handoffAttemptRef.current === attempt) {
                const response = await fetch(parsedUrl, { cache: "no-store" });
                if (handoffAttemptRef.current !== attempt) return;

                if (response.status === 202) {
                    setHandoffMessage("Helper is still waiting for Chrome to expose PlayStation session cookies…");
                    await delay(1500);
                    continue;
                }

                if (response.status === 410) {
                    throw new Error("This local helper result was already imported. Run the helper again for a fresh handoff URL.");
                }
                if (!response.ok) {
                    throw new Error(await readLocalHelperFailure(response));
                }

                const result = parsePsnConnectObject(await response.json());
                if (result instanceof Error) {
                    throw result;
                }
                if (result.expired) {
                    throw new Error("The PlayStation session is already expired. Reconnect PlayStation and try again.");
                }
                if (result.provider !== "psn") {
                    throw new Error("This helper result is not for PlayStation.");
                }

                setHandoffMessage("PlayStation session captured. Storing it in the encrypted vault…");
                if (await onSave({ cookies: result.cookies })) {
                    setHandoffUrl("");
                    setHelperPayload("");
                    setHandoffMessage("PlayStation session imported from the local helper.");
                    return;
                }

                setCookies(cookieMapToRows(result.cookies));
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
    }

    const stopHandoffPolling = () => {
        handoffAttemptRef.current += 1;
        setHandoffPolling(false);
        setHelperLaunching(false);
        setHandoffMessage(null);
    };

    return (
        <CredentialCard
            platform="Psn"
            description="Existing Chrome handoff with manual cookie fallback"
            status={status}
            busy={busy}
            onDelete={onDelete}
        >
            <CredentialSection
                icon={<PlugZap className="h-4 w-4 text-primary" />}
                title={status?.isConfigured ? "Reconnect PlayStation" : "Connect PlayStation"}
                description="Launch the local helper to open or attach to the persistent GameHub Chrome profile and import the one-time handoff automatically."
                tone="primary"
            >
                <p className="text-xs leading-5 text-muted-foreground">
                    GameHub never asks for your PSN password. Cookies stay in a local one-time
                    handoff before encrypted vault storage; <span className="font-medium text-foreground">pdccws_p</span> is the relevant cookie.
                </p>
                <Button
                    variant="default"
                    size="sm"
                    className="w-full"
                    disabled={busy || handoffPolling || helperLaunching}
                    onClick={() => void launchProtocolHelper()}
                >
                    {helperLaunching ? (
                        <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                    ) : (
                        <PlugZap className="mr-2 h-4 w-4" />
                    )}
                    {helperLaunching ? "Starting helper…" : "Connect PlayStation"}
                </Button>
                <HelperStatusMessage message={handoffMessage} error={handoffError} />
                {(handoffPolling || helperLaunching) && (
                    <Button
                        variant="ghost"
                        size="sm"
                        className="w-full"
                        onClick={stopHandoffPolling}
                    >
                        Stop waiting
                    </Button>
                )}
                <CommandFallback
                    title="Advanced helper options"
                    note="Use diagnostics only when the normal Chrome handoff cannot complete; fresh PlayStation sign-ins can be provider-fragile."
                    commands={[
                        'cd services/cli && go build -o gamehub-cli . && ./gamehub-cli helper install-protocol-handler --cli-path "$PWD/gamehub-cli"',
                        "/Applications/Google\\ Chrome.app/Contents/MacOS/Google\\ Chrome --remote-debugging-address=127.0.0.1 --remote-debugging-port=49333 --user-data-dir=$HOME/Library/Caches/GameHub/psn-chrome-profile https://store.playstation.com/",
                        `cd services/cli && go run . connect psn --remote-debugging-url http://127.0.0.1:49333 --handoff-server --origin ${pageOrigin}`,
                        `cd services/cli && go run . connect psn --handoff-server --origin ${pageOrigin} --diagnostics-file /tmp/gamehub-psn-login.jsonl`,
                    ]}
                >
                    <Input
                        aria-label="PlayStation local helper handoff URL"
                        className="font-mono text-xs"
                        placeholder="Paste local helper handoff URL"
                        value={handoffUrl}
                        onChange={(event) => {
                            setHandoffUrl(event.target.value);
                            setHandoffError(null);
                        }}
                        disabled={handoffPolling || helperLaunching}
                    />
                    <Button
                        variant="outline"
                        size="sm"
                        className="w-full"
                        disabled={busy || handoffPolling || helperLaunching || !handoffUrl.trim()}
                        onClick={() => void pollHandoffUrl()}
                    >
                        {handoffPolling ? (
                            <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                        ) : (
                            <PlugZap className="mr-2 h-4 w-4" />
                        )}
                        {handoffPolling ? "Waiting for helper…" : "Import pasted handoff URL"}
                    </Button>
                </CommandFallback>
            </CredentialSection>

            <HiddenFallback
                title="JSON fallback"
                description="If loopback handoff is unavailable, paste the helper's one-time JSON result here."
            >
                <Textarea
                    aria-label="PlayStation helper result"
                    className="min-h-24 font-mono text-xs"
                    placeholder='{"provider":"psn","cookies":{"pdccws_p":"..."}}'
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
                            ? "Store refreshed PlayStation session"
                            : "Store JSON helper result"}
                </Button>
            </HiddenFallback>

            <HiddenFallback
                title="Manual fallback"
                description="Add only the cookies needed for the vault; pdccws_p is required for PSN import."
                action={
                    <Button
                        variant="ghost"
                        size="sm"
                        className="h-7 px-2 text-xs"
                        onClick={() => {
                            window.open("https://store.playstation.com/", "_blank", "noopener,noreferrer");
                        }}
                    >
                        <ExternalLink className="mr-1.5 h-3.5 w-3.5" />
                        Open PlayStation
                    </Button>
                }
            >
                {cookies.map((cookie, index) => (
                    <div key={index} className="grid gap-2 sm:grid-cols-[1fr_1.4fr_auto]">
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
            </HiddenFallback>
        </CredentialCard>
    );
}

function CredentialSection({
    icon,
    title,
    description,
    action,
    tone = "muted",
    children,
}: {
    icon: ReactNode;
    title: string;
    description: string;
    action?: ReactNode;
    tone?: "muted" | "primary";
    children: ReactNode;
}) {
    return (
        <section
            className={`rounded-lg border p-3 ${
                tone === "primary" ? "bg-primary/5" : "bg-muted/20"
            }`}
        >
            <div className="flex items-start gap-3">
                <div className="mt-0.5 shrink-0">{icon}</div>
                <div className="min-w-0 flex-1 space-y-3">
                    <div className="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
                        <div>
                            <h4 className="text-sm font-medium text-foreground">{title}</h4>
                            <p className="mt-1 text-xs leading-5 text-muted-foreground">
                                {description}
                            </p>
                        </div>
                        {action}
                    </div>
                    {children}
                </div>
            </div>
        </section>
    );
}

function HelperStatusMessage({
    message,
    error,
}: {
    message: string | null;
    error: string | null;
}) {
    if (!message && !error) return null;

    return (
        <p
            className={`rounded-md border px-3 py-2 text-xs ${
                error
                    ? "border-destructive/30 bg-destructive/10 text-destructive"
                    : "border-primary/30 bg-primary/10 text-primary"
            }`}
        >
            {error ?? message}
        </p>
    );
}

function HiddenFallback({
    title,
    description,
    action,
    children,
}: {
    title: string;
    description: string;
    action?: ReactNode;
    children: ReactNode;
}) {
    return (
        <details className="rounded-lg border bg-muted/10 px-3 py-2 text-xs">
            <summary className="cursor-pointer list-none">
                <div className="inline-flex w-full items-start justify-between gap-3">
                    <div className="min-w-0">
                        <span className="font-medium text-muted-foreground">{title}</span>
                        <p className="mt-0.5 leading-5 text-muted-foreground/80">{description}</p>
                    </div>
                    <span className="shrink-0 text-muted-foreground/70">Show</span>
                </div>
            </summary>
            <div className="mt-3 space-y-3">
                {action}
                {children}
            </div>
        </details>
    );
}

function CommandFallback({
    title,
    note,
    commands,
    children,
}: {
    title: string;
    note?: string;
    commands: string[];
    children?: ReactNode;
}) {
    return (
        <details className="rounded-md border bg-background/70 px-3 py-2 text-xs">
            <summary className="cursor-pointer text-muted-foreground">
                {title}
            </summary>
            <div className="mt-2 space-y-2">
                {children}
                {note && (
                    <p className="text-muted-foreground">
                        {note}
                    </p>
                )}
                {commands.map((command) => (
                    <code
                        key={command}
                        className="block break-all rounded bg-muted px-2 py-1.5 font-mono text-[11px] text-foreground"
                    >
                        {command}
                    </code>
                ))}
            </div>
        </details>
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

async function waitForHelperLaunchStatus(
    provider: ConnectProvider,
    launchId: string,
    attempt: number,
    handoffAttemptRef: RefObject<number>,
): Promise<string> {
    const deadline = Date.now() + 45 * 1000;
    const statusUrl = new URL(`${helperLaunchStatusBaseUrl}/connect/${provider}/launch-status`);
    statusUrl.searchParams.set("launchId", launchId);
    statusUrl.searchParams.set("origin", window.location.origin);
    let reachedHelper = false;

    while (Date.now() < deadline && handoffAttemptRef.current === attempt) {
        let response: Response;
        try {
            response = await fetch(statusUrl, { cache: "no-store" });
        } catch {
            await delay(1000);
            continue;
        }
        reachedHelper = true;

        if (response.status === 202) {
            await delay(1000);
            continue;
        }
        if (!response.ok) {
            throw new Error(await readLocalHelperFailure(response));
        }

        const parsed = parseHelperLaunchStatus(await response.json());
        if (parsed instanceof Error) {
            throw parsed;
        }
        if (parsed.status === "failed") {
            throw new Error(parsed.message ?? "The local helper could not start.");
        }
        if (parsed.status === "handoff_ready" && parsed.handoffUrl) {
            return parsed.handoffUrl;
        }

        await delay(1000);
    }

    throw new Error(reachedHelper
        ? "The local helper started but did not publish its handoff URL. Reinstall the protocol handler from a freshly built gamehub-cli binary and try again."
        : "Timed out waiting for the local helper to start.");
}

function errorMessage(error: unknown): string {
    return error instanceof Error ? error.message : "The credential request failed.";
}

function localLaunchErrorMessage(error: unknown): string {
    const message = errorMessage(error);
    if (message.toLowerCase().includes("timed out")) {
        return "GameHub could not reach the local helper. Install or reinstall the protocol handler, allow the browser prompt to open GameHub Helper, or use the fallback command below.";
    }
    return message;
}

function localHandoffErrorMessage(error: unknown): string {
    const message = errorMessage(error);
    if (message.toLowerCase() === "failed to fetch") {
        return "The browser could not read the local helper. Make sure the helper is still running, the handoff URL has not expired, and the helper --origin matches this Admin page origin.";
    }
    return message;
}

async function readLocalHelperFailure(response: Response): Promise<string> {
    try {
        const body = await response.json() as { message?: string; status?: string };
        if (body.message) return body.message;
        if (body.status === "failed") return "The local helper reported a provider sign-in failure.";
    } catch {
        // Fall through to the generic status message when the helper response is not JSON.
    }
    return `The local helper returned status ${response.status}.`;
}

function delay(milliseconds: number): Promise<void> {
    return new Promise((resolve) => window.setTimeout(resolve, milliseconds));
}

function newHelperLaunchId(): string {
    if (typeof crypto.randomUUID === "function") {
        return crypto.randomUUID();
    }

    const bytes = new Uint8Array(16);
    crypto.getRandomValues(bytes);
    return Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join("");
}

function cookieMapToRows(cookies: Record<string, string>): { key: string; value: string }[] {
    const rows = Object.entries(cookies).map(([key, value]) => ({ key, value }));
    return rows.length > 0 ? rows : [{ key: "", value: "" }];
}

function platformLabel(platform: Platform): string {
    if (platform === "Gog") return "GOG";
    if (platform === "Psn") return "PlayStation";
    return platform;
}
