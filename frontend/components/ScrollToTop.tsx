"use client";

/**
 * ScrollToTop — floating button that appears after scrolling down.
 *
 * Behaviour:
 *   - Hidden when near the top of the page (< 400px scroll)
 *   - Fades in when the user scrolls past the threshold
 *   - Smooth-scrolls back to the top on click
 *   - Positioned bottom-right with a subtle glassmorphism effect
 */

import { useEffect, useState } from "react";
import { ArrowUp } from "lucide-react";

const SCROLL_THRESHOLD = 400;

export function ScrollToTop() {
    const [visible, setVisible] = useState(false);

    useEffect(() => {
        const handleScroll = () => {
            setVisible(window.scrollY > SCROLL_THRESHOLD);
        };

        window.addEventListener("scroll", handleScroll, { passive: true });
        return () => window.removeEventListener("scroll", handleScroll);
    }, []);

    const scrollToTop = () => {
        window.scrollTo({ top: 0, behavior: "smooth" });
    };

    return (
        <button
            type="button"
            onClick={scrollToTop}
            className={`
                fixed bottom-6 right-6 z-40
                flex h-11 w-11 items-center justify-center
                rounded-full border border-border/50
                bg-background/80 backdrop-blur-sm
                text-muted-foreground hover:text-foreground
                shadow-lg hover:shadow-xl
                transition-all duration-300
                ${visible
                    ? "opacity-100 translate-y-0 pointer-events-auto"
                    : "opacity-0 translate-y-4 pointer-events-none"
                }
            `}
            aria-label="Scroll to top"
        >
            <ArrowUp className="h-5 w-5" />
        </button>
    );
}
