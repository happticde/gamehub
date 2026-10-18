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
import { HelperSetupProvider, PlatformConnectAssistant } from "@/components/admin/PlatformConnectAssistant";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { fetchAuthenticated } from "@/lib/authenticated-fetch";
import { getPlatformColor } from "@/lib/platform-colors";
import {
    allocateHelperStatusPort,
    buildHelperLaunchUrls,
    parseEpicConnectObject,
    parseGogConnectObject,
    parseGogConnectPayload,
    parseHandoffUrl,
    parseEpicAuthorizationCodePayload,
    parseHelperLaunchStatus,
    parsePsnChromeDebugVersion,
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
type ConnectProvider = "gog" | "psn" | "epic";
type HelperStatusKind = "starting" | "browser" | "captured" | "saving" | "saved";
type EpicConnectStart = {
    authorizationUrl: string;
    state: string;
    expiresAtUtc: string;
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
    const [errorPlatform, setErrorPlatform] = useState<Platform | null>(null);

    const loadStatus = useCallback(async () => {
        setLoading(true);
        setError(null);
        setErrorPlatform(null);
        try {
            const response = await fetchAuthenticated("/api/core/platform-credentials", { cache: "no-store" });
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
        setErrorPlatform(null);
        try {
            const response = await fetchAuthenticated(`/api/core/platform-credentials/${platform}`, {
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
            setErrorPlatform(platform);
            return false;
        } finally {
            setBusyPlatform(null);
        }
    };

    const remove = async (platform: Platform) => {
        setBusyPlatform(platform);
        setMessage(null);
        setError(null);
        setErrorPlatform(null);
        try {
            const response = await fetchAuthenticated(`/api/core/platform-credentials/${platform}`, {
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

    const startEpicConnect = async (): Promise<EpicConnectStart | null> => {
        setBusyPlatform("Epic");
        setMessage(null);
        setError(null);
        setErrorPlatform(null);
        try {
            const response = await fetchAuthenticated("/api/platform-credentials/epic/connect/start", {
                method: "POST",
                cache: "no-store",
            });
            if (!response.ok) throw new Error(await readError(response));
            const payload = (await response.json()) as EpicConnectStart;
            setMessage("Epic Games connect session started.");
            return payload;
        } catch (caught) {
            setError(errorMessage(caught));
            setErrorPlatform("Epic");
            return null;
        } finally {
            setBusyPlatform(null);
        }
    };

    const completeEpicConnect = async (
        authorizationCode: string,
        connectState: string,
    ): Promise<boolean> => {
        setBusyPlatform("Epic");
        setMessage(null);
        setError(null);
        setErrorPlatform(null);
        try {
            const response = await fetchAuthenticated("/api/platform-credentials/epic/connect/complete", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ authorizationCode, state: connectState }),
            });
            if (!response.ok) throw new Error(await readError(response));
            setState(await response.json());
            setMessage("Epic Games credential stored securely.");
            return true;
        } catch (caught) {
            setError(errorMessage(caught));
            setErrorPlatform("Epic");
            return false;
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
                                Gaming accounts
                            </CardTitle>
                            <CardDescription className="mt-1">
                                Connect your accounts to import your game libraries. Credentials are stored encrypted.
                            </CardDescription>
                        </div>
                        <Badge variant={state?.platformSyncEnabled ? "default" : "secondary"}>
                            Platform sync {state?.platformSyncEnabled ? "enabled" : "disabled"}
                        </Badge>
                    </div>
                </CardHeader>
                <CardContent className="text-sm text-muted-foreground">
                    Store a credential for at least one platform to enable library sync.
                    Removing the final credential disables sync.
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

            <HelperSetupProvider>
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
                        saveError={errorPlatform === "Gog" ? error : null}
                        onSave={(payload) => save("Gog", payload)}
                        onDelete={() => remove("Gog")}
                    />
                    <PsnCredentialCard
                        status={statusFor("Psn")}
                        busy={busyPlatform === "Psn"}
                        saveError={errorPlatform === "Psn" ? error : null}
                        onSave={(payload) => save("Psn", payload)}
                        onDelete={() => remove("Psn")}
                    />
                    <EpicCredentialCard
                        status={statusFor("Epic")}
                        busy={busyPlatform === "Epic"}
                        saveError={errorPlatform === "Epic" ? error : null}
                        onSave={(payload) => save("Epic", payload)}
                        onStartConnect={startEpicConnect}
                        onCompleteConnect={completeEpicConnect}
                        onDelete={() => remove("Epic")}
                    />
                </div>
            </HelperSetupProvider>
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
    saveError?: string | null;
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

function GogCredentialCard({ status, busy, saveError, onSave, onDelete }: FormProps) {
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
            clearHelperState(helperStatusMessage("gog", "saved"));
        }
    };

    function clearHelperState(successMessage: string | null = null) {
        setHandoffError(null);
        setHelperError(null);
        setHandoffUrl("");
        setHandoffMessage(successMessage);
    }

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
            clearHelperState(helperStatusMessage("gog", "saved"));
        }
    };

    const launchProtocolHelper = async () => {
        const launchId = newHelperLaunchId();
        const { launchUrl, statusUrl } = newHelperLaunchUrls("gog", launchId);

        const attempt = handoffAttemptRef.current + 1;
        handoffAttemptRef.current = attempt;
        setHandoffError(null);
        setHelperError(null);
        setHandoffUrl("");
        setHelperLaunching(true);
        setHandoffMessage(helperStatusMessage("gog", "starting"));
        window.location.href = launchUrl;

        try {
            const launchedHandoffUrl = await waitForHelperLaunchStatus(statusUrl, attempt, handoffAttemptRef);
            if (handoffAttemptRef.current !== attempt) return;

            setHandoffUrl(launchedHandoffUrl);
            setHandoffMessage(helperStatusMessage("gog", "browser"));
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
        setHandoffMessage(helperStatusMessage("gog", "browser"));

        try {
            const deadline = Date.now() + 10 * 60 * 1000;
            while (Date.now() < deadline && handoffAttemptRef.current === attempt) {
                const response = await fetch(parsedUrl, { cache: "no-store" });
                if (handoffAttemptRef.current !== attempt) return;

                if (response.status === 202) {
                    setHandoffMessage(helperStatusMessage("gog", "browser"));
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

                setHandoffMessage(helperStatusMessage("gog", "captured"));
                await delay(250);
                if (handoffAttemptRef.current !== attempt) return;
                setHandoffMessage(helperStatusMessage("gog", "saving"));
                if (await onSave({ cookie: result.cookie, user: result.user })) {
                    setHandoffUrl("");
                    setHelperPayload("");
                    setHandoffError(null);
                    setHandoffMessage(helperStatusMessage("gog", "saved"));
                    return;
                }

                setCookie(result.cookie);
                setUser(result.user);
                setHandoffError(helperStoreFailedMessage("gog", "manual fields"));
                setHandoffMessage(null);
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
            description="Connect your GOG account with GameHub Helper"
            status={status}
            busy={busy}
            onDelete={onDelete}
        >
            <PlatformConnectAssistant
                provider="gog"
                configured={status?.isConfigured ?? false}
                busy={busy}
                waiting={handoffPolling || helperLaunching}
                saved={handoffMessage === helperStatusMessage("gog", "saved")}
                message={handoffMessage}
                error={[saveError, handoffError].filter(Boolean).join(" ") || null}
                onConnect={launchProtocolHelper}
                onReset={() => clearHelperState()}
                onStop={stopHandoffPolling}
            >
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
            </PlatformConnectAssistant>
        </CredentialCard>
    );
}

function PsnCredentialCard({ status, busy, saveError, onSave, onDelete }: FormProps) {
    const [cookies, setCookies] = useState([{ key: "", value: "" }]);
    const [helperPayload, setHelperPayload] = useState("");
    const [handoffUrl, setHandoffUrl] = useState("");
    const [handoffPolling, setHandoffPolling] = useState(false);
    const [helperLaunching, setHelperLaunching] = useState(false);
    const [handoffMessage, setHandoffMessage] = useState<string | null>(null);
    const [handoffError, setHandoffError] = useState<string | null>(null);
    const [helperError, setHelperError] = useState<string | null>(null);
    const [pageOrigin, setPageOrigin] = useState("http://localhost:3005");
    const [chromeDebugStatus, setChromeDebugStatus] = useState<string | null>(null);
    const [chromeDebugError, setChromeDebugError] = useState<string | null>(null);
    const [chromeDebugChecking, setChromeDebugChecking] = useState(false);
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
        if (await onSave({ cookies: payload })) {
            setCookies([{ key: "", value: "" }]);
            clearHelperState(helperStatusMessage("psn", "saved"));
        }
    };

    function clearHelperState(successMessage: string | null = null) {
        setHandoffError(null);
        setHelperError(null);
        setChromeDebugError(null);
        setHandoffUrl("");
        setHandoffMessage(successMessage);
    }

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
            clearHelperState(helperStatusMessage("psn", "saved"));
        }
    };

    const checkPersistentChrome = async () => {
        setChromeDebugStatus(null);
        setChromeDebugError(null);
        setChromeDebugChecking(true);

        try {
            const response = await fetch("http://127.0.0.1:49333/json/version", {
                cache: "no-store",
            });
            if (!response.ok) {
                throw new Error(`Chrome returned status ${response.status}.`);
            }

            const parsed = parsePsnChromeDebugVersion(await response.json());
            if (parsed instanceof Error) {
                throw parsed;
            }

            setChromeDebugStatus(parsed.product
                ? `Persistent Chrome is reachable (${parsed.product}).`
                : "Persistent Chrome is reachable.");
        } catch (caught) {
            setChromeDebugError(`Could not reach persistent Chrome on 127.0.0.1:49333. Start the profile command below and keep that browser open.\nDetails: ${errorMessage(caught)}`);
        } finally {
            setChromeDebugChecking(false);
        }
    };

    const launchProtocolHelper = async () => {
        const launchId = newHelperLaunchId();
        const { launchUrl, statusUrl } = newHelperLaunchUrls("psn", launchId);

        const attempt = handoffAttemptRef.current + 1;
        handoffAttemptRef.current = attempt;
        setHandoffError(null);
        setHelperError(null);
        setHandoffUrl("");
        setHelperLaunching(true);
        setHandoffMessage(helperStatusMessage("psn", "starting"));
        window.location.href = launchUrl;

        try {
            const launchedHandoffUrl = await waitForHelperLaunchStatus(statusUrl, attempt, handoffAttemptRef);
            if (handoffAttemptRef.current !== attempt) return;

            setHandoffUrl(launchedHandoffUrl);
            setHandoffMessage(helperStatusMessage("psn", "browser"));
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
        setHandoffMessage(helperStatusMessage("psn", "browser"));

        try {
            const deadline = Date.now() + 10 * 60 * 1000;
            while (Date.now() < deadline && handoffAttemptRef.current === attempt) {
                const response = await fetch(parsedUrl, { cache: "no-store" });
                if (handoffAttemptRef.current !== attempt) return;

                if (response.status === 202) {
                    setHandoffMessage(helperStatusMessage("psn", "browser"));
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

                setHandoffMessage(helperStatusMessage("psn", "captured"));
                await delay(250);
                if (handoffAttemptRef.current !== attempt) return;
                setHandoffMessage(helperStatusMessage("psn", "saving"));
                if (await onSave({ cookies: result.cookies })) {
                    setHandoffUrl("");
                    setHelperPayload("");
                    setHandoffError(null);
                    setHandoffMessage(helperStatusMessage("psn", "saved"));
                    return;
                }

                setCookies(cookieMapToRows(result.cookies));
                setHandoffError(helperStoreFailedMessage("psn", "manual fields"));
                setHandoffMessage(null);
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
            description="Connect your PlayStation account with GameHub Helper"
            status={status}
            busy={busy}
            onDelete={onDelete}
        >
            <PlatformConnectAssistant
                provider="psn"
                configured={status?.isConfigured ?? false}
                busy={busy}
                waiting={handoffPolling || helperLaunching}
                saved={handoffMessage === helperStatusMessage("psn", "saved")}
                message={handoffMessage}
                error={[saveError, handoffError].filter(Boolean).join(" ") || null}
                onConnect={launchProtocolHelper}
                onReset={() => clearHelperState()}
                onStop={stopHandoffPolling}
            >
                <HiddenFallback
                    title="Manual PlayStation browser connection"
                    description="Use this if the installed helper cannot open or attach to the dedicated Chrome profile."
                >
                    <div className="space-y-2 rounded-md border bg-background/70 p-3 text-xs">
                        <p className="font-medium text-foreground">1. Start or reuse the persistent Chrome profile</p>
                        <code className="block break-all rounded bg-muted px-2 py-1.5 font-mono text-[11px] text-foreground">
                            /Applications/Google\ Chrome.app/Contents/MacOS/Google\ Chrome --remote-debugging-address=127.0.0.1 --remote-debugging-port=49333 --user-data-dir=$HOME/Library/Caches/GameHub/psn-chrome-profile https://store.playstation.com/
                        </code>
                        <p className="text-muted-foreground">
                            Sign in to PlayStation in that Chrome window and keep it open.
                        </p>
                        <Button
                            variant="outline"
                            size="sm"
                            className="w-full"
                            disabled={chromeDebugChecking}
                            onClick={() => void checkPersistentChrome()}
                        >
                            {chromeDebugChecking ? (
                                <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                            ) : (
                                <CheckCircle2 className="mr-2 h-4 w-4" />
                            )}
                            {chromeDebugChecking ? "Checking Chrome…" : "Check persistent Chrome"}
                        </Button>
                        <HelperStatusMessage message={chromeDebugStatus} error={chromeDebugError} />
                    </div>
                    <div className="space-y-2 rounded-md border bg-background/70 p-3 text-xs">
                        <p className="font-medium text-foreground">2. Run the PSN helper against that profile</p>
                        <code className="block break-all rounded bg-muted px-2 py-1.5 font-mono text-[11px] text-foreground">
                            {`cd services/cli && go run . connect psn --remote-debugging-url http://127.0.0.1:49333 --handoff-server --origin ${pageOrigin}`}
                        </code>
                        <p className="text-muted-foreground">
                            Paste the printed handoff URL here.
                        </p>
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
                            {handoffPolling ? "Waiting for helper…" : "Import persistent Chrome handoff"}
                        </Button>
                    </div>
                    <CommandFallback
                        title="Diagnostics"
                        note="The diagnostics file is sanitized by the helper; do not add cookies, headers, query strings, fragments, or tokens to reports."
                        commands={[
                            'cd services/cli && go build -o gamehub-cli . && ./gamehub-cli helper install-protocol-handler --cli-path "$PWD/gamehub-cli"',
                            `cd services/cli && go run . connect psn --handoff-server --origin ${pageOrigin} --diagnostics-file /tmp/gamehub-psn-login.jsonl`,
                        ]}
                    />
                </HiddenFallback>

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
            </PlatformConnectAssistant>
        </CredentialCard>
    );
}

interface EpicFormProps extends FormProps {
    onStartConnect: () => Promise<EpicConnectStart | null>;
    onCompleteConnect: (authorizationCode: string, connectState: string) => Promise<boolean>;
}

function EpicCredentialCard({
    status,
    busy,
    saveError,
    onSave,
    onStartConnect,
    onCompleteConnect,
    onDelete,
}: EpicFormProps) {
    const [epicResult, setEpicResult] = useState("");
    const [manualAuthorizationCode, setManualAuthorizationCode] = useState("");
    const [refreshToken, setRefreshToken] = useState("");
    const [connectState, setConnectState] = useState<string | null>(null);
    const [connectExpiresAt, setConnectExpiresAt] = useState<string | null>(null);
    const [handoffUrl, setHandoffUrl] = useState("");
    const [handoffPolling, setHandoffPolling] = useState(false);
    const [helperLaunching, setHelperLaunching] = useState(false);
    const [handoffMessage, setHandoffMessage] = useState<string | null>(null);
    const [handoffError, setHandoffError] = useState<string | null>(null);
    const [pageOrigin, setPageOrigin] = useState("http://localhost:3005");
    const [localError, setLocalError] = useState<string | null>(null);
    const handoffAttemptRef = useRef(0);

    useEffect(() => () => {
        handoffAttemptRef.current += 1;
    }, []);

    useEffect(() => {
        setPageOrigin(window.location.origin);
    }, []);

    const beginConnectTransaction = async (): Promise<EpicConnectStart | null> => {
        setLocalError(null);
        const started = await onStartConnect();
        if (!started) return null;

        setConnectState(started.state);
        setConnectExpiresAt(started.expiresAtUtc);
        return started;
    };

    const startBrowserConnect = async () => {
        const started = await beginConnectTransaction();
        if (!started) return;
        window.open(started.authorizationUrl, "_blank", "noopener,noreferrer");
    };

    const finishConnect = async () => {
        setLocalError(null);
        if (!connectState) {
            setLocalError("Start the Epic Games connect flow first.");
            return;
        }

        const parsed = parseEpicAuthorizationCodePayload(epicResult);
        if (parsed instanceof Error) {
            setLocalError(parsed.message);
            return;
        }

        if (await onCompleteConnect(parsed.authorizationCode, connectState)) {
            setEpicResult("");
            setConnectState(null);
            setConnectExpiresAt(null);
            clearHelperState(helperStatusMessage("epic", "saved"));
        }
    };

    function clearHelperState(successMessage: string | null = null) {
        setHandoffError(null);
        setLocalError(null);
        setHandoffUrl("");
        setHandoffMessage(successMessage);
    }

    const launchProtocolHelper = async () => {
        const launchId = newHelperLaunchId();
        const { launchUrl, statusUrl } = newHelperLaunchUrls("epic", launchId);

        const attempt = handoffAttemptRef.current + 1;
        handoffAttemptRef.current = attempt;
        setHandoffError(null);
        setHandoffUrl("");
        setHelperLaunching(true);
        setHandoffMessage(helperStatusMessage("epic", "starting"));
        window.location.href = launchUrl;

        try {
            const launchedHandoffUrl = await waitForHelperLaunchStatus(statusUrl, attempt, handoffAttemptRef);
            if (handoffAttemptRef.current !== attempt) return;

            setHandoffUrl(launchedHandoffUrl);
            setHandoffMessage(helperStatusMessage("epic", "browser"));
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
        const parsedUrl = parseHandoffUrl(candidateUrl, "epic", window.location.origin);
        if (parsedUrl instanceof Error) {
            setHandoffError(parsedUrl.message);
            return;
        }

        const attempt = handoffAttemptRef.current + 1;
        handoffAttemptRef.current = attempt;
        setHandoffPolling(true);
        setHelperLaunching(false);
        setHandoffError(null);
        setLocalError(null);
        setHandoffMessage(helperStatusMessage("epic", "browser"));

        try {
            const deadline = Date.now() + 10 * 60 * 1000;
            while (Date.now() < deadline && handoffAttemptRef.current === attempt) {
                const response = await fetch(parsedUrl, { cache: "no-store" });
                if (handoffAttemptRef.current !== attempt) return;

                if (response.status === 202) {
                    setHandoffMessage(helperStatusMessage("epic", "browser"));
                    await delay(1500);
                    continue;
                }

                if (response.status === 410) {
                    throw new Error("This local helper result was already imported. Run the helper again for a fresh handoff URL.");
                }
                if (!response.ok) {
                    throw new Error(await readLocalHelperFailure(response));
                }

                const result = parseEpicConnectObject(await response.json());
                if (result instanceof Error) {
                    throw result;
                }
                if (result.expired) {
                    throw new Error("The Epic Games authorization code is already expired. Reconnect Epic Games and try again.");
                }
                if (result.provider !== "epic") {
                    throw new Error("This helper result is not for Epic Games.");
                }

                setHandoffMessage(helperStatusMessage("epic", "captured"));
                await delay(250);
                if (handoffAttemptRef.current !== attempt) return;
                setHandoffMessage(helperStatusMessage("epic", "saving"));
                if (await onSave({ authorizationCode: result.authorizationCode })) {
                    setHandoffUrl("");
                    setEpicResult("");
                    setConnectState(null);
                    setConnectExpiresAt(null);
                    setHandoffError(null);
                    setHandoffMessage(helperStatusMessage("epic", "saved"));
                    return;
                }

                setManualAuthorizationCode(result.authorizationCode);
                setConnectState(null);
                setConnectExpiresAt(null);
                setHandoffError(helperStoreFailedMessage("epic", "manual authorization code field"));
                setHandoffMessage(null);
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

    const saveAuthorizationCode = async () => {
        if (await onSave({ authorizationCode: manualAuthorizationCode })) {
            setManualAuthorizationCode("");
            clearHelperState(helperStatusMessage("epic", "saved"));
        }
    };

    const saveRefreshToken = async () => {
        if (await onSave({ refreshToken })) {
            setRefreshToken("");
            clearHelperState(helperStatusMessage("epic", "saved"));
        }
    };

    return (
        <CredentialCard
            platform="Epic"
            description="Connect your Epic Games account with GameHub Helper"
            status={status}
            busy={busy}
            onDelete={onDelete}
        >
            <PlatformConnectAssistant
                provider="epic"
                configured={status?.isConfigured ?? false}
                busy={busy}
                waiting={handoffPolling || helperLaunching}
                saved={handoffMessage === helperStatusMessage("epic", "saved")}
                message={handoffMessage}
                error={[saveError, handoffError].filter(Boolean).join(" ") || null}
                onConnect={launchProtocolHelper}
                onReset={() => clearHelperState()}
                onStop={stopHandoffPolling}
            >
                <CommandFallback
                    title="Advanced helper options"
                    commands={[
                        'cd services/cli && go build -o gamehub-cli . && ./gamehub-cli helper install-protocol-handler --cli-path "$PWD/gamehub-cli"',
                        `cd services/cli && go run . connect epic --handoff-server --origin ${pageOrigin}`,
                    ]}
                >
                    <Input
                        aria-label="Epic Games local helper handoff URL"
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

                <HiddenFallback
                    title="JSON fallback"
                    description="If loopback handoff is unavailable, paste the helper or browser one-time JSON result here."
                    action={
                        <Button
                            variant="ghost"
                            size="sm"
                            className="h-7 px-2 text-xs"
                            disabled={busy}
                            onClick={() => void startBrowserConnect()}
                        >
                            {busy ? (
                                <Loader2 className="mr-1.5 h-3.5 w-3.5 animate-spin" />
                            ) : (
                                <ExternalLink className="mr-1.5 h-3.5 w-3.5" />
                            )}
                            Open Epic
                        </Button>
                    }
                >
                    <p className="text-xs leading-5 text-muted-foreground">
                        Epic&apos;s launcher-compatible page returns JSON instead of redirecting back to
                        GameHub. Paste that result here to finish through the BFF.
                    </p>
                    {connectExpiresAt && (
                        <p className="text-xs text-muted-foreground">
                            Connect session expires {new Date(connectExpiresAt).toLocaleTimeString()}.
                        </p>
                    )}
                    {localError && (
                        <p className="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-xs text-destructive">
                            {localError}
                        </p>
                    )}
                    <Textarea
                        aria-label="Epic authorization result"
                        placeholder='Paste Epic JSON, e.g. {"authorizationCode":"..."}'
                        value={epicResult}
                        onChange={(event) => setEpicResult(event.target.value)}
                        className="min-h-24 font-mono text-xs"
                        autoComplete="off"
                    />
                    <Button
                        className="w-full"
                        disabled={busy || !connectState || !epicResult.trim()}
                        onClick={() => void finishConnect()}
                    >
                        {busy ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <PlugZap className="mr-2 h-4 w-4" />}
                        {busy ? "Saving…" : "Finish Epic connect"}
                    </Button>
                </HiddenFallback>

                <HiddenFallback
                    title="Manual fallback"
                    description="Use direct entry only when the helper or JSON fallback cannot be completed."
                >
                    <CredentialInput
                        label="Epic authorizationCode"
                        value={manualAuthorizationCode}
                        onChange={setManualAuthorizationCode}
                        secret
                    />
                    <Button
                        className="w-full"
                        disabled={busy || !manualAuthorizationCode.trim()}
                        onClick={() => void saveAuthorizationCode()}
                    >
                        {busy ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <PlugZap className="mr-2 h-4 w-4" />}
                        {busy ? "Saving…" : "Exchange and store authorization code"}
                    </Button>
                    <CredentialInput
                        label="Epic refresh token"
                        value={refreshToken}
                        onChange={setRefreshToken}
                        secret
                    />
                    <SaveButton
                        busy={busy}
                        disabled={!refreshToken.trim()}
                        onClick={saveRefreshToken}
                    />
                </HiddenFallback>
            </PlatformConnectAssistant>
        </CredentialCard>
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
    const displayedError = error ? splitHelperError(error) : null;

    return (
        <div
            className={`rounded-md border px-3 py-2 text-xs ${
                error
                    ? "border-destructive/30 bg-destructive/10 text-destructive"
                    : "border-primary/30 bg-primary/10 text-primary"
            }`}
        >
            {displayedError ? (
                <div className="space-y-2">
                    <p>{displayedError.message}</p>
                    {displayedError.details && (
                        <details className="text-destructive/80">
                            <summary className="cursor-pointer">Show details</summary>
                            <p className="mt-1 break-words font-mono text-[11px] leading-4">
                                {displayedError.details}
                            </p>
                        </details>
                    )}
                </div>
            ) : (
                <p>{message}</p>
            )}
        </div>
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

function helperStatusMessage(provider: ConnectProvider, status: HelperStatusKind): string {
    const label = connectProviderLabel(provider);
    switch (status) {
        case "starting":
            return "Helper is starting. If your browser asks, allow it to open GameHub Helper.";
        case "browser":
            return `Browser login is running. Complete the ${label} sign-in in the visible browser.`;
        case "captured":
            return "Result captured. Preparing encrypted vault storage.";
        case "saving":
            return "Saving result in the encrypted vault.";
        case "saved":
            return "Credential saved securely. The next library import will verify the provider session.";
    }
}

function helperStoreFailedMessage(provider: ConnectProvider, fallbackTarget: string): string {
    const label = connectProviderLabel(provider);
    return `Saving failed. The ${label} result is still available in the ${fallbackTarget} below so you can retry quickly.`;
}

function connectProviderLabel(provider: ConnectProvider): string {
    if (provider === "gog") return "GOG";
    if (provider === "psn") return "PlayStation";
    return "Epic Games";
}

function splitHelperError(error: string): { message: string; details: string | null } {
    const [message, ...details] = error.split("\nDetails: ");
    return {
        message,
        details: details.length > 0 ? details.join("\nDetails: ") : null,
    };
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
    statusUrl: string,
    attempt: number,
    handoffAttemptRef: RefObject<number>,
): Promise<string> {
    const deadline = Date.now() + 45 * 1000;
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
    if (message.includes("\nDetails: ") || message.startsWith("Another GameHub helper")) return message;
    return `The local helper could not start. Check the helper installation or use the advanced connection options.\nDetails: ${message}`;
}

function localHandoffErrorMessage(error: unknown): string {
    const message = errorMessage(error);
    if (message.toLowerCase() === "failed to fetch") {
        return "The browser could not read the local helper. Make sure the helper is still running, the handoff URL has not expired, and the helper --origin matches this Admin page origin.";
    }
    if (
        message.startsWith("Paste ")
        || message.startsWith("This local helper result")
        || message.startsWith("Timed out ")
        || message.includes("already expired")
        || message.includes("not for ")
        || message.includes("\nDetails: ")
    ) {
        return message;
    }
    return `The local helper could not finish this handoff. Use the manual fallback below to retry.\nDetails: ${message}`;
}

async function readLocalHelperFailure(response: Response): Promise<string> {
    try {
        const body = await response.json() as { message?: string; status?: string };
        if (body.status === "busy") {
            return "Another GameHub helper connection is still running. Finish that connection or install the latest helper download before retrying.";
        }
        if (body.message) {
            return `The local helper reported a provider sign-in failure. Use the manual fallback below to retry.\nDetails: ${body.message}`;
        }
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

function newHelperLaunchUrls(provider: ConnectProvider, launchId: string) {
    const randomValue = crypto.getRandomValues(new Uint16Array(1))[0];
    return buildHelperLaunchUrls(provider, window.location.origin, launchId, allocateHelperStatusPort(randomValue));
}

function cookieMapToRows(cookies: Record<string, string>): { key: string; value: string }[] {
    const rows = Object.entries(cookies).map(([key, value]) => ({ key, value }));
    return rows.length > 0 ? rows : [{ key: "", value: "" }];
}

function platformLabel(platform: Platform): string {
    if (platform === "Gog") return "GOG";
    if (platform === "Psn") return "PlayStation";
    if (platform === "Epic") return "Epic Games";
    return platform;
}
