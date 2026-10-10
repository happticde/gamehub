"use client";

import { createContext, useContext, useEffect, useState, type ReactNode } from "react";
import { CheckCircle2, Download, Loader2, PlugZap } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
    Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, DialogTrigger,
} from "@/components/ui/dialog";

type Provider = "gog" | "psn" | "epic";
type SetupStep = "helper" | "signin";
type HelperSetup = {
    ready: boolean;
    setReady: (ready: boolean) => void;
    downloadsAvailable: boolean;
    checkingDownloads: boolean;
};

const HelperSetupContext = createContext<HelperSetup | null>(null);

/** Installation is shared across connectors for this page, without storing account data. */
export function HelperSetupProvider({ children }: { children: ReactNode }) {
    const [ready, setReady] = useState(false);
    const [downloadsAvailable, setDownloadsAvailable] = useState(false);
    const [checkingDownloads, setCheckingDownloads] = useState(true);

    useEffect(() => {
        const controller = new AbortController();
        let disposed = false;
        const timeout = window.setTimeout(() => controller.abort(), 5000);
        // Fixed same-origin files are built alongside the frontend, never external download URLs.
        async function checkDownloads() {
            try {
                const responses = await Promise.all(["arm64", "amd64"].map((architecture) =>
                    fetch(`/downloads/helper/gamehub-helper-macos-${architecture}.tar.gz`, {
                        method: "HEAD", cache: "no-store", signal: controller.signal,
                    })));
                if (!disposed) setDownloadsAvailable(responses.every((response) => response.ok));
            } catch {
                if (!disposed) setDownloadsAvailable(false);
            } finally {
                window.clearTimeout(timeout);
                if (!disposed) setCheckingDownloads(false);
            }
        }
        void checkDownloads();
        return () => {
            disposed = true;
            window.clearTimeout(timeout);
            controller.abort();
        };
    }, []);

    return (
        <HelperSetupContext.Provider value={{ ready, setReady, downloadsAvailable, checkingDownloads }}>
            {children}
        </HelperSetupContext.Provider>
    );
}

const providerCopy: Record<Provider, { label: string; signin: string }> = {
    psn: {
        label: "PlayStation",
        signin: "The helper opens a dedicated Chrome profile for PlayStation and reuses it next time. Sign in directly with Sony in that window.",
    },
    gog: {
        label: "GOG",
        signin: "The helper opens a browser window for GOG. Sign in directly with GOG and complete any verification there.",
    },
    epic: {
        label: "Epic Games",
        signin: "The helper opens a browser window for Epic Games. Sign in directly with Epic and leave the window open until GameHub receives the result.",
    },
};

/** Keeps provider capture/save logic in the existing connector; only presentation is shared. */
export function PlatformConnectAssistant({
    provider, configured, busy, waiting, saved, message, error, onConnect, onReset, onStop, children,
}: {
    provider: Provider;
    configured: boolean;
    busy: boolean;
    waiting: boolean;
    saved: boolean;
    message: string | null;
    error: string | null;
    onConnect: () => Promise<void>;
    onReset: () => void;
    onStop: () => void;
    children: ReactNode;
}) {
    const helper = useContext(HelperSetupContext);
    const [open, setOpen] = useState(false);
    const [step, setStep] = useState<SetupStep>("helper");
    const [architecture, setArchitecture] = useState<"arm64" | "amd64">("arm64");
    if (!helper) throw new Error("PlatformConnectAssistant requires HelperSetupProvider.");
    const setup = helper;
    const copy = providerCopy[provider];
    const currentStep = saved ? "done" : step;
    const active = busy || waiting;

    function changeOpen(nextOpen: boolean) {
        // An in-flight vault write must finish before the dialog is dismissed.
        if (!nextOpen && busy) return;
        if (nextOpen) {
            onReset();
            setStep(setup.ready ? "signin" : "helper");
        } else {
            onStop();
        }
        setOpen(nextOpen);
    }

    function continueToSignin() {
        setup.setReady(true);
        setStep("signin");
    }

    return (
        <Dialog open={open} onOpenChange={changeOpen}>
            <DialogTrigger asChild>
                <Button className="w-full" disabled={busy}>
                    <PlugZap className="mr-2 h-4 w-4" />
                    {configured ? "Reconnect" : "Connect"} {copy.label}
                </Button>
            </DialogTrigger>
            <DialogContent className="max-h-[85dvh] overflow-y-auto sm:max-w-xl" showCloseButton={!busy}>
                <DialogHeader className="pr-8">
                    <DialogTitle>{configured ? "Reconnect" : "Connect"} {copy.label}</DialogTitle>
                    <DialogDescription>Install the helper once, then sign in with your gaming account.</DialogDescription>
                </DialogHeader>
                <ol className="grid grid-cols-3 gap-2 border-y py-3 text-xs" aria-label="Connection progress">
                    {(["helper", "signin", "done"] as const).map((item, index) => (
                        <li key={item} aria-current={currentStep === item ? "step" : undefined}
                            className={currentStep === item ? "font-medium text-primary" : "text-muted-foreground"}>
                            {index + 1}. {item === "helper" ? "Helper" : item === "signin" ? "Sign in" : "Saved"}
                        </li>
                    ))}
                </ol>

                {currentStep === "helper" && (
                    <div className="space-y-4">
                        <div>
                            <h3 className="font-medium">Install GameHub Helper on your Mac</h3>
                            <p className="mt-2 text-sm text-muted-foreground">
                                This small local app connects GameHub to your provider login. Install it once
                                for PlayStation, GOG and Epic Games on the Mac where you use your browser,
                                even if GameHub runs on a server. Google Chrome must be installed.
                            </p>
                        </div>
                        <p className="text-xs text-muted-foreground">
                            This setup currently supports macOS. Windows, Linux, iPhone and iPad setup will follow later.
                        </p>
                        {helper.checkingDownloads ? (
                            <p role="status" className="text-sm text-muted-foreground">Checking helper downloads…</p>
                        ) : helper.downloadsAvailable ? (
                            <div className="space-y-2">
                                <label className="block text-sm">
                                    Mac processor
                                    <select className="mt-1 w-full rounded-md border bg-background p-2"
                                        value={architecture} onChange={(event) => setArchitecture(event.target.value === "amd64" ? "amd64" : "arm64")}>
                                        <option value="arm64">Apple Silicon (M series)</option>
                                        <option value="amd64">Intel</option>
                                    </select>
                                </label>
                                <p className="text-xs text-muted-foreground">Find your processor in Apple menu → About This Mac.</p>
                                <Button asChild variant="outline" className="w-full">
                                    <a href={`/downloads/helper/gamehub-helper-macos-${architecture}.tar.gz`} download>
                                        <Download className="mr-2 h-4 w-4" /> Download for macOS
                                    </a>
                                </Button>
                            </div>
                        ) : (
                            <p role="status" className="rounded-md border p-3 text-sm text-muted-foreground">
                                Helper downloads are unavailable on this deployment. Ask your operator to
                                build the frontend with its helper packages, or use the advanced setup below.
                            </p>
                        )}
                        <ol className="list-decimal space-y-2 pl-5 text-sm">
                            <li>Extract the download.</li>
                            <li>Open <span className="font-medium">Install GameHub Helper.command</span> and wait for the installation to finish.</li>
                            <li>Return here and continue. You can delete the extracted download after installation.</li>
                        </ol>
                        <p className="text-xs text-muted-foreground">
                            The packages are unsigned and not notarized. macOS may require your approval to open them.
                        </p>
                        <div className="flex flex-wrap gap-2">
                            <Button onClick={continueToSignin}>Continue after installation</Button>
                            <Button variant="ghost" onClick={continueToSignin}>Already installed</Button>
                        </div>
                    </div>
                )}

                {currentStep === "signin" && (
                    <div className="space-y-4">
                        <h3 className="font-medium">Sign in directly with {copy.label}</h3>
                        <p className="text-sm text-muted-foreground">{copy.signin}</p>
                        <p className="text-sm text-muted-foreground">
                            Allow your browser to open GameHub Helper when prompted. Your password is
                            entered only on the provider website. GameHub saves the captured session automatically.
                        </p>
                        <Button className="w-full" disabled={active} onClick={() => void onConnect()}>
                            {active ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <PlugZap className="mr-2 h-4 w-4" />}
                            {active ? "Connecting…" : `Open Helper & sign in with ${copy.label}`}
                        </Button>
                        {waiting && <Button variant="ghost" disabled={busy} onClick={onStop}>Stop waiting</Button>}
                        <Button variant="link" disabled={active} onClick={() => setStep("helper")}>Helper installation</Button>
                    </div>
                )}

                {currentStep === "done" && (
                    <div className="space-y-4">
                        <h3 className="flex items-center gap-2 font-medium"><CheckCircle2 className="h-5 w-5 text-primary" /> {copy.label} credential saved</h3>
                        <p className="text-sm text-muted-foreground">
                            Your session is stored encrypted in GameHub. The next library import will check
                            whether the provider accepts it. You can close the helper now.
                        </p>
                        <Button onClick={() => changeOpen(false)}>Done</Button>
                    </div>
                )}

                {!saved && (message || error) && (
                    <div role={error ? "alert" : "status"} aria-live="polite"
                        className={`rounded-md border p-3 text-sm ${error ? "text-destructive" : "text-muted-foreground"}`}>
                        <p>{(error ?? message)?.split("\nDetails: ")[0]}</p>
                        {error?.includes("\nDetails: ") && (
                            <details className="mt-2"><summary className="cursor-pointer">Technical details</summary>
                                <p className="mt-2 break-words text-xs">{error.split("\nDetails: ").slice(1).join("\nDetails: ")}</p>
                            </details>
                        )}
                    </div>
                )}
                {!saved && (
                    <details className="border-t pt-3" open={Boolean(error)}>
                        <summary className="cursor-pointer text-sm text-muted-foreground">Having trouble? Advanced connection options</summary>
                        <div className="mt-4 space-y-4">{children}</div>
                    </details>
                )}
            </DialogContent>
        </Dialog>
    );
}
