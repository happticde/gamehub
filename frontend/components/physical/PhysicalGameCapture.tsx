"use client";

import { useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { ArrowLeft, Check, Gamepad2, Loader2, Search } from "lucide-react";
import { MetadataSearchResultCard } from "@/components/library/MetadataSearchResultCard";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from "@/components/ui/select";
import { useDebouncedMetadataSearch } from "@/hooks/useDebouncedMetadataSearch";
import { getErrorMessage } from "@/lib/errors";
import type { ApiMessageResponse } from "@/types/api";
import type {
    MetadataSearchResult,
    PhysicalCaptureResponse,
    PhysicalGamePlatform,
} from "@/types/game";

const PHYSICAL_PLATFORMS: readonly PhysicalGamePlatform[] = ["PS3", "PS4", "PS5"];
type CaptureStep = "search" | "platform" | "confirm";

export function PhysicalGameCapture() {
    const router = useRouter();
    const requestInFlight = useRef(false);
    const [step, setStep] = useState<CaptureStep>("search");
    const [query, setQuery] = useState("");
    const [selectedResult, setSelectedResult] = useState<MetadataSearchResult | null>(null);
    const [platform, setPlatform] = useState<PhysicalGamePlatform | null>(null);
    const [saving, setSaving] = useState(false);
    const [saveError, setSaveError] = useState<string | null>(null);
    const search = useDebouncedMetadataSearch(query, step === "search");

    const selectResult = (result: MetadataSearchResult) => {
        setSelectedResult(result);
        setPlatform(null);
        setSaveError(null);
    };

    const saveCapture = async () => {
        if (step !== "confirm" || !selectedResult || !platform || requestInFlight.current) return;

        requestInFlight.current = true;
        setSaving(true);
        setSaveError(null);
        try {
            const response = await fetch("/api/core/library/physical-copies/capture", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    metadataProviderId: selectedResult.providerId,
                    platform,
                    confirmed: true,
                }),
            });
            const data = await response.json().catch(() => null) as
                | PhysicalCaptureResponse
                | ApiMessageResponse
                | null;
            if (!response.ok || !data || !("gameId" in data)) {
                throw new Error(readApiMessage(data, "The physical game could not be saved."));
            }

            router.push(`/library/${data.gameId}`);
            router.refresh();
        } catch (caughtError: unknown) {
            setSaveError(getErrorMessage(caughtError, "The physical game could not be saved."));
            requestInFlight.current = false;
            setSaving(false);
        }
    };

    const selectedTitle = selectedResult?.matchedTitle?.trim() || "Unknown title";

    return (
        <div className="space-y-6">
            {step === "search" && (
                <section className={`rounded-xl border bg-card p-4 shadow-sm sm:p-6 ${selectedResult ? "pb-[calc(6rem+env(safe-area-inset-bottom))] sm:pb-[calc(6rem+env(safe-area-inset-bottom))] lg:pb-6" : ""}`}>
                    <div className="lg:grid lg:grid-cols-[minmax(0,1fr)_16rem] lg:items-start lg:gap-6">
                        <div className="min-w-0">
                            <StepHeading icon={<Search className="h-5 w-5" />} title="1. Search IGDB">
                                Search by title, then explicitly select the correct result.
                            </StepHeading>

                            <div className="relative">
                                <Search className="absolute left-3 top-3 h-4 w-4 text-muted-foreground" />
                                <Input
                                    className="h-10 pl-9 pr-10"
                                    value={query}
                                    onChange={(event) => {
                                        setQuery(event.target.value);
                                        setSelectedResult(null);
                                        setPlatform(null);
                                        setSaveError(null);
                                    }}
                                    placeholder="Search for a game on IGDB"
                                    aria-label="IGDB game title"
                                    autoComplete="off"
                                />
                                {search.pending && (
                                    <Loader2 aria-label="Searching IGDB" className="absolute right-3 top-3 h-4 w-4 animate-spin" />
                                )}
                            </div>

                            {query.trim().length < 2 && (
                                <p className="mt-3 text-sm text-muted-foreground">Enter at least two characters to search.</p>
                            )}
                            {search.error && (
                                <p role="alert" className="mt-4 rounded-lg border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">
                                    {search.error} No game or physical copy was created. You can adjust the title and retry.
                                </p>
                            )}
                            {!search.pending && !search.error && search.completedQuery && search.results.length === 0 && (
                                <p className="mt-4 rounded-lg border bg-muted/40 p-4 text-center text-sm text-muted-foreground">
                                    No IGDB results found for &quot;{search.completedQuery}&quot;. Try a shorter or alternate title.
                                </p>
                            )}

                            {search.results.length > 0 && (
                                <div className="mt-4 grid gap-3">
                                    {search.results.map((result) => (
                                        <MetadataSearchResultCard
                                            key={result.providerId}
                                            result={result}
                                            actionLabel="Select"
                                            selected={selectedResult?.providerId === result.providerId}
                                            onAction={() => selectResult(result)}
                                            showPlatformHint
                                        />
                                    ))}
                                </div>
                            )}
                        </div>

                        <DesktopSearchActions
                            selectedResult={selectedResult}
                            selectedTitle={selectedTitle}
                            onContinue={() => setStep("platform")}
                            onCancel={() => router.push("/library")}
                        />
                    </div>

                    {selectedResult && (
                        <div
                            className="fixed inset-x-0 bottom-0 z-40 border-t bg-background px-4 pt-3 pb-[calc(0.75rem+env(safe-area-inset-bottom))] shadow-lg lg:hidden"
                            role="region"
                            aria-label="Selected IGDB game"
                        >
                            <div className="mx-auto flex max-w-3xl items-center gap-3">
                                <div className="min-w-0 flex-1">
                                    <p className="text-xs text-muted-foreground">Selected game</p>
                                    <p className="truncate text-sm font-semibold">{selectedTitle}</p>
                                </div>
                                <Button type="button" onClick={() => setStep("platform")}>
                                    Continue
                                </Button>
                            </div>
                        </div>
                    )}
                </section>
            )}

            {step === "platform" && selectedResult && (
                <section className="rounded-xl border bg-card p-4 shadow-sm sm:p-6 lg:max-w-3xl">
                    <StepHeading icon={<Gamepad2 className="h-5 w-5" />} title="2. Choose the physical platform">
                        IGDB platform data is only a hint. GameHub never selects PS3, PS4, or PS5 for you.
                    </StepHeading>

                    <div className="rounded-lg border bg-muted/30 p-4">
                        <p className="font-semibold">{selectedTitle}</p>
                        <p className="mt-1 text-sm text-muted-foreground">
                            IGDB platform hint: {selectedResult.platforms?.length
                                ? selectedResult.platforms.join(", ")
                                : "Not provided"}
                        </p>
                    </div>

                    <label className="mt-5 block text-sm font-medium" htmlFor="capture-platform">Physical platform</label>
                    <Select
                        value={platform ?? ""}
                        onValueChange={(value) => setPlatform(value as PhysicalGamePlatform)}
                    >
                        <SelectTrigger id="capture-platform" className="mt-2 h-10 w-full sm:w-52">
                            <SelectValue placeholder="Choose PS3, PS4, or PS5" />
                        </SelectTrigger>
                        <SelectContent>
                            {PHYSICAL_PLATFORMS.map((value) => (
                                <SelectItem key={value} value={value}>{value}</SelectItem>
                            ))}
                        </SelectContent>
                    </Select>

                    <div className="mt-5 flex flex-col-reverse gap-2 sm:flex-row sm:justify-between">
                        <Button type="button" variant="ghost" onClick={() => setStep("search")}>
                            <ArrowLeft /> Back to search
                        </Button>
                        <Button type="button" disabled={!platform} onClick={() => setStep("confirm")}>
                            Review selection
                        </Button>
                    </div>
                </section>
            )}

            {step === "confirm" && selectedResult && platform && (
                <section className="rounded-xl border bg-card p-4 shadow-sm sm:p-6 lg:max-w-3xl">
                    <StepHeading icon={<Check className="h-5 w-5" />} title="3. Confirm physical game">
                        This is the only step that can create a game or physical copy.
                    </StepHeading>

                    <div className="rounded-xl border border-primary/30 bg-primary/5 p-4">
                        <dl className="grid gap-4 text-sm sm:grid-cols-2">
                            <Summary label="Game title" value={selectedTitle} />
                            <Summary label="IGDB ID" value={String(selectedResult.providerId)} />
                            <Summary label="Physical platform" value={platform} />
                            <Summary label="GameHub action" value="Reuse an exact tenant-local IGDB match, or create a new tenant-owned game" />
                        </dl>
                    </div>

                    {saveError && (
                        <p role="alert" className="mt-4 rounded-lg border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">
                            {saveError}
                        </p>
                    )}

                    <div className="mt-5 flex flex-col-reverse gap-2 sm:flex-row sm:justify-between">
                        <div className="flex flex-col-reverse gap-2 sm:flex-row">
                            <Button type="button" variant="ghost" disabled={saving} onClick={() => setStep("platform")}>
                                <ArrowLeft /> Back to platform
                            </Button>
                            <Button type="button" variant="outline" disabled={saving} onClick={() => router.push("/library")}>
                                Cancel
                            </Button>
                        </div>
                        <Button type="button" size="lg" disabled={saving} onClick={() => void saveCapture()}>
                            {saving ? <Loader2 className="animate-spin" /> : <Check />}
                            {saving ? "Saving…" : "Confirm and add physical game"}
                        </Button>
                    </div>
                </section>
            )}
        </div>
    );
}

function StepHeading({ icon, title, children }: { icon: React.ReactNode; title: string; children: React.ReactNode }) {
    return (
        <div className="mb-5 flex items-start gap-3">
            <span className="rounded-full bg-primary/10 p-2 text-primary">{icon}</span>
            <div>
                <h2 className="font-semibold">{title}</h2>
                <p className="text-sm text-muted-foreground">{children}</p>
            </div>
        </div>
    );
}

function DesktopSearchActions({
    selectedResult,
    selectedTitle,
    onContinue,
    onCancel,
}: {
    selectedResult: MetadataSearchResult | null;
    selectedTitle: string;
    onContinue(): void;
    onCancel(): void;
}) {
    return (
        <aside className="hidden lg:sticky lg:top-24 lg:block" aria-label="Physical capture selection">
            <div className="rounded-lg border bg-muted/30 p-4">
                <p className="text-xs uppercase tracking-wide text-muted-foreground">Selected game</p>
                <div className="mt-2 min-h-16" aria-live="polite">
                    {selectedResult ? (
                        <>
                            <p className="line-clamp-2 font-semibold">{selectedTitle}</p>
                            <p className="mt-1 text-xs text-muted-foreground">IGDB ID: {selectedResult.providerId}</p>
                        </>
                    ) : (
                        <p className="text-sm text-muted-foreground">Select a game from the results to continue.</p>
                    )}
                </div>
                <Button className="mt-5 w-full" type="button" disabled={!selectedResult} onClick={onContinue}>
                    Continue
                </Button>
                <Button className="mt-2 w-full" type="button" variant="ghost" onClick={onCancel}>
                    Cancel
                </Button>
            </div>
        </aside>
    );
}

function Summary({ label, value }: { label: string; value: string }) {
    return (
        <div>
            <dt className="text-xs uppercase tracking-wide text-muted-foreground">{label}</dt>
            <dd className="mt-1 font-medium">{value}</dd>
        </div>
    );
}

function readApiMessage(data: unknown, fallback: string) {
    if (!data || typeof data !== "object") return fallback;
    if ("message" in data && typeof data.message === "string") return data.message;
    if ("Message" in data && typeof data.Message === "string") return data.Message;
    return fallback;
}
