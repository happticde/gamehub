"use client";

import { useEffect, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { Check, Gamepad2, Loader2, Plus, Search, ScanBarcode } from "lucide-react";
import { CameraBarcodeScanner } from "@/components/physical/CameraBarcodeScanner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from "@/components/ui/select";
import { getErrorMessage } from "@/lib/errors";
import type { ApiMessageResponse } from "@/types/api";
import type {
    PagedResult,
    PhysicalCaptureResponse,
    PhysicalGamePlatform,
    PhysicalGameSearchResult,
    PhysicalGtinCandidate,
    PhysicalGtinLookupResponse,
} from "@/types/game";

const PHYSICAL_PLATFORMS: readonly PhysicalGamePlatform[] = ["PS3", "PS4", "PS5"];

interface SelectedGame {
    id: number;
    title: string;
    slug: string;
}

export function PhysicalGameCapture() {
    const router = useRouter();
    const requestInFlight = useRef(false);
    const [gtinInput, setGtinInput] = useState("");
    const [normalizedGtin, setNormalizedGtin] = useState<string | null>(null);
    const [gtinCandidates, setGtinCandidates] = useState<PhysicalGtinCandidate[]>([]);
    const [gtinPending, setGtinPending] = useState(false);
    const [search, setSearch] = useState("");
    const [searchResults, setSearchResults] = useState<PhysicalGameSearchResult[]>([]);
    const [searchPending, setSearchPending] = useState(false);
    const [selectedGame, setSelectedGame] = useState<SelectedGame | null>(null);
    const [manualMode, setManualMode] = useState(false);
    const [manualTitle, setManualTitle] = useState("");
    const [platform, setPlatform] = useState<PhysicalGamePlatform>("PS5");
    const [saving, setSaving] = useState(false);
    const [error, setError] = useState<string | null>(null);

    useEffect(() => {
        const query = search.trim();
        if (query.length < 2 || manualMode) {
            setSearchResults([]);
            setSearchPending(false);
            return;
        }

        const controller = new AbortController();
        const timer = setTimeout(async () => {
            setSearchPending(true);
            try {
                const response = await fetch(
                    `/api/core/library?search=${encodeURIComponent(query)}&pageSize=10&sortBy=title`,
                    { signal: controller.signal },
                );
                if (!response.ok) throw new Error("Game search is temporarily unavailable.");
                const page = await response.json() as PagedResult<PhysicalGameSearchResult>;
                setSearchResults(page.items);
            } catch (caughtError: unknown) {
                if (!(caughtError instanceof DOMException && caughtError.name === "AbortError")) {
                    setError(getErrorMessage(caughtError, "Game search is temporarily unavailable."));
                }
            } finally {
                if (!controller.signal.aborted) setSearchPending(false);
            }
        }, 250);

        return () => {
            clearTimeout(timer);
            controller.abort();
        };
    }, [manualMode, search]);

    const validateGtin = async (value = gtinInput) => {
        const trimmed = value.trim();
        if (!trimmed || gtinPending) return;

        setGtinPending(true);
        setError(null);
        try {
            const response = await fetch(
                `/api/core/library/physical-copies/gtin-lookup?gtin=${encodeURIComponent(trimmed)}`,
            );
            const data = await response.json().catch(() => null) as
                | PhysicalGtinLookupResponse
                | ApiMessageResponse
                | null;
            if (!response.ok || !data || !("normalizedGtin" in data)) {
                throw new Error(readApiMessage(data, "The GTIN is invalid."));
            }

            setNormalizedGtin(data.normalizedGtin);
            setGtinCandidates(data.candidates);
        } catch (caughtError: unknown) {
            setNormalizedGtin(null);
            setGtinCandidates([]);
            setError(getErrorMessage(caughtError, "The GTIN could not be validated."));
        } finally {
            setGtinPending(false);
        }
    };

    const selectExistingGame = (game: SelectedGame) => {
        setSelectedGame(game);
        setManualMode(false);
        setManualTitle("");
        setError(null);
    };

    const beginManualGame = () => {
        setManualMode(true);
        setSelectedGame(null);
        setSearchResults([]);
        setError(null);
    };

    const targetTitle = manualMode ? manualTitle.trim() : selectedGame?.title ?? "";
    const hasValidatedOptionalGtin = gtinInput.trim().length === 0 || normalizedGtin !== null;
    const canSave = targetTitle.length > 0 && hasValidatedOptionalGtin && !saving;

    const saveCapture = async () => {
        if (!canSave || requestInFlight.current) return;

        requestInFlight.current = true;
        setSaving(true);
        setError(null);
        try {
            const response = await fetch("/api/core/library/physical-copies/capture", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    existingGameId: manualMode ? null : selectedGame?.id,
                    manualGameTitle: manualMode ? manualTitle.trim() : null,
                    platform,
                    gtin: normalizedGtin,
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
            setError(getErrorMessage(caughtError, "The physical game could not be saved."));
            requestInFlight.current = false;
            setSaving(false);
        }
    };

    return (
        <div className="space-y-6">
            <section className="rounded-xl border bg-card p-4 shadow-sm sm:p-6">
                <div className="mb-4 flex items-start gap-3">
                    <span className="rounded-full bg-primary/10 p-2 text-primary"><ScanBarcode className="h-5 w-5" /></span>
                    <div>
                        <h2 className="font-semibold">1. Optional barcode</h2>
                        <p className="text-sm text-muted-foreground">
                            Scan UPC-A/EAN-13 or enter a GTIN. Unknown codes continue to normal search.
                        </p>
                    </div>
                </div>

                <div className="flex flex-col gap-2 sm:flex-row">
                    <Input
                        className="h-10"
                        inputMode="numeric"
                        autoComplete="off"
                        value={gtinInput}
                        onChange={(event) => {
                            setGtinInput(event.target.value);
                            setNormalizedGtin(null);
                            setGtinCandidates([]);
                        }}
                        placeholder="UPC-A, EAN-13, or GTIN-14"
                        aria-label="GTIN"
                        disabled={saving}
                    />
                    <div className="flex gap-2">
                        <Button
                            type="button"
                            variant="outline"
                            onClick={() => void validateGtin()}
                            disabled={!gtinInput.trim() || gtinPending || saving}
                        >
                            {gtinPending ? <Loader2 className="animate-spin" /> : <Check />}
                            Validate
                        </Button>
                        <CameraBarcodeScanner
                            disabled={saving || gtinPending}
                            onDetected={(rawValue) => {
                                setGtinInput(rawValue);
                                setNormalizedGtin(null);
                                setGtinCandidates([]);
                                void validateGtin(rawValue);
                            }}
                        />
                    </div>
                </div>

                {normalizedGtin && (
                    <p className="mt-3 text-sm text-emerald-600 dark:text-emerald-400">
                        Validated canonical GTIN: <span className="font-mono font-semibold">{normalizedGtin}</span>
                    </p>
                )}

                {gtinCandidates.length > 0 && (
                    <div className="mt-4 rounded-lg border border-amber-500/35 bg-amber-500/10 p-3">
                        <p className="text-sm font-medium">
                            {gtinCandidates.length === 1
                                ? "A tenant-local confirmed association was found. Select it if it is correct."
                                : "Conflicting tenant-local associations were found. Choose the correct game explicitly."}
                        </p>
                        <div className="mt-2 grid gap-2">
                            {gtinCandidates.map((candidate) => (
                                <GameChoice
                                    key={candidate.gameId}
                                    title={candidate.title}
                                    description={`${candidate.copyCount} confirmed copy/copies · ${candidate.platforms.join(", ")}`}
                                    selected={selectedGame?.id === candidate.gameId}
                                    onSelect={() => selectExistingGame({
                                        id: candidate.gameId,
                                        title: candidate.title,
                                        slug: candidate.slug,
                                    })}
                                />
                            ))}
                        </div>
                    </div>
                )}
            </section>

            <section className="rounded-xl border bg-card p-4 shadow-sm sm:p-6">
                <div className="mb-4 flex items-start gap-3">
                    <span className="rounded-full bg-primary/10 p-2 text-primary"><Search className="h-5 w-5" /></span>
                    <div>
                        <h2 className="font-semibold">2. Choose the game</h2>
                        <p className="text-sm text-muted-foreground">Search your tenant library or explicitly create a manual game.</p>
                    </div>
                </div>

                {!manualMode ? (
                    <div className="space-y-3">
                        <div className="relative">
                            <Search className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
                            <Input
                                className="h-10 pl-9"
                                value={search}
                                onChange={(event) => setSearch(event.target.value)}
                                placeholder="Search existing games"
                                disabled={saving}
                            />
                            {searchPending && <Loader2 className="absolute right-3 top-2.5 h-4 w-4 animate-spin" />}
                        </div>

                        {searchResults.length > 0 && (
                            <div className="grid gap-2">
                                {searchResults.map((game) => (
                                    <GameChoice
                                        key={game.id}
                                        title={game.title}
                                        description="Existing unified game"
                                        selected={selectedGame?.id === game.id}
                                        onSelect={() => selectExistingGame(game)}
                                    />
                                ))}
                            </div>
                        )}

                        <Button type="button" variant="outline" onClick={beginManualGame} disabled={saving}>
                            <Plus /> Create a new manual game instead
                        </Button>
                    </div>
                ) : (
                    <div className="space-y-3 rounded-lg border border-primary/30 bg-primary/5 p-4">
                        <label className="text-sm font-medium" htmlFor="manual-game-title">New manual game title</label>
                        <Input
                            id="manual-game-title"
                            className="h-10"
                            value={manualTitle}
                            maxLength={200}
                            onChange={(event) => setManualTitle(event.target.value)}
                            placeholder="Exact game title"
                            disabled={saving}
                        />
                        <p className="text-xs text-muted-foreground">
                            A scan never creates this automatically. The title will use GameHub&apos;s existing slug matching for later digital sync.
                        </p>
                        <Button type="button" variant="ghost" onClick={() => setManualMode(false)} disabled={saving}>
                            Return to existing game search
                        </Button>
                    </div>
                )}
            </section>

            <section className="rounded-xl border bg-card p-4 shadow-sm sm:p-6">
                <div className="mb-4 flex items-start gap-3">
                    <span className="rounded-full bg-primary/10 p-2 text-primary"><Gamepad2 className="h-5 w-5" /></span>
                    <div>
                        <h2 className="font-semibold">3. Platform and confirmation</h2>
                        <p className="text-sm text-muted-foreground">Review every persisted value before saving.</p>
                    </div>
                </div>

                <label className="text-sm font-medium" htmlFor="capture-platform">Physical platform</label>
                <Select value={platform} onValueChange={(value) => setPlatform(value as PhysicalGamePlatform)} disabled={saving}>
                    <SelectTrigger id="capture-platform" className="mt-2 h-10 w-full sm:w-52"><SelectValue /></SelectTrigger>
                    <SelectContent>
                        {PHYSICAL_PLATFORMS.map((value) => <SelectItem key={value} value={value}>{value}</SelectItem>)}
                    </SelectContent>
                </Select>

                <div className="mt-5 rounded-xl border border-primary/30 bg-primary/5 p-4">
                    <h3 className="font-semibold">Confirm physical game</h3>
                    <dl className="mt-3 grid gap-3 text-sm sm:grid-cols-2">
                        <Summary label="Game title" value={targetTitle || "Choose a game first"} />
                        <Summary label="Game record" value={manualMode ? "New manual unified game" : selectedGame ? "Existing unified game" : "Not selected"} />
                        <Summary label="Physical platform" value={platform} />
                        <Summary label="Normalized GTIN" value={normalizedGtin ?? (gtinInput.trim() ? "Validate the entered GTIN" : "None")} mono={Boolean(normalizedGtin)} />
                    </dl>
                </div>

                {error && <p role="alert" className="mt-4 rounded-lg border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{error}</p>}

                <Button type="button" size="lg" className="mt-5 w-full sm:w-auto" disabled={!canSave} onClick={() => void saveCapture()}>
                    {saving ? <Loader2 className="animate-spin" /> : <Check />}
                    {saving ? "Saving…" : "Confirm and add physical game"}
                </Button>
            </section>
        </div>
    );
}

function GameChoice({ title, description, selected, onSelect }: {
    title: string;
    description: string;
    selected: boolean;
    onSelect(): void;
}) {
    return (
        <button
            type="button"
            className={`flex w-full items-center justify-between rounded-lg border p-3 text-left transition-colors ${selected ? "border-primary bg-primary/10" : "hover:bg-muted/50"}`}
            onClick={onSelect}
        >
            <span>
                <span className="block font-medium">{title}</span>
                <span className="block text-xs text-muted-foreground">{description}</span>
            </span>
            {selected && <Check className="h-5 w-5 text-primary" />}
        </button>
    );
}

function Summary({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
    return (
        <div>
            <dt className="text-xs uppercase tracking-wide text-muted-foreground">{label}</dt>
            <dd className={`mt-1 font-medium ${mono ? "font-mono" : ""}`}>{value}</dd>
        </div>
    );
}

function readApiMessage(data: unknown, fallback: string) {
    if (!data || typeof data !== "object") return fallback;

    if ("message" in data && typeof data.message === "string") return data.message;
    if ("Message" in data && typeof data.Message === "string") return data.Message;
    return fallback;
}
