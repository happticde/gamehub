"use client";

import Image from "next/image";
import { useState, useRef, useEffect } from "react";
import { ChevronLeft, ChevronRight, Maximize2 } from "lucide-react";
import { ImageLightbox } from "./ImageLightbox";

interface ScreenshotCarouselProps {
    screenshots: string[];
}

export default function ScreenshotCarousel({ screenshots }: ScreenshotCarouselProps) {
    const scrollRef = useRef<HTMLDivElement>(null);
    const [activeIndex, setActiveIndex] = useState(0);
    const [lightboxOpen, setLightboxOpen] = useState(false);
    const [lightboxIndex, setLightboxIndex] = useState(0);

    if (!screenshots || screenshots.length === 0) return null;

    const handleScroll = () => {
        if (!scrollRef.current) return;
        const scrollWidth = scrollRef.current.offsetWidth;
        const scrollLeft = scrollRef.current.scrollLeft;
        const index = Math.round(scrollLeft / scrollWidth);
        setActiveIndex(index);
    };

    const openLightbox = (index: number) => {
        setLightboxIndex(index);
        setLightboxOpen(true);
    };

    return (
        <section className="space-y-4">
            <h2 className="text-2xl font-bold tracking-tight mb-4">Gallery</h2>
            
            {/* Mobile Carousel View (md:hidden) */}
            <div className="md:hidden relative group">
                <div 
                    ref={scrollRef}
                    onScroll={handleScroll}
                    className="flex w-full snap-x snap-mandatory overflow-x-auto gap-0 no-scrollbar rounded-xl border border-border bg-black"
                    style={{ scrollbarWidth: 'none', msOverflowStyle: 'none' }}
                >
                    {screenshots.map((url, idx) => (
                        <div 
                            key={url} 
                            onClick={() => openLightbox(idx)}
                            className="relative aspect-video w-full flex-shrink-0 snap-center overflow-hidden active:opacity-90"
                        >
                            <Image
                                src={url}
                                alt={`Screenshot ${idx + 1}`}
                                fill
                                className="object-contain"
                                unoptimized
                                priority={idx === 0}
                            />
                            {/* Gradient Overlay for better contrast on index */}
                            <div className="absolute top-3 right-3 bg-black/50 backdrop-blur-md px-2 py-1 rounded text-[10px] font-bold text-white border border-white/10 uppercase tracking-widest">
                                {idx + 1} / {screenshots.length}
                            </div>
                        </div>
                    ))}
                </div>
                
                {/* Visual Pagination Dots */}
                <div className="flex justify-center gap-1.5 mt-2">
                    {screenshots.length > 1 && screenshots.map((_, idx) => (
                        <div 
                            key={idx} 
                            className={`h-1.5 transition-all duration-300 rounded-full ${
                                idx === activeIndex ? "w-4 bg-primary" : "w-1.5 bg-muted-foreground/30"
                            }`} 
                        />
                    ))}
                </div>
            </div>

            {/* Desktop Grid View (hidden md:grid) */}
            <div className="hidden md:grid grid-cols-2 lg:grid-cols-3 gap-4">
                {screenshots.map((url, idx) => (
                    <div 
                        key={url} 
                        onClick={() => openLightbox(idx)}
                        className="relative aspect-video w-full overflow-hidden rounded-xl bg-muted border border-border group/item shadow-sm cursor-zoom-in group"
                    >
                        <Image
                            src={url}
                            alt={`Screenshot ${idx + 1}`}
                            fill
                            className="object-cover transition-transform duration-700 group-hover/item:scale-110"
                            unoptimized
                        />
                        {/* Hover Overlay */}
                        <div className="absolute inset-0 bg-black/40 opacity-0 group-hover/item:opacity-100 transition-all duration-300 flex items-center justify-center">
                            <div className="bg-white/10 backdrop-blur-md p-3 rounded-full border border-white/20 transform translate-y-4 group-hover/item:translate-y-0 transition-transform duration-300">
                                <Maximize2 className="h-6 w-6 text-white" />
                            </div>
                        </div>
                    </div>
                ))}
            </div>

            {/* Lightbox Component */}
            <ImageLightbox 
                images={screenshots} 
                initialIndex={lightboxIndex} 
                open={lightboxOpen} 
                onOpenChange={setLightboxOpen} 
            />
        </section>
    );
}
