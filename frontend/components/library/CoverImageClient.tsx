"use client";

import Image from "next/image";
import { useState } from "react";
import { Maximize2 } from "lucide-react";
import { ImageLightbox } from "./ImageLightbox";

interface CoverImageClientProps {
    coverUrl: string | null;
    title: string;
}

export default function CoverImageClient({ coverUrl, title }: CoverImageClientProps) {
    const [isOpen, setIsOpen] = useState(false);

    if (!coverUrl) {
        return (
            <div className="hidden shrink-0 overflow-hidden rounded-lg border-2 border-border/50 bg-muted shadow-2xl md:flex items-center justify-center aspect-[3/4] w-48 lg:w-56 translate-y-12 text-zinc-500">
                No Cover
            </div>
        );
    }

    return (
        <>
            <div 
                onClick={() => setIsOpen(true)}
                className="hidden shrink-0 overflow-hidden rounded-lg border-2 border-border/50 bg-muted shadow-2xl md:block relative aspect-[3/4] w-48 lg:w-56 translate-y-12 cursor-zoom-in group transition-transform hover:scale-[1.02] active:scale-[0.98] duration-300"
            >
                <Image
                    src={coverUrl}
                    alt={`${title} cover`}
                    fill
                    className="object-cover transition-filter duration-500 group-hover:brightness-75"
                    unoptimized
                />
                <div className="absolute inset-0 flex items-center justify-center opacity-0 group-hover:opacity-100 transition-opacity duration-300">
                    <div className="bg-white/10 backdrop-blur-md p-2.5 rounded-full border border-white/20">
                        <Maximize2 className="h-5 w-5 text-white" />
                    </div>
                </div>
            </div>

            <ImageLightbox 
                images={[coverUrl]} 
                open={isOpen} 
                onOpenChange={setIsOpen} 
            />
        </>
    );
}
