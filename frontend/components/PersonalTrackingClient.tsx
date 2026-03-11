"use client";

import { useState } from "react";
import { PlayStatus } from "@/types/game";
import { Star, Loader2, Check } from "lucide-react";

interface PersonalTrackingProps {
    gameId: number;
    initialStatus: PlayStatus;
    initialRating: number | null;
}

export default function PersonalTrackingClient({
    gameId,
    initialStatus,
    initialRating
}: PersonalTrackingProps) {
    const [status, setStatus] = useState<PlayStatus>(initialStatus);
    const [rating, setRating] = useState<number | null>(initialRating);
    const [isSaving, setIsSaving] = useState(false);
    const [saveSuccess, setSaveSuccess] = useState(false);

    const handleSave = async (newStatus: PlayStatus, newRating: number | null) => {
        setIsSaving(true);
        setSaveSuccess(false);

        try {
            const res = await fetch(`/api/core/library/${gameId}/personal`, {
                method: "PATCH",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    playStatus: newStatus,
                    userRating: newRating
                })
            });

            if (!res.ok) throw new Error("Failed to save personal tracking");
            
            setStatus(newStatus);
            setRating(newRating);
            setSaveSuccess(true);
            setTimeout(() => setSaveSuccess(false), 2000);
        } catch (error) {
            console.error(error);
            // In a real app we might show a toast here
        } finally {
            setIsSaving(false);
        }
    };

    return (
        <div className="rounded-xl border border-primary/20 bg-primary/5 p-6 shadow-sm mb-8">
            <h3 className="flex items-center gap-2 font-semibold text-lg border-b border-primary/20 pb-3 mb-4 text-primary">
                <Star className="h-5 w-5 fill-primary" />
                Personal Tracking
            </h3>

            <div className="space-y-6">
                {/* Play Status Select */}
                <div>
                    <label className="text-sm font-medium text-muted-foreground block mb-2">Play Status</label>
                    <select
                        value={status}
                        onChange={(e) => handleSave(e.target.value as PlayStatus, rating)}
                        disabled={isSaving}
                        className="w-full rounded-md border border-border bg-background px-3 py-2 text-sm text-foreground focus:outline-none focus:ring-2 focus:ring-primary focus:border-transparent cursor-pointer disabled:opacity-50"
                    >
                        <option value="Unplayed">Unplayed</option>
                        <option value="Played">Played</option>
                        <option value="Playing">Playing</option>
                        <option value="Completed">Completed</option>
                        <option value="Abandoned">Abandoned</option>
                    </select>
                </div>

                {/* 5-Star Rating */}
                <div>
                    <label className="text-sm font-medium text-muted-foreground block mb-2 flex items-center justify-between">
                        <span>Your Rating</span>
                        {rating !== null && (
                            <button
                                onClick={() => handleSave(status, null)}
                                className="text-xs text-muted-foreground hover:text-destructive transition-colors"
                            >
                                Clear
                            </button>
                        )}
                    </label>
                    <div className="flex items-center gap-1">
                        {[1, 2, 3, 4, 5].map((star) => (
                            <button
                                key={star}
                                onClick={() => handleSave(status, star)}
                                disabled={isSaving}
                                className={`p-1 rounded-sm focus:outline-none focus:ring-2 focus:ring-primary transition-all hover:scale-110 disabled:opacity-50 disabled:hover:scale-100 ${
                                    (rating || 0) >= star
                                        ? "text-yellow-400 group-hover:text-yellow-500"
                                        : "text-muted hover:text-yellow-400/50"
                                }`}
                                aria-label={`Rate ${star} stars`}
                            >
                                <Star
                                    className={`h-7 w-7 transition-all ${
                                        (rating || 0) >= star ? "fill-yellow-400" : "fill-none"
                                    }`}
                                />
                            </button>
                        ))}
                        
                        <div className="ml-4 h-5 w-5 flex items-center justify-center">
                            {isSaving && <Loader2 className="h-4 w-4 animate-spin text-muted-foreground" />}
                            {saveSuccess && <Check className="h-4 w-4 text-green-500 animate-in zoom-in" />}
                        </div>
                    </div>
                </div>
            </div>
        </div>
    );
}
