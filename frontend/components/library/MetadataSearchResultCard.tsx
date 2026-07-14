import Image from "next/image";
import { Check, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import type { MetadataSearchResult } from "@/types/game";

interface MetadataSearchResultCardProps {
    result: MetadataSearchResult;
    actionLabel: string;
    onAction(): void;
    selected?: boolean;
    pending?: boolean;
    disabled?: boolean;
    showPlatformHint?: boolean;
}

/** Shared IGDB result presentation for Fix Match and physical capture. */
export function MetadataSearchResultCard({
    result,
    actionLabel,
    onAction,
    selected = false,
    pending = false,
    disabled = false,
    showPlatformHint = false,
}: MetadataSearchResultCardProps) {
    const title = result.matchedTitle?.trim() || "Unknown title";
    const releaseYear = result.firstReleaseDate
        ? new Date(result.firstReleaseDate).getFullYear()
        : null;

    return (
        <article className={`flex gap-3 rounded-lg border p-3 transition-colors ${selected ? "border-primary bg-primary/10" : "hover:bg-muted/50"}`}>
            <div className="relative h-20 w-16 shrink-0 overflow-hidden rounded bg-muted">
                {result.coverUrl ? (
                    <Image src={result.coverUrl} alt={`${title} cover`} fill className="object-cover" unoptimized />
                ) : (
                    <div className="flex h-full items-center justify-center px-1 text-center text-xs text-muted-foreground">
                        No cover
                    </div>
                )}
            </div>
            <div className="min-w-0 flex-1">
                <h3 className="truncate text-sm font-semibold">{title}</h3>
                <p className="text-xs text-muted-foreground">
                    {releaseYear ?? "Unknown year"} · ID: {result.providerId}
                </p>
                {showPlatformHint && (
                    <p className="mt-1 line-clamp-1 text-xs text-muted-foreground">
                        IGDB platform hint: {result.platforms?.length ? result.platforms.join(", ") : "Not provided"}
                    </p>
                )}
                <p className="mt-1 line-clamp-2 text-xs text-muted-foreground">
                    {result.summary || "No description available."}
                </p>
            </div>
            <div className="flex shrink-0 items-center">
                <Button type="button" size="sm" onClick={onAction} disabled={disabled || pending}>
                    {pending ? <Loader2 className="animate-spin" /> : selected ? <Check /> : null}
                    {pending ? "Applying…" : selected ? "Selected" : actionLabel}
                </Button>
            </div>
        </article>
    );
}
