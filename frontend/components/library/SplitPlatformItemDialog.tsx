"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { AlertCircle, Loader2, Unlink } from "lucide-react";
import {
    AlertDialog,
    AlertDialogCancel,
    AlertDialogContent,
    AlertDialogDescription,
    AlertDialogFooter,
    AlertDialogHeader,
    AlertDialogTitle,
    AlertDialogTrigger,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import { getErrorMessage } from "@/lib/errors";
import type { ApiMessageResponse } from "@/types/api";
import type { Platform } from "@/types/game";

interface SplitPlatformItemDialogProps {
    libraryItemId: number;
    platform: Platform;
    externalId: string;
}

interface SplitGameResponse extends ApiMessageResponse {
    newGameId?: number;
}

export default function SplitPlatformItemDialog({
    libraryItemId,
    platform,
    externalId,
}: SplitPlatformItemDialogProps) {
    const router = useRouter();
    const [open, setOpen] = useState(false);
    const [isSplitting, setIsSplitting] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const handleOpenChange = (nextOpen: boolean) => {
        if (isSplitting) return;

        setOpen(nextOpen);
        if (nextOpen) setError(null);
    };

    const handleSplit = async () => {
        if (isSplitting) return;

        setIsSplitting(true);
        setError(null);

        try {
            const response = await fetch("/api/core/library/split", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ libraryItemId }),
            });
            const data = await response.json().catch(() => null) as SplitGameResponse | null;

            if (!response.ok) {
                throw new Error(
                    data?.message ?? data?.Message ?? "The platform entry could not be split.",
                );
            }

            if (!data?.newGameId) {
                setOpen(false);
                router.refresh();
                return;
            }

            setOpen(false);
            router.push(`/library/${data.newGameId}`);
            router.refresh();
        } catch (caughtError: unknown) {
            setError(getErrorMessage(caughtError, "The platform entry could not be split."));
            setIsSplitting(false);
        }
    };

    return (
        <AlertDialog open={open} onOpenChange={handleOpenChange}>
            <AlertDialogTrigger asChild>
                <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    className="h-8 shrink-0 gap-1.5"
                    disabled={isSplitting}
                    aria-label={`Split ${platform} entry ${externalId} into a separate game`}
                >
                    <Unlink />
                    Split
                </Button>
            </AlertDialogTrigger>
            <AlertDialogContent className="w-[calc(100%-2rem)] sm:max-w-lg">
                <AlertDialogHeader>
                    <AlertDialogTitle>Split this platform entry?</AlertDialogTitle>
                    <AlertDialogDescription>
                        This entry will move into a new game. The other platform entries will stay
                        here, and future syncs will keep this entry separate.
                    </AlertDialogDescription>
                </AlertDialogHeader>

                <div className="rounded-lg border border-border bg-muted/40 p-3 text-sm">
                    <div className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1">
                        <span className="text-muted-foreground">Platform</span>
                        <span className="font-medium">{platform}</span>
                        <span className="text-muted-foreground">External ID</span>
                        <span className="break-all font-mono text-xs">{externalId}</span>
                    </div>
                </div>

                {error && (
                    <div
                        className="flex items-start gap-2 rounded-lg border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive"
                        role="alert"
                    >
                        <AlertCircle className="mt-0.5 shrink-0" />
                        <span>{error}</span>
                    </div>
                )}

                <AlertDialogFooter>
                    <AlertDialogCancel disabled={isSplitting}>Cancel</AlertDialogCancel>
                    <Button
                        type="button"
                        variant="destructive"
                        onClick={() => void handleSplit()}
                        disabled={isSplitting}
                    >
                        {isSplitting ? <Loader2 className="animate-spin" /> : <Unlink />}
                        {isSplitting ? "Splitting…" : "Split into new game"}
                    </Button>
                </AlertDialogFooter>
            </AlertDialogContent>
        </AlertDialog>
    );
}
