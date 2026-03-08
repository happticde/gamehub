"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { RefreshCw } from "lucide-react";

export function SyncTriggerButton() {
    const router = useRouter();
    const [isPending, startTransition] = useTransition();
    const [isSyncing, setIsSyncing] = useState(false);
    const [resultMsg, setResultMsg] = useState("");

    const handleSync = async () => {
        setIsSyncing(true);
        setResultMsg("");

        try {
            // Because this is a Client Component, we hit the Next.js API proxy route defined in next.config.ts
            // /api/core/ -> http://localhost:5001/api/
            const res = await fetch("/api/core/library/sync", {
                method: "POST",
            });

            if (!res.ok) {
                setResultMsg(`Sync failed with status: ${res.status}`);
            } else {
                const data = await res.json();
                setResultMsg(data.Message || "Sync completed successfully.");

                // Refresh the server component to pull the fresh sync-history
                startTransition(() => {
                    router.refresh();
                });
            }
        } catch (error: any) {
            setResultMsg(`Error: ${error.message}`);
        } finally {
            setIsSyncing(false);
        }
    };

    return (
        <div className="flex flex-col items-start gap-3">
            <Button
                onClick={handleSync}
                disabled={isSyncing || isPending}
                size="lg"
            >
                <RefreshCw className={`mr-2 h-5 w-5 ${isSyncing || isPending ? "animate-spin" : ""}`} />
                {isSyncing ? "Running Unification Sync..." : "Sync Library Now"}
            </Button>

            {resultMsg && (
                <p className="text-sm text-muted-foreground animate-in fade-in duration-300">
                    {resultMsg}
                </p>
            )}
        </div>
    );
}
