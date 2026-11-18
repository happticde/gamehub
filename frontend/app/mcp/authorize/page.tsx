import type { Metadata } from "next";
import { cookies } from "next/headers";
import { McpConsentPermissions } from "@/components/McpConsentPermissions";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { ACCESS_TOKEN_COOKIE } from "@/lib/auth-session";
import { sendMcpRemoteConsent } from "@/lib/mcp-consent";
import { getManagedMcpConfig } from "@/lib/mcp-managed-config.mjs";
import { parseMcpRemotePreview, parseMcpRemoteRequest } from "@/lib/mcp-remote-contract";

export const metadata: Metadata = {
    title: "Review assistant access — GameHub", robots: { index: false, follow: false }, referrer: "no-referrer",
};
export const dynamic = "force-dynamic";

async function loadReview(params: Record<string, string | string[] | undefined>) {
    const request = parseMcpRemoteRequest(params.client_id, params.request_uri);
    const config = getManagedMcpConfig();
    const token = (await cookies()).get(ACCESS_TOKEN_COOKIE)?.value;
    if (!config || !token) return null;
    const upstream = await sendMcpRemoteConsent(request, { action: "preview" }, token);
    if (!upstream.ok) return null;
    return { request, preview: parseMcpRemotePreview(await upstream.json(), config.instance, request.clientId) };
}

export default async function McpAuthorizePage({ searchParams }: {
    searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
    const params = await searchParams;
    const review = await loadReview(params).catch(() => null);
    return <section aria-labelledby="remote-consent-title" className="mx-auto w-full max-w-xl px-4 py-10 md:py-16">
        <Card>
            <CardHeader>
                <CardTitle><h1 id="remote-consent-title" className="text-2xl">Review assistant access</h1></CardTitle>
                <CardDescription>Choose what this connection can do in your GameHub library.</CardDescription>
            </CardHeader>
            <CardContent>
                {review ? <form method="post" action="/api/mcp/authorize" noValidate className="space-y-5">
                    <input type="hidden" name="client_id" value={review.request.clientId} />
                    <input type="hidden" name="request_uri" value={review.request.requestUri} />
                    <input type="hidden" name="confirmation" value={review.preview.confirmation} />
                    {params.result === "uncertain" && <p role="alert" className="rounded-md border border-destructive/50 p-3 text-sm text-destructive">
                        GameHub could not confirm your decision. Review the connection before trying again.
                    </p>}
                    <dl className="space-y-2 text-sm">
                        <div><dt className="text-muted-foreground">Assistant</dt><dd>{review.preview.client}</dd></div>
                        <div><dt className="text-muted-foreground">GameHub address</dt><dd className="break-all">{review.preview.resource}</dd></div>
                        <div><dt className="text-muted-foreground">Signed-in account</dt>
                            <dd className="break-words">{review.preview.account}</dd>
                            <dd className="break-all font-mono text-xs text-muted-foreground">{review.preview.accountReference}</dd>
                        </div>
                    </dl>
                    <p className="text-sm text-muted-foreground">
                        The public client name identifies the software. Anyone can reuse it, so approve only a sign-in you started.
                    </p>
                    <McpConsentPermissions trackingWriteRequested={review.preview.trackingWriteRequested}
                        renewalRequested={review.preview.renewalRequested} />
                    <div className="flex flex-wrap gap-3">
                        <Button type="submit" name="action" value="deny" variant="outline" size="lg">Deny connection</Button>
                        <Button type="submit" name="action" value="approve" size="lg">Approve connection</Button>
                    </div>
                </form> : <p role="status" className="text-sm">
                    This request cannot be reviewed. Sign in with the configured provider and start the connection again from your assistant.
                </p>}
            </CardContent>
        </Card>
    </section>;
}
