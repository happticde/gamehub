"use client";

import { useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { AlertCircle, Disc3, Loader2, Plus, Trash2 } from "lucide-react";
import {
    AlertDialog,
    AlertDialogCancel,
    AlertDialogContent,
    AlertDialogDescription,
    AlertDialogFooter,
    AlertDialogHeader,
    AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
    Dialog,
    DialogContent,
    DialogDescription,
    DialogFooter,
    DialogHeader,
    DialogTitle,
    DialogTrigger,
} from "@/components/ui/dialog";
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from "@/components/ui/select";
import { getErrorMessage } from "@/lib/errors";
import { fetchAuthenticated } from "@/lib/authenticated-fetch";
import type { ApiMessageResponse } from "@/types/api";
import type { PhysicalGameCopy, PhysicalGamePlatform } from "@/types/game";

const PHYSICAL_PLATFORMS: readonly PhysicalGamePlatform[] = ["PS3", "PS4", "PS5"];

interface PhysicalCollectionProps {
    gameId: number;
    initialCopies: PhysicalGameCopy[];
}

export default function PhysicalCollection({ gameId, initialCopies }: PhysicalCollectionProps) {
    const router = useRouter();
    const [copies, setCopies] = useState(initialCopies);
    const [selectedPlatform, setSelectedPlatform] = useState<PhysicalGamePlatform>("PS5");
    const [gtinInput, setGtinInput] = useState("");
    const [addOpen, setAddOpen] = useState(false);
    const [copyToRemove, setCopyToRemove] = useState<PhysicalGameCopy | null>(null);
    const [pendingAction, setPendingAction] = useState<"add" | "remove" | null>(null);
    const requestInFlight = useRef(false);
    const [addError, setAddError] = useState<string | null>(null);
    const [removeError, setRemoveError] = useState<string | null>(null);

    const handleAddOpenChange = (open: boolean) => {
        if (requestInFlight.current) return;
        setAddOpen(open);
        if (open) setAddError(null);
    };

    const handleAdd = async () => {
        if (requestInFlight.current) return;

        requestInFlight.current = true;
        setPendingAction("add");
        setAddError(null);
        try {
            const response = await fetchAuthenticated(`/api/core/library/${gameId}/physical-copies`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    platform: selectedPlatform,
                    gtin: gtinInput.trim() || null,
                }),
            });
            const data = await response.json().catch(() => null) as
                | PhysicalGameCopy
                | ApiMessageResponse
                | null;

            if (!response.ok || !data || !("id" in data)) {
                const errorResponse = data as ApiMessageResponse | null;
                throw new Error(
                    errorResponse?.message
                    ?? errorResponse?.Message
                    ?? "The physical copy could not be added.",
                );
            }

            setCopies((current) => [...current, data].sort(compareCopies));
            setGtinInput("");
            setAddOpen(false);
            router.refresh();
        } catch (caughtError: unknown) {
            setAddError(getErrorMessage(caughtError, "The physical copy could not be added."));
        } finally {
            requestInFlight.current = false;
            setPendingAction(null);
        }
    };

    const handleRemove = async () => {
        if (!copyToRemove || requestInFlight.current) return;

        requestInFlight.current = true;
        setPendingAction("remove");
        setRemoveError(null);
        try {
            const response = await fetchAuthenticated(
                `/api/core/library/${gameId}/physical-copies/${copyToRemove.id}`,
                { method: "DELETE" },
            );
            if (!response.ok) {
                const data = await response.json().catch(() => null) as ApiMessageResponse | null;
                throw new Error(
                    data?.message ?? data?.Message ?? "The physical copy could not be removed.",
                );
            }

            setCopies((current) => current.filter((copy) => copy.id !== copyToRemove.id));
            setCopyToRemove(null);
            router.refresh();
        } catch (caughtError: unknown) {
            setRemoveError(getErrorMessage(caughtError, "The physical copy could not be removed."));
        } finally {
            requestInFlight.current = false;
            setPendingAction(null);
        }
    };

    return (
        <section className="rounded-xl border border-primary/25 bg-card p-6 shadow-sm">
            <div className="mb-4 flex items-start justify-between gap-3 border-b border-border pb-3">
                <div>
                    <h3 className="flex items-center gap-2 text-lg font-semibold">
                        <Disc3 className="h-5 w-5 text-primary" />
                        Physical Collection
                    </h3>
                    <p className="mt-1 text-xs text-muted-foreground">
                        Physical copies are separate from synchronized digital libraries.
                    </p>
                </div>

                <Dialog open={addOpen} onOpenChange={handleAddOpenChange}>
                    <DialogTrigger asChild>
                        <Button type="button" size="sm" disabled={pendingAction !== null}>
                            <Plus /> Add copy
                        </Button>
                    </DialogTrigger>
                    <DialogContent
                        className="w-[calc(100%-2rem)] sm:max-w-sm"
                        showCloseButton={pendingAction === null}
                    >
                        <DialogHeader>
                            <DialogTitle>Add physical copy</DialogTitle>
                            <DialogDescription>
                                Choose the console for this copy. You can add the same game and console more than once.
                            </DialogDescription>
                        </DialogHeader>

                        <div className="space-y-2 py-2">
                            <label className="text-sm font-medium" htmlFor="physical-platform">
                                Physical platform
                            </label>
                            <Select
                                value={selectedPlatform}
                                onValueChange={(value) => setSelectedPlatform(value as PhysicalGamePlatform)}
                                disabled={pendingAction !== null}
                            >
                                <SelectTrigger id="physical-platform" className="h-10 w-full">
                                    <SelectValue />
                                </SelectTrigger>
                                <SelectContent>
                                    {PHYSICAL_PLATFORMS.map((platform) => (
                                        <SelectItem key={platform} value={platform}>{platform}</SelectItem>
                                    ))}
                                </SelectContent>
                            </Select>

                            <label className="block pt-2 text-sm font-medium" htmlFor="physical-gtin">
                                GTIN (optional)
                            </label>
                            <Input
                                id="physical-gtin"
                                className="h-10"
                                inputMode="numeric"
                                autoComplete="off"
                                value={gtinInput}
                                onChange={(event) => setGtinInput(event.target.value)}
                                placeholder="UPC-A, EAN-13, or GTIN-14"
                                disabled={pendingAction !== null}
                            />

                            <div className="rounded-lg border bg-muted/30 p-3 text-sm">
                                <p><span className="text-muted-foreground">Confirm platform:</span> {selectedPlatform}</p>
                                <p className="mt-1 break-all">
                                    <span className="text-muted-foreground">GTIN:</span> {gtinInput.trim() || "None"}
                                </p>
                            </div>
                        </div>

                        {addError && <ErrorMessage message={addError} />}

                        <DialogFooter>
                            <Button
                                type="button"
                                variant="outline"
                                onClick={() => setAddOpen(false)}
                                disabled={pendingAction !== null}
                            >
                                Cancel
                            </Button>
                            <Button
                                type="button"
                                onClick={() => void handleAdd()}
                                disabled={pendingAction !== null}
                            >
                                {pendingAction === "add" ? <Loader2 className="animate-spin" /> : <Plus />}
                                {pendingAction === "add" ? "Adding…" : "Add copy"}
                            </Button>
                        </DialogFooter>
                    </DialogContent>
                </Dialog>
            </div>

            {copies.length === 0 ? (
                <p className="text-sm text-muted-foreground">No physical copies added yet.</p>
            ) : (
                <ul className="space-y-2">
                    {copies.map((copy) => (
                        <li
                            key={copy.id}
                            className="flex items-center justify-between gap-3 rounded-lg border border-border/70 bg-muted/20 p-3"
                        >
                            <div className="min-w-0">
                                <span className="inline-flex rounded-full border border-primary/30 bg-primary/10 px-2.5 py-0.5 text-xs font-semibold text-primary">
                                    {copy.platform}
                                </span>
                                {copy.normalizedGtin && (
                                    <p className="mt-1 font-mono text-xs text-muted-foreground">GTIN {copy.normalizedGtin}</p>
                                )}
                            </div>
                            <Button
                                type="button"
                                variant="ghost"
                                size="icon-sm"
                                disabled={pendingAction !== null}
                                onClick={() => {
                                    setRemoveError(null);
                                    setCopyToRemove(copy);
                                }}
                                aria-label={`Remove ${copy.platform} physical copy`}
                            >
                                <Trash2 />
                            </Button>
                        </li>
                    ))}
                </ul>
            )}

            <AlertDialog
                open={copyToRemove !== null}
                onOpenChange={(open) => {
                    if (!open && !requestInFlight.current) setCopyToRemove(null);
                }}
            >
                <AlertDialogContent className="w-[calc(100%-2rem)] sm:max-w-lg">
                    <AlertDialogHeader>
                        <AlertDialogTitle>Remove this physical copy?</AlertDialogTitle>
                        <AlertDialogDescription>
                            This removes only the {copyToRemove?.platform} physical copy. The game and any other copies stay in your library.
                        </AlertDialogDescription>
                    </AlertDialogHeader>
                    {removeError && <ErrorMessage message={removeError} />}
                    <AlertDialogFooter>
                        <AlertDialogCancel disabled={pendingAction !== null}>Cancel</AlertDialogCancel>
                        <Button
                            type="button"
                            variant="destructive"
                            onClick={() => void handleRemove()}
                            disabled={pendingAction !== null}
                        >
                            {pendingAction === "remove" ? <Loader2 className="animate-spin" /> : <Trash2 />}
                            {pendingAction === "remove" ? "Removing…" : "Remove copy"}
                        </Button>
                    </AlertDialogFooter>
                </AlertDialogContent>
            </AlertDialog>
        </section>
    );
}

function compareCopies(left: PhysicalGameCopy, right: PhysicalGameCopy) {
    return left.platform.localeCompare(right.platform) || left.id - right.id;
}

function ErrorMessage({ message }: { message: string }) {
    return (
        <div
            className="flex items-start gap-2 rounded-lg border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive"
            role="alert"
        >
            <AlertCircle className="mt-0.5 shrink-0" />
            <span>{message}</span>
        </div>
    );
}
