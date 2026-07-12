"use client";

import { type FormEvent, useMemo, useState } from "react";
import { Check, Loader2, Plus, Tags, X } from "lucide-react";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import type { CustomTagUpdateResponse } from "@/types/game";

interface CustomTagEditorProps {
    gameId: number;
    initialTags: string[];
    availableTags: string[];
}

function normalizeTagName(value: string): string {
    return value.trim().replace(/\s+/g, " ");
}

function sameTags(left: string[], right: string[]): boolean {
    const normalize = (tags: string[]) => tags.map(tag => tag.toLocaleLowerCase()).sort();
    return JSON.stringify(normalize(left)) === JSON.stringify(normalize(right));
}

/** Lets a tenant assign existing suggestions or create durable custom tags for one game. */
export default function CustomTagEditor({ gameId, initialTags, availableTags }: CustomTagEditorProps) {
    const router = useRouter();
    const [tags, setTags] = useState(initialTags);
    const [savedTags, setSavedTags] = useState(initialTags);
    const [input, setInput] = useState("");
    const [isSaving, setIsSaving] = useState(false);
    const [message, setMessage] = useState<string | null>(null);
    const [hasError, setHasError] = useState(false);

    const suggestions = useMemo(
        () => availableTags.filter(option =>
            !tags.some(tag => tag.toLocaleLowerCase() === option.toLocaleLowerCase())),
        [availableTags, tags]
    );
    const hasChanges = !sameTags(tags, savedTags);

    const addTag = (event: FormEvent<HTMLFormElement>) => {
        event.preventDefault();
        const normalized = normalizeTagName(input);
        if (!normalized) return;

        const suggestedName = availableTags.find(
            tag => tag.toLocaleLowerCase() === normalized.toLocaleLowerCase()
        );
        const name = suggestedName ?? normalized;
        if (!tags.some(tag => tag.toLocaleLowerCase() === name.toLocaleLowerCase())) {
            setTags(current => [...current, name].sort((a, b) => a.localeCompare(b)));
        }
        setInput("");
        setMessage(null);
    };

    const saveTags = async () => {
        setIsSaving(true);
        setMessage(null);
        setHasError(false);

        try {
            const response = await fetch(`/api/core/library/${gameId}/tags`, {
                method: "PUT",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ tags })
            });
            const payload = await response.json().catch(() => null) as
                | (Partial<CustomTagUpdateResponse> & { message?: string })
                | null;

            if (!response.ok) {
                throw new Error(payload?.message ?? "Custom tags could not be saved.");
            }

            const persistedTags = payload?.customTags ?? tags;
            setTags(persistedTags);
            setSavedTags(persistedTags);
            setMessage("Tags saved.");
            router.refresh();
        } catch (error: unknown) {
            setHasError(true);
            setMessage(error instanceof Error ? error.message : "Custom tags could not be saved.");
        } finally {
            setIsSaving(false);
        }
    };

    return (
        <section className="rounded-xl border border-border bg-card p-5 shadow-sm">
            <h3 className="mb-1 flex items-center gap-2 font-semibold">
                <Tags className="h-4 w-4 text-primary" /> Your Tags
            </h3>
            <p className="mb-4 text-xs text-muted-foreground">
                Reuse a suggestion or add your own tag. Suggestions are private to your tenant.
            </p>

            <div className="mb-4 flex min-h-8 flex-wrap gap-2">
                {tags.length === 0 ? (
                    <span className="text-sm text-muted-foreground">No custom tags yet.</span>
                ) : tags.map(tag => (
                    <span
                        key={tag}
                        className="inline-flex items-center gap-1 rounded-full border border-primary/20 bg-primary/10 px-2.5 py-1 text-sm text-primary"
                    >
                        {tag}
                        <button
                            type="button"
                            onClick={() => {
                                setTags(current => current.filter(candidate => candidate !== tag));
                                setMessage(null);
                            }}
                            className="rounded-full p-0.5 hover:bg-primary/15 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                            aria-label={`Remove ${tag}`}
                        >
                            <X className="h-3 w-3" />
                        </button>
                    </span>
                ))}
            </div>

            <form onSubmit={addTag} className="flex gap-2">
                <Input
                    value={input}
                    onChange={event => setInput(event.target.value)}
                    list={`tag-suggestions-${gameId}`}
                    maxLength={64}
                    placeholder="e.g. Cozy, Backlog 2026"
                    aria-label="Custom tag name"
                    disabled={isSaving || tags.length >= 50}
                />
                <datalist id={`tag-suggestions-${gameId}`}>
                    {suggestions.map(tag => <option key={tag} value={tag} />)}
                </datalist>
                <Button
                    type="submit"
                    variant="outline"
                    disabled={!normalizeTagName(input) || isSaving || tags.length >= 50}
                >
                    <Plus data-icon="inline-start" /> Add
                </Button>
            </form>

            <div className="mt-3 flex items-center justify-between gap-3">
                <p
                    className={`min-h-5 text-xs ${hasError ? "text-destructive" : "text-muted-foreground"}`}
                    role={hasError ? "alert" : "status"}
                >
                    {message}
                </p>
                <Button type="button" onClick={saveTags} disabled={!hasChanges || isSaving}>
                    {isSaving ? <Loader2 className="animate-spin" /> : <Check />}
                    Save
                </Button>
            </div>
        </section>
    );
}
