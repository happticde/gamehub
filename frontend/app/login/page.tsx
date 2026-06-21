import type { Metadata } from "next";
import { Gamepad2, KeyRound, ShieldCheck } from "lucide-react";
import { LoginForm } from "@/components/LoginForm";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { safeReturnPath } from "@/lib/auth-session";
import { isOidcEnabled } from "@/lib/oidc";

export const metadata: Metadata = {
    title: "Sign in — GameHub",
    description: "Sign in to your tenant-isolated GameHub library.",
};

/** Server-rendered login shell; only LoginForm needs browser-side React state. */
export default async function LoginPage({
    searchParams,
}: {
    searchParams: Promise<{ returnTo?: string | string[]; oidcError?: string | string[] }>;
}) {
    const params = await searchParams;
    const requestedPath = typeof params.returnTo === "string" ? params.returnTo : undefined;

    return (
        <div className="relative flex min-h-[calc(100vh-8rem)] items-center justify-center overflow-hidden px-4 py-12">
            <div className="pointer-events-none absolute inset-0 -z-10 overflow-hidden">
                <div className="landing-gradient-orb landing-orb-1" />
                <div className="landing-gradient-orb landing-orb-2" />
            </div>

            <div className="grid w-full max-w-4xl items-center gap-10 md:grid-cols-[1.1fr_0.9fr]">
                <section className="hidden space-y-6 md:block">
                    <div className="inline-flex items-center gap-2 rounded-full border border-border/50 bg-muted/50 px-4 py-1.5 text-sm text-muted-foreground backdrop-blur">
                        <Gamepad2 className="h-4 w-4" />
                        Your collection, your tenant
                    </div>
                    <div>
                        <h1 className="text-4xl font-extrabold tracking-tight lg:text-5xl">
                            Welcome back to <span className="text-primary">GameHub</span>.
                        </h1>
                        <p className="mt-4 max-w-lg text-lg leading-relaxed text-muted-foreground">
                            Sign in to open your unified library, statistics, and administration tools.
                        </p>
                    </div>
                    <div className="grid gap-3 text-sm text-muted-foreground">
                        <div className="flex items-center gap-3">
                            <ShieldCheck className="h-5 w-5 text-primary" />
                            Tenant identity comes only from verified server tokens.
                        </div>
                        <div className="flex items-center gap-3">
                            <KeyRound className="h-5 w-5 text-primary" />
                            Session secrets stay protected in HttpOnly cookies.
                        </div>
                    </div>
                </section>

                <Card className="border-border/60 bg-card/90 shadow-2xl backdrop-blur">
                    <CardHeader className="space-y-2">
                        <div className="mb-2 flex h-11 w-11 items-center justify-center rounded-xl bg-primary/15 text-primary md:hidden">
                            <Gamepad2 className="h-6 w-6" />
                        </div>
                        <CardTitle className="text-2xl">Sign in</CardTitle>
                        <CardDescription>
                            Use the configured identity provider or the local compatibility account.
                        </CardDescription>
                    </CardHeader>
                    <CardContent>
                        <LoginForm
                            returnTo={safeReturnPath(requestedPath)}
                            oidcEnabled={isOidcEnabled()}
                            oidcFailed={params.oidcError === "authentication_failed"}
                        />
                    </CardContent>
                </Card>
            </div>
        </div>
    );
}
