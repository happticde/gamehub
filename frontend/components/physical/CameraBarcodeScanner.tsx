"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { usePathname } from "next/navigation";
import { Camera, Loader2, ScanBarcode } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
    Dialog,
    DialogContent,
    DialogDescription,
    DialogFooter,
    DialogHeader,
    DialogTitle,
} from "@/components/ui/dialog";
import {
    startPhysicalBarcodeDecoder,
    type PhysicalBarcodeDecoderSession,
} from "@/lib/physical-barcode-decoder";

interface CameraBarcodeScannerProps {
    disabled?: boolean;
    onDetected(rawValue: string): void;
}

export function CameraBarcodeScanner({ disabled, onDetected }: CameraBarcodeScannerProps) {
    const pathname = usePathname();
    const videoRef = useRef<HTMLVideoElement>(null);
    const streamRef = useRef<MediaStream | null>(null);
    const decoderRef = useRef<PhysicalBarcodeDecoderSession | null>(null);
    const startGeneration = useRef(0);
    const [open, setOpen] = useState(false);
    const [starting, setStarting] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const stopCamera = useCallback(() => {
        startGeneration.current += 1;
        decoderRef.current?.stop();
        decoderRef.current = null;
        streamRef.current?.getTracks().forEach((track) => track.stop());
        streamRef.current = null;
        if (videoRef.current) videoRef.current.srcObject = null;
        setStarting(false);
    }, []);

    const startCamera = async () => {
        if (disabled || starting) return;

        setOpen(true);
        setStarting(true);
        setError(null);
        const generation = startGeneration.current + 1;
        startGeneration.current = generation;

        if (!window.isSecureContext) {
            setError("Camera scanning requires HTTPS or a secure localhost context.");
            setStarting(false);
            return;
        }

        if (!navigator.mediaDevices?.getUserMedia) {
            setError("This browser does not expose a camera capture API. Enter the GTIN manually instead.");
            setStarting(false);
            return;
        }

        try {
            const stream = await navigator.mediaDevices.getUserMedia({
                video: { facingMode: { ideal: "environment" } },
                audio: false,
            });
            if (startGeneration.current !== generation) {
                stream.getTracks().forEach((track) => track.stop());
                return;
            }

            streamRef.current = stream;
            await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
            const video = videoRef.current;
            if (!video || startGeneration.current !== generation) {
                stopCamera();
                return;
            }

            const decoder = await startPhysicalBarcodeDecoder(video, stream, ({ rawValue }) => {
                onDetected(rawValue);
                stopCamera();
                setOpen(false);
            });
            if (startGeneration.current !== generation) {
                decoder.stop();
                return;
            }

            decoderRef.current = decoder;
            setStarting(false);
        } catch (caughtError: unknown) {
            stopCamera();
            setError(cameraErrorMessage(caughtError));
        }
    };

    useEffect(() => stopCamera, [stopCamera]);
    useEffect(() => stopCamera, [pathname, stopCamera]);

    return (
        <>
            <Button type="button" variant="outline" onClick={() => void startCamera()} disabled={disabled}>
                <Camera /> Scan barcode
            </Button>

            <Dialog
                open={open}
                onOpenChange={(nextOpen) => {
                    if (!nextOpen) stopCamera();
                    setOpen(nextOpen);
                }}
            >
                <DialogContent className="w-[calc(100%-1.5rem)] sm:max-w-lg" showCloseButton={!starting}>
                    <DialogHeader>
                        <DialogTitle className="flex items-center gap-2">
                            <ScanBarcode className="h-5 w-5" /> Scan UPC-A or EAN-13
                        </DialogTitle>
                        <DialogDescription>
                            Point the rear camera at the case barcode. Frames stay on this device and are never uploaded.
                        </DialogDescription>
                    </DialogHeader>

                    <div className="relative aspect-[4/3] overflow-hidden rounded-xl border bg-black">
                        <video
                            ref={videoRef}
                            className="h-full w-full object-cover"
                            muted
                            playsInline
                            aria-label="Live camera preview for barcode scanning"
                        />
                        {starting && !error && (
                            <div className="absolute inset-0 flex items-center justify-center bg-black/65 text-sm text-white">
                                <Loader2 className="mr-2 h-4 w-4 animate-spin" /> Starting camera…
                            </div>
                        )}
                    </div>

                    {error && (
                        <p role="alert" className="rounded-lg border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">
                            {error}
                        </p>
                    )}

                    <DialogFooter>
                        <Button
                            type="button"
                            variant="outline"
                            onClick={() => {
                                stopCamera();
                                setOpen(false);
                            }}
                        >
                            Cancel
                        </Button>
                    </DialogFooter>
                </DialogContent>
            </Dialog>
        </>
    );
}

function cameraErrorMessage(error: unknown) {
    if (!(error instanceof DOMException)) {
        return "The camera scanner could not start. Enter the GTIN manually instead.";
    }

    switch (error.name) {
        case "NotAllowedError":
        case "SecurityError":
            return "Camera permission was denied. Allow camera access or enter the GTIN manually.";
        case "NotFoundError":
        case "OverconstrainedError":
            return "No suitable camera was found on this device.";
        case "NotReadableError":
            return "The camera is already in use or unavailable. Close other camera apps and try again.";
        case "AbortError":
            return "Camera capture was interrupted. You can try again or enter the GTIN manually.";
        default:
            return "The camera scanner could not start. Enter the GTIN manually instead.";
    }
}
