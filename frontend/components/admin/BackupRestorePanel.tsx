"use client";

import { useState } from "react";
import {
    AlertCircle,
    ArchiveRestore,
    CheckCircle2,
    Download,
    FileJson,
    Loader2,
    Upload,
    X,
} from "lucide-react";
import {
    AlertDialog,
    AlertDialogAction,
    AlertDialogCancel,
    AlertDialogContent,
    AlertDialogDescription,
    AlertDialogFooter,
    AlertDialogHeader,
    AlertDialogTitle,
    AlertDialogTrigger,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import type {
    BackupRestoreResult,
    BackupSummary,
    BackupValidationResult,
} from "@/types/game";

const maxBackupSizeBytes = 25 * 1024 * 1024;

type Operation = "idle" | "downloading" | "validating" | "restoring";
type Notice = { kind: "success" | "error"; message: string };

function formatFileSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KiB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MiB`;
}

function errorMessage(error: unknown): string {
    return error instanceof Error ? error.message : "An unexpected error occurred.";
}

function SummaryGrid({ summary }: { summary: BackupSummary }) {
    const metrics = [
        ["Games", summary.gameCount],
        ["Platform items", summary.platformItemCount],
        ["Manual links", summary.manualLinkCount],
        ["Tags", summary.tagCount],
    ] as const;

    return (
        <div className="grid grid-cols-2 gap-px overflow-hidden rounded-lg border bg-border md:grid-cols-4">
            {metrics.map(([label, value]) => (
                <div key={label} className="min-w-0 bg-background px-4 py-3">
                    <p className="text-xs font-medium text-muted-foreground">{label}</p>
                    <p className="mt-1 text-xl font-semibold tabular-nums">{value.toLocaleString()}</p>
                </div>
            ))}
        </div>
    );
}

export function BackupRestorePanel() {
    const [operation, setOperation] = useState<Operation>("idle");
    const [selectedFile, setSelectedFile] = useState<File | null>(null);
    const [validation, setValidation] = useState<BackupValidationResult | null>(null);
    const [restoreResult, setRestoreResult] = useState<BackupRestoreResult | null>(null);
    const [notice, setNotice] = useState<Notice | null>(null);

    const handleDownload = async () => {
        setOperation("downloading");
        setNotice(null);

        try {
            const response = await fetch("/api/core/backup");
            if (!response.ok) {
                throw new Error(`Backup download failed (Status ${response.status}).`);
            }

            const blob = await response.blob();
            const objectUrl = URL.createObjectURL(blob);
            const anchor = document.createElement("a");
            anchor.href = objectUrl;
            anchor.download = `gamehub-backup-${new Date().toISOString().slice(0, 10)}.json`;
            document.body.appendChild(anchor);
            anchor.click();
            anchor.remove();
            URL.revokeObjectURL(objectUrl);

            setNotice({ kind: "success", message: "Backup downloaded." });
        } catch (error) {
            setNotice({ kind: "error", message: errorMessage(error) });
        } finally {
            setOperation("idle");
        }
    };

    const validateFile = async (file: File) => {
        setOperation("validating");
        setValidation(null);
        setRestoreResult(null);
        setNotice(null);

        if (file.size > maxBackupSizeBytes) {
            setNotice({ kind: "error", message: "Backup files may not exceed 25 MiB." });
            setOperation("idle");
            return;
        }

        try {
            const formData = new FormData();
            formData.append("file", file);

            const response = await fetch("/api/core/backup/validate", {
                method: "POST",
                body: formData,
            });
            const result = (await response.json()) as BackupValidationResult;
            setValidation(result);

            if (!response.ok && result.errors.length === 0) {
                setNotice({ kind: "error", message: `Validation failed (Status ${response.status}).` });
            }
        } catch (error) {
            setNotice({ kind: "error", message: errorMessage(error) });
        } finally {
            setOperation("idle");
        }
    };

    const handleFileChange = (event: React.ChangeEvent<HTMLInputElement>) => {
        const file = event.target.files?.[0] ?? null;
        event.target.value = "";
        setSelectedFile(file);

        if (file) {
            void validateFile(file);
        }
    };

    const clearFile = () => {
        setSelectedFile(null);
        setValidation(null);
        setRestoreResult(null);
        setNotice(null);
    };

    const handleRestore = async () => {
        if (!selectedFile || !validation?.isValid) return;

        setOperation("restoring");
        setRestoreResult(null);
        setNotice(null);

        try {
            const formData = new FormData();
            formData.append("file", selectedFile);

            const response = await fetch("/api/core/backup/restore", {
                method: "POST",
                body: formData,
            });
            const result = (await response.json()) as BackupRestoreResult;
            setRestoreResult(result);
            setNotice({
                kind: result.restored ? "success" : "error",
                message: result.message || `Restore failed (Status ${response.status}).`,
            });
        } catch (error) {
            setNotice({ kind: "error", message: errorMessage(error) });
        } finally {
            setOperation("idle");
        }
    };

    const isBusy = operation !== "idle";

    return (
        <div className="space-y-8 animate-in fade-in slide-in-from-bottom-2 duration-300">
            <section className="border-b pb-8">
                <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
                    <div className="flex items-start gap-3">
                        <Download className="mt-0.5 h-5 w-5 shrink-0 text-primary" />
                        <div>
                            <h2 className="text-lg font-semibold">JSON Backup</h2>
                            <p className="mt-1 text-sm text-muted-foreground">
                                Versioned snapshot of library data and manual matching overrides.
                            </p>
                        </div>
                    </div>
                    <Button onClick={handleDownload} disabled={isBusy} className="w-full sm:w-auto">
                        {operation === "downloading" ? (
                            <Loader2 className="animate-spin" />
                        ) : (
                            <Download />
                        )}
                        Download backup
                    </Button>
                </div>
            </section>

            <section className="space-y-5">
                <div className="flex items-start gap-3">
                    <Upload className="mt-0.5 h-5 w-5 shrink-0 text-primary" />
                    <div>
                        <h2 className="text-lg font-semibold">Restore</h2>
                        <p className="mt-1 text-sm text-muted-foreground">
                            The target library must be empty.
                        </p>
                    </div>
                </div>

                <div className="rounded-lg border border-dashed p-4">
                    {selectedFile ? (
                        <div className="flex min-w-0 items-center gap-3">
                            <FileJson className="h-5 w-5 shrink-0 text-muted-foreground" />
                            <div className="min-w-0 flex-1">
                                <p className="truncate text-sm font-medium" title={selectedFile.name}>
                                    {selectedFile.name}
                                </p>
                                <p className="text-xs text-muted-foreground">
                                    {formatFileSize(selectedFile.size)}
                                </p>
                            </div>
                            {operation === "validating" ? (
                                <Loader2 className="h-4 w-4 shrink-0 animate-spin text-muted-foreground" />
                            ) : (
                                <Button
                                    type="button"
                                    variant="ghost"
                                    size="icon"
                                    onClick={clearFile}
                                    disabled={isBusy}
                                    title="Remove selected backup"
                                >
                                    <X />
                                    <span className="sr-only">Remove selected backup</span>
                                </Button>
                            )}
                        </div>
                    ) : (
                        <div className="flex flex-col items-center justify-center gap-3 py-5 text-center">
                            <FileJson className="h-8 w-8 text-muted-foreground" />
                            <Button asChild variant="outline" disabled={isBusy}>
                                <label htmlFor="backup-file">
                                    <Upload />
                                    Choose JSON backup
                                </label>
                            </Button>
                        </div>
                    )}
                    <input
                        id="backup-file"
                        type="file"
                        accept="application/json,.json"
                        className="sr-only"
                        onChange={handleFileChange}
                        disabled={isBusy}
                    />
                </div>

                {validation?.isValid && (
                    <div className="space-y-4">
                        <div className="flex flex-wrap items-center justify-between gap-2">
                            <div className="flex items-center gap-2">
                                <CheckCircle2 className="h-4 w-4 text-green-600 dark:text-green-400" />
                                <span className="text-sm font-medium">Backup valid</span>
                            </div>
                            <Badge variant="outline">Format v{validation.formatVersion}</Badge>
                        </div>

                        <SummaryGrid summary={validation.summary} />

                        <div className="flex justify-end">
                            <AlertDialog>
                                <AlertDialogTrigger asChild>
                                    <Button
                                        variant="destructive"
                                        disabled={isBusy || restoreResult?.restored === true}
                                    >
                                        {operation === "restoring" ? (
                                            <Loader2 className="animate-spin" />
                                        ) : (
                                            <ArchiveRestore />
                                        )}
                                        Restore backup
                                    </Button>
                                </AlertDialogTrigger>
                                <AlertDialogContent>
                                    <AlertDialogHeader>
                                        <AlertDialogTitle>Restore this backup?</AlertDialogTitle>
                                        <AlertDialogDescription>
                                            {validation.summary.gameCount.toLocaleString()} games and{" "}
                                            {validation.summary.platformItemCount.toLocaleString()} platform items
                                            will be written. The operation is accepted only when the target library is empty.
                                        </AlertDialogDescription>
                                    </AlertDialogHeader>
                                    <AlertDialogFooter>
                                        <AlertDialogCancel>Cancel</AlertDialogCancel>
                                        <AlertDialogAction
                                            onClick={() => void handleRestore()}
                                            className="bg-destructive text-white hover:bg-destructive/90"
                                        >
                                            Restore into empty library
                                        </AlertDialogAction>
                                    </AlertDialogFooter>
                                </AlertDialogContent>
                            </AlertDialog>
                        </div>
                    </div>
                )}

                {validation && !validation.isValid && (
                    <div className="rounded-lg border border-destructive/40 bg-destructive/10 p-4" role="alert">
                        <div className="flex items-center gap-2 text-destructive">
                            <AlertCircle className="h-4 w-4" />
                            <p className="text-sm font-semibold">Backup validation failed</p>
                        </div>
                        <ul className="mt-3 space-y-1 text-sm text-destructive/90">
                            {validation.errors.slice(0, 5).map((error, index) => (
                                <li key={`${error.code}-${error.path ?? index}`}>
                                    {error.path ? `${error.path}: ` : ""}{error.message}
                                </li>
                            ))}
                        </ul>
                        {validation.errors.length > 5 && (
                            <p className="mt-2 text-xs text-destructive/80">
                                {validation.errors.length - 5} additional validation errors
                            </p>
                        )}
                    </div>
                )}

                {notice && (
                    <div
                        className={
                            notice.kind === "success"
                                ? "flex items-center gap-2 rounded-lg border border-green-600/30 bg-green-600/10 p-3 text-sm text-green-700 dark:text-green-300"
                                : "flex items-center gap-2 rounded-lg border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive"
                        }
                        role={notice.kind === "error" ? "alert" : "status"}
                        aria-live="polite"
                    >
                        {notice.kind === "success" ? (
                            <CheckCircle2 className="h-4 w-4 shrink-0" />
                        ) : (
                            <AlertCircle className="h-4 w-4 shrink-0" />
                        )}
                        <span>{notice.message}</span>
                    </div>
                )}
            </section>
        </div>
    );
}
