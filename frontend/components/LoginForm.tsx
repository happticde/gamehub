"use client";

import { FormEvent, useState } from "react";
import { useRouter } from "next/navigation";
import { KeyRound, Loader2, LogIn } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";

interface LoginFormProps {
    returnTo: string;
    oidcEnabled: boolean;
    oidcFailed: boolean;
}

/**
 * Interactive credentials form. React owns only the input/loading/error state; the submitted
 * password goes directly to the same-origin BFF and is never stored in component state after
 * navigation. Access and refresh tokens return as HttpOnly cookies, not JSON visible here.
 */
export function LoginForm({ returnTo, oidcEnabled, oidcFailed }: LoginFormProps) {
    const router = useRouter();
    const [isSubmitting, setIsSubmitting] = useState(false);
    const [error, setError] = useState<string | null>(
        oidcFailed ? "External sign-in failed or expired. Please try again." : null,
    );

    async function handleSubmit(event: FormEvent<HTMLFormElement>) {
        event.preventDefault();
        setIsSubmitting(true);
        setError(null);

        const form = new FormData(event.currentTarget);
        try {
            const response = await fetch("/api/auth/login", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    email: form.get("email"),
                    password: form.get("password"),
                }),
            });

            if (!response.ok) {
                const payload = (await response.json().catch(() => null)) as
                    | { message?: string }
                    | null;
                setError(payload?.message || "Sign in failed. Please try again.");
                return;
            }

            router.replace(returnTo);
            router.refresh();
        } catch {
            setError("GameHub could not reach the authentication service.");
        } finally {
            setIsSubmitting(false);
        }
    }

    return (
        <div className="space-y-5">
            {oidcEnabled && (
                <>
                    <Button asChild className="h-11 w-full">
                        <a href={`/api/auth/oidc/start?returnTo=${encodeURIComponent(returnTo)}`}>
                            <KeyRound aria-hidden="true" />
                            Sign in with identity provider
                        </a>
                    </Button>
                    <div className="flex items-center gap-3 text-xs uppercase tracking-wide text-muted-foreground">
                        <span className="h-px flex-1 bg-border" />
                        Local compatibility
                        <span className="h-px flex-1 bg-border" />
                    </div>
                </>
            )}
            <form onSubmit={handleSubmit} className="space-y-5">
            <div className="space-y-2">
                <label htmlFor="email" className="text-sm font-medium">
                    Email address
                </label>
                <Input
                    id="email"
                    name="email"
                    type="email"
                    autoComplete="email"
                    autoCapitalize="none"
                    spellCheck={false}
                    required
                    disabled={isSubmitting}
                    placeholder="owner@example.com"
                    className="h-11"
                />
            </div>

            <div className="space-y-2">
                <label htmlFor="password" className="text-sm font-medium">
                    Password
                </label>
                <Input
                    id="password"
                    name="password"
                    type="password"
                    autoComplete="current-password"
                    required
                    disabled={isSubmitting}
                    className="h-11"
                />
            </div>

            {error && (
                <div
                    role="alert"
                    className="rounded-lg border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive"
                >
                    {error}
                </div>
            )}

            <Button type="submit" className="h-11 w-full" disabled={isSubmitting}>
                {isSubmitting ? (
                    <Loader2 className="animate-spin" aria-hidden="true" />
                ) : (
                    <LogIn aria-hidden="true" />
                )}
                {isSubmitting ? "Signing in…" : "Sign in"}
            </Button>
            </form>
        </div>
    );
}
