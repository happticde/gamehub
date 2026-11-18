"use client";

import { useEffect, useRef, useState } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { McpConsentPermissions } from "@/components/McpConsentPermissions";
import { fetchAuthenticated } from "@/lib/authenticated-fetch";
import type { McpConsentInput, McpConsentPreview } from "@/lib/mcp-consent-contract";

/** The browser sees only a display snapshot and opaque confirmation, never a session credential. */
export function McpConsentForm() {
    const [code, setCode] = useState("");
    const [preview, setPreview] = useState<McpConsentPreview | null>(null);
    const [tracking, setTracking] = useState(false);
    const [pending, setPending] = useState(false);
    const [error, setError] = useState("");
    const [completed, setCompleted] = useState<"approve" | "deny" | null>(null);
    const busy = useRef(false);
    const errorRef = useRef<HTMLParagraphElement>(null);
    const reviewRef = useRef<HTMLHeadingElement>(null);
    const resultRef = useRef<HTMLHeadingElement>(null);

    // Move focus after React commits the new step, so keyboard users reach its heading/error.
    useEffect(() => {
        if (error) errorRef.current?.focus();
        else if (preview) reviewRef.current?.focus();
        else if (completed) resultRef.current?.focus();
    }, [error, preview, completed]);

    async function submit(action: McpConsentInput["action"]) {
        if (busy.current) return;
        busy.current = true;
        setPending(true);
        setError("");
        try {
            if (!code.trim()) throw new Error("Enter the code shown by the assistant.");
            const input: McpConsentInput = action === "preview"
                ? { action, userCode: code.trim() }
                : { action, userCode: code.trim(), confirmation: preview!.confirmation, allowTrackingWrite: tracking };
            const response = await fetchAuthenticated("/api/mcp/consent", {
                method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(input),
                signal: AbortSignal.timeout(15_000),
            });
            if (!response.ok) {
                if (response.status === 403) throw new Error("Sign in with the configured identity provider to review this connection. A local password session cannot approve access.");
                if (response.status === 429) throw new Error("Too many attempts. Wait a minute, then review the code again.");
                if (response.status === 400) throw new Error("This code or review has expired or already been used. Check the code or request a new one from the assistant.");
                throw new Error("The result could not be confirmed. Review the code again before making another decision.");
            }
            if (action === "preview") {
                setPreview(await response.json() as McpConsentPreview);
                setTracking(false);
            } else { setCompleted(action); setPreview(null); setCode(""); }
        } catch (failure) {
            setPreview(null);
            setError(failure instanceof Error && failure.name !== "TimeoutError" && failure.name !== "TypeError"
                ? failure.message : "The result could not be confirmed. Review the code again before making another decision.");
        } finally { busy.current = false; setPending(false); }
    }

    if (completed) return <div className="space-y-4" role="status">
        <h2 ref={resultRef} tabIndex={-1} className="font-semibold">{completed === "approve" ? "Connection approved" : "Connection denied"}</h2>
        <p>{completed === "approve" ? "Return to the assistant to finish connecting." : "The assistant has not been given access."}</p>
        <Button variant="outline" onClick={() => setCompleted(null)}>Review another code</Button>
    </div>;

    return <form noValidate className="space-y-6" aria-busy={pending} onSubmit={event => { event.preventDefault(); void submit(preview ? "approve" : "preview"); }}>
        {error && <p ref={errorRef} tabIndex={-1} role="alert" id="consent-error" className="rounded-md border border-destructive/50 p-3 text-sm text-destructive">{error}</p>}
        {!preview ? <div className="space-y-3">
            <label htmlFor="device-code" className="text-sm font-medium">Code from the assistant</label>
            <Input id="device-code" value={code} maxLength={32} autoComplete="off" spellCheck={false}
                autoCapitalize="characters" className="font-mono text-lg tracking-widest" disabled={pending}
                aria-invalid={!!error} aria-describedby={error ? "code-help consent-error" : "code-help"}
                onChange={event => { setCode(event.target.value); setError(""); }} />
            <p id="code-help" className="text-sm text-muted-foreground">Only enter a code for a connection you started. Never approve a code sent by someone else.</p>
            <Button type="submit" disabled={pending} className="min-w-36">{pending ? "Reviewing…" : "Review access"}</Button>
        </div> : <>
            <div className="space-y-3">
                <h2 ref={reviewRef} tabIndex={-1} className="text-lg font-semibold">Review this connection</h2>
                <dl className="space-y-2 text-sm">
                    <div><dt className="text-muted-foreground">Instance</dt><dd className="break-all">{preview.instance}</dd></div>
                    <div><dt className="text-muted-foreground">Signed-in account</dt><dd className="break-words">{preview.account}</dd><dd className="break-all font-mono text-xs text-muted-foreground">{preview.accountReference}</dd></div>
                    <div><dt className="text-muted-foreground">Code</dt><dd className="font-mono text-lg tracking-widest">{code}</dd></div>
                </dl>
                <p className="text-sm text-muted-foreground">The assistant identifies itself as GameHub MCP. This public name does not prove which app or device is connecting.</p>
            </div>
            <McpConsentPermissions trackingWriteRequested={preview.trackingWriteRequested}
                renewalRequested={preview.renewalRequested} disabled={pending} tracking={tracking}
                onTrackingChange={setTracking} />
            <p className="text-sm text-muted-foreground">This review expires in at most five minutes. Approve only if the instance, account and code match your intended connection.</p>
            <div className="flex flex-wrap gap-3">
                <Button type="button" variant="outline" disabled={pending} onClick={() => void submit("deny")}>Deny connection</Button>
                <Button type="submit" disabled={pending} className="w-full sm:w-48">{pending ? "Saving decision…" : "Approve connection"}</Button>
                <Button type="button" variant="ghost" disabled={pending} onClick={() => { setPreview(null); setTracking(false); }}>Change code</Button>
            </div>
        </>}
    </form>;
}
