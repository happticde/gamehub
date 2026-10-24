"use client";

import * as React from "react";
import Image from "next/image";
import { ChevronLeft, ChevronRight, X } from "lucide-react";
import {
    Dialog,
    DialogContent,
    DialogTitle,
} from "@/components/ui/dialog";

interface ImageLightboxProps {
    images: string[];
    initialIndex?: number;
    open: boolean;
    onOpenChange: (open: boolean) => void;
}

export function ImageLightbox({
    images,
    initialIndex = 0,
    open,
    onOpenChange,
}: ImageLightboxProps) {
    const [currentIndex, setCurrentIndex] = React.useState(initialIndex);
    const [previousInput, setPreviousInput] = React.useState({ open, initialIndex });

    if (previousInput.open !== open || previousInput.initialIndex !== initialIndex) {
        setPreviousInput({ open, initialIndex });
        if (open) {
            setCurrentIndex(initialIndex);
        }
    }

    const handlePrevious = React.useCallback((e?: React.MouseEvent) => {
        e?.stopPropagation();
        setCurrentIndex((prev) => (prev > 0 ? prev - 1 : images.length - 1));
    }, [images.length]);

    const handleNext = React.useCallback((e?: React.MouseEvent) => {
        e?.stopPropagation();
        setCurrentIndex((prev) => (prev < images.length - 1 ? prev + 1 : 0));
    }, [images.length]);

    // Keyboard support
    React.useEffect(() => {
        const handleKeyDown = (e: KeyboardEvent) => {
            if (!open) return;
            if (e.key === "ArrowLeft") handlePrevious();
            if (e.key === "ArrowRight") handleNext();
        };
        window.addEventListener("keydown", handleKeyDown);
        return () => window.removeEventListener("keydown", handleKeyDown);
    }, [open, handlePrevious, handleNext]);

    if (!images || images.length === 0) return null;

    return (
        <Dialog open={open} onOpenChange={onOpenChange}>
            <DialogContent 
                className="max-w-[100vw] w-screen h-screen border-none bg-black/95 p-0 sm:max-w-[100vw] rounded-none outline-none shadow-none z-[100]"
                overlayClassName="bg-black/90 backdrop-blur-xl"
                showCloseButton={false}
            >
                <DialogTitle className="sr-only">Game screenshots</DialogTitle>
                <div className="relative flex h-full w-full items-center justify-center">
                    {/* Close Button */}
                    <button
                        aria-label="Close screenshots"
                        onClick={() => onOpenChange(false)}
                        className="fixed right-6 top-6 z-50 rounded-full bg-black/40 p-3 text-white/70 backdrop-blur-md transition-all hover:bg-black/60 hover:text-white hover:scale-110 active:scale-95 border border-white/10"
                    >
                        <X className="h-6 w-6" />
                    </button>

                    {/* Navigation Buttons */}
                    {images.length > 1 && (
                        <>
                            <button
                                aria-label="Previous screenshot"
                                onClick={handlePrevious}
                                className="fixed left-6 z-20 rounded-full bg-black/40 p-4 text-white/70 backdrop-blur-md transition-all hover:bg-black/60 hover:text-white hover:scale-110 active:scale-95 border border-white/10"
                            >
                                <ChevronLeft className="h-8 w-8" />
                            </button>
                            <button
                                aria-label="Next screenshot"
                                onClick={handleNext}
                                className="fixed right-6 z-20 rounded-full bg-black/40 p-4 text-white/70 backdrop-blur-md transition-all hover:bg-black/60 hover:text-white hover:scale-110 active:scale-95 border border-white/10"
                            >
                                <ChevronRight className="h-8 w-8" />
                            </button>
                        </>
                    )}

                    {/* Image Area */}
                    <div className="relative h-[85vh] w-[90vw] animate-in fade-in zoom-in-95 duration-500 ease-out flex items-center justify-center">
                        <Image
                            key={currentIndex}
                            src={images[currentIndex]}
                            alt={`Image ${currentIndex + 1}`}
                            fill
                            className="object-contain"
                            unoptimized
                            priority
                        />
                    </div>

                    {/* Counter UI */}
                    {images.length > 1 && (
                        <div className="fixed bottom-8 left-1/2 -translate-x-1/2 rounded-full bg-black/40 px-6 py-2 text-sm font-medium text-white/80 backdrop-blur-md border border-white/10 tracking-widest uppercase">
                            {currentIndex + 1} <span className="mx-2 text-white/30">/</span> {images.length}
                        </div>
                    )}
                </div>
            </DialogContent>
        </Dialog>
    );
}
