import type { Metadata } from "next";
import { McpConsentForm } from "@/components/McpConsentForm";
import { getMcpConsentConfig } from "@/lib/mcp-consent";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

export const metadata: Metadata = { title: "Connect an assistant — GameHub", robots: { index: false, follow: false } };
export const dynamic = "force-dynamic";

/** Code entry deliberately uses no query parameter, so sign-in redirects cannot carry a code. */
export default function McpVerifyPage() {
    let available = false;
    try { getMcpConsentConfig(); available = true; } catch { /* Generic operator-owned unavailable state. */ }
    return <section aria-labelledby="mcp-consent-title" className="mx-auto w-full max-w-xl px-4 py-10 md:py-16">
        <Card>
            <CardHeader>
                <CardTitle><h1 id="mcp-consent-title" className="text-2xl">Connect an assistant</h1></CardTitle>
                <CardDescription>Review access to your GameHub library.</CardDescription>
            </CardHeader>
            <CardContent>{available ? <McpConsentForm /> : <p role="status">
                Assistant connections are unavailable on this instance. Contact the instance owner, then try again.
            </p>}</CardContent>
        </Card>
    </section>;
}
