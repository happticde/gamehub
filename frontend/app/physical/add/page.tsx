import Link from "next/link";
import { ArrowLeft } from "lucide-react";
import { PhysicalGameCapture } from "@/components/physical/PhysicalGameCapture";

export default function AddPhysicalGamePage() {
    return (
        <div className="container mx-auto max-w-5xl px-4 py-6 md:px-8 md:py-10">
            <Link href="/library" className="mb-5 inline-flex items-center gap-2 text-sm text-muted-foreground hover:text-foreground">
                <ArrowLeft className="h-4 w-4" /> Back to library
            </Link>
            <div className="mb-7">
                <h1 className="text-3xl font-bold tracking-tight md:text-4xl">Add physical game</h1>
                <p className="mt-2 text-muted-foreground">
                    Find the game on IGDB, choose PS3, PS4, or PS5, and review everything before GameHub writes.
                </p>
            </div>
            <PhysicalGameCapture />
        </div>
    );
}
