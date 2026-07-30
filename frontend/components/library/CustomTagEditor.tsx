"use client";

import { type FormEvent, useEffect, useMemo, useState } from "react";
import { Loader2, Plus, Tags, Trash2, X } from "lucide-react";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Popover, PopoverAnchor, PopoverContent } from "@/components/ui/popover";
import { fetchAuthenticated } from "@/lib/authenticated-fetch";
import type {
    CustomTagUpdateResponse,
    DeleteCustomTagResponse,
    TagSuggestion
} from "@/types/game";

interface CustomTagEditorProps {
    gameId: number;
    initialTags: string[];
    availableTags: TagSuggestion[];
}

function normalizeTagName(value: string): string {
    return value.trim().replace(/\s+/g, " ");
}

/** Lets a tenant assign existing suggestions or create durable custom tags for one game. */
export default function CustomTagEditor({ gameId, initialTags, availableTags }: CustomTagEditorProps) {
    const router = useRouter();
    const [tags, setTags] = useState(initialTags);
    const [suggestions, setSuggestions] = useState(availableTags);
    const [input, setInput] = useState("");
    const [suggestionsOpen, setSuggestionsOpen] = useState(false);
    const [isSaving, setIsSaving] = useState(false);
    const [deletingTagId, setDeletingTagId] = useState<number | null>(null);
    const [message, setMessage] = useState<string | null>(null);
    const [hasError, setHasError] = useState(false);

    useEffect(() => setSuggestions(availableTags), [availableTags]);

    const visibleSuggestions = useMemo(() => {
        const search = normalizeTagName(input).toLocaleLowerCase();
        const visible = suggestions.filter(suggestion =>
            !tags.some(tag => tag.toLocaleLowerCase() === suggestion.name.toLocaleLowerCase())
            && (!search || suggestion.name.toLocaleLowerCase().includes(search))
        );

        return {
            custom: visible.filter(suggestion => suggestion.isCustom),
            system: visible.filter(suggestion => !suggestion.isCustom)
        };
    }, [input, suggestions, tags]);
    const isBusy = isSaving || deletingTagId !== null;

    const persistTags = async (nextTags: string[], previousTags: string[]): Promise<boolean> => {
        setTags(nextTags);
        setIsSaving(true);
        setMessage(null);
        setHasError(false);

        try {
            const response = await fetchAuthenticated(`/api/core/library/${gameId}/tags`, {
                method: "PUT",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ tags: nextTags })
            });
            const payload = await response.json().catch(() => null) as
                | (Partial<CustomTagUpdateResponse> & { message?: string })
                | null;

            if (!response.ok) {
                throw new Error(payload?.message ?? "Custom tags could not be saved.");
            }

            const persistedTags = payload?.customTags ?? nextTags;
            setTags(persistedTags);
            setMessage("Saved automatically.");
            setSuggestionsOpen(false);
            router.refresh();
            return true;
        } catch (error: unknown) {
            setTags(previousTags);
            setHasError(true);
            setMessage(error instanceof Error ? error.message : "Custom tags could not be saved.");
            return false;
        } finally {
            setIsSaving(false);
        }
    };

    const addTagName = async (rawName: string) => {
        const normalized = normalizeTagName(rawName);
        if (!normalized) return;

        const suggestion = suggestions.find(
            candidate => candidate.name.toLocaleLowerCase() === normalized.toLocaleLowerCase()
        );
        const name = suggestion?.name ?? normalized;
        if (tags.some(tag => tag.toLocaleLowerCase() === name.toLocaleLowerCase())) {
            setInput("");
            setSuggestionsOpen(false);
            return;
        }

        const nextTags = [...tags, name].sort((a, b) => a.localeCompare(b));
        setInput("");
        const saved = await persistTags(nextTags, tags);
        if (!saved) setInput(name);
    };

    const addTag = (event: FormEvent<HTMLFormElement>) => {
        event.preventDefault();
        void addTagName(input);
    };

    const removeTag = (tagToRemove: string) => {
        const nextTags = tags.filter(tag => tag !== tagToRemove);
        void persistTags(nextTags, tags);
    };

    const deleteSuggestion = async (suggestion: TagSuggestion) => {
        if (suggestion.usageCount > 0 && !window.confirm(
            `Delete "${suggestion.name}" from ${suggestion.usageCount} game assignment${suggestion.usageCount === 1 ? "" : "s"}?`
        )) {
            return;
        }

        setDeletingTagId(suggestion.id);
        setMessage(null);
        setHasError(false);

        try {
            const response = await fetchAuthenticated(`/api/core/library/tags/${suggestion.id}`, {
                method: "DELETE"
            });
            const payload = await response.json().catch(() => null) as
                | (Partial<DeleteCustomTagResponse> & { message?: string })
                | null;
            if (!response.ok) {
                throw new Error(payload?.message ?? "The custom tag could not be deleted.");
            }

            setSuggestions(current => current.filter(candidate => candidate.id !== suggestion.id));
            setMessage(`Deleted "${suggestion.name}".`);
            router.refresh();
        } catch (error: unknown) {
            setHasError(true);
            setMessage(error instanceof Error ? error.message : "The custom tag could not be deleted.");
        } finally {
            setDeletingTagId(null);
        }
    };

    const renderSuggestionGroup = (title: string, items: TagSuggestion[]) => {
        if (items.length === 0) return null;

        return (
            <div>
                <p className="px-2 pb-1 pt-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                    {title}
                </p>
                <div className="space-y-0.5">
                    {items.map(suggestion => (
                        <div
                            key={suggestion.id}
                            className="flex items-center rounded-md hover:bg-muted"
                        >
                            <button
                                type="button"
                                onClick={() => void addTagName(suggestion.name)}
                                disabled={isBusy}
                                className="min-w-0 flex-1 px-2 py-2 text-left text-sm disabled:opacity-50"
                            >
                                <span className="block truncate">{suggestion.name}</span>
                            </button>
                            {suggestion.isCustom && (
                                <button
                                    type="button"
                                    onClick={() => void deleteSuggestion(suggestion)}
                                    disabled={isBusy}
                                    className="mr-1 rounded-md p-2 text-muted-foreground hover:bg-destructive/10 hover:text-destructive disabled:opacity-50"
                                    aria-label={`Delete custom tag ${suggestion.name}`}
                                    title="Delete custom tag"
                                >
                                    {deletingTagId === suggestion.id
                                        ? <Loader2 className="h-4 w-4 animate-spin" />
                                        : <Trash2 className="h-4 w-4" />}
                                </button>
                            )}
                        </div>
                    ))}
                </div>
            </div>
        );
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
                            onClick={() => removeTag(tag)}
                            disabled={isBusy}
                            className="rounded-full p-0.5 hover:bg-primary/15 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                            aria-label={`Remove ${tag}`}
                        >
                            <X className="h-3 w-3" />
                        </button>
                    </span>
                ))}
            </div>

            <form onSubmit={addTag} className="flex gap-2">
                <Popover open={suggestionsOpen} onOpenChange={setSuggestionsOpen}>
                    <PopoverAnchor asChild>
                        <div className="min-w-0 flex-1">
                            <Input
                                value={input}
                                onChange={event => {
                                    setInput(event.target.value);
                                    setSuggestionsOpen(true);
                                }}
                                onFocus={() => setSuggestionsOpen(true)}
                                maxLength={64}
                                placeholder="e.g. Cozy, Backlog 2026"
                                aria-label="Custom tag name"
                                aria-expanded={suggestionsOpen}
                                disabled={isBusy || tags.length >= 50}
                            />
                        </div>
                    </PopoverAnchor>
                    <PopoverContent
                        align="start"
                        className="max-h-72 w-80 max-w-[calc(100vw-2rem)] overflow-y-auto p-1"
                        onOpenAutoFocus={event => event.preventDefault()}
                    >
                        {visibleSuggestions.custom.length === 0 && visibleSuggestions.system.length === 0 ? (
                            <p className="px-3 py-4 text-center text-sm text-muted-foreground">
                                No saved suggestions found.
                            </p>
                        ) : (
                            <>
                                {renderSuggestionGroup("Custom", visibleSuggestions.custom)}
                                {renderSuggestionGroup("System Tags", visibleSuggestions.system)}
                            </>
                        )}
                    </PopoverContent>
                </Popover>
                <Button
                    type="submit"
                    variant="outline"
                    disabled={!normalizeTagName(input) || isBusy || tags.length >= 50}
                >
                    {isSaving ? <Loader2 className="animate-spin" /> : <Plus data-icon="inline-start" />}
                    Add
                </Button>
            </form>

            <p
                className={`mt-3 min-h-5 text-xs ${hasError ? "text-destructive" : "text-muted-foreground"}`}
                role={hasError ? "alert" : "status"}
            >
                {isSaving ? "Saving…" : message}
            </p>
        </section>
    );
}
