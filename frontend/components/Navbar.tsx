"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useState } from "react";
import { cn } from "@/lib/utils";
import { Menu, X } from "lucide-react";

/** Navigation links shared between desktop and mobile menus */
const NAV_LINKS = [
    { href: "/library", label: "Unified Library", match: (p: string) => p.startsWith("/library") || p === "/" },
    { href: "/stats", label: "Stats", match: (p: string) => p.startsWith("/stats") },
    { href: "/admin", label: "Admin", match: (p: string) => p.startsWith("/admin") },
] as const;

export default function Navbar() {
    const pathname = usePathname();
    const [mobileOpen, setMobileOpen] = useState(false);

    // Auto-close mobile menu on route change
    useEffect(() => {
        setMobileOpen(false);
    }, [pathname]);

    // Prevent body scrolling when mobile menu is open
    useEffect(() => {
        if (mobileOpen) {
            document.body.style.overflow = "hidden";
        } else {
            document.body.style.overflow = "";
        }
        return () => { document.body.style.overflow = ""; };
    }, [mobileOpen]);

    return (
        <nav className="sticky top-0 z-50 w-full border-b border-border/40 bg-background/95 backdrop-blur supports-[backdrop-filter]:bg-background/60">
            <div className="container px-4 md:px-8 flex h-16 max-w-screen-2xl items-center justify-between">
                {/* Logo */}
                <div className="flex items-center">
                    <Link href="/" className="flex items-center space-x-2" onClick={() => setMobileOpen(false)}>
                        <span className="text-xl font-bold tracking-tight text-primary">GameHub</span>
                    </Link>
                </div>

                {/* Desktop nav links — hidden on mobile */}
                <div className="hidden md:flex gap-6 text-sm font-medium">
                    {NAV_LINKS.map((link) => (
                        <Link
                            key={link.href}
                            href={link.href}
                            className={cn(
                                "transition-colors hover:text-foreground",
                                link.match(pathname)
                                    ? "text-foreground"
                                    : "text-muted-foreground"
                            )}
                        >
                            {link.label}
                        </Link>
                    ))}
                </div>

                {/* Mobile hamburger button — visible on small screens */}
                <button
                    type="button"
                    className="md:hidden flex h-10 w-10 items-center justify-center rounded-md text-muted-foreground hover:text-foreground hover:bg-accent transition-colors"
                    onClick={() => setMobileOpen(!mobileOpen)}
                    aria-label={mobileOpen ? "Close menu" : "Open menu"}
                    aria-expanded={mobileOpen}
                >
                    {mobileOpen ? <X className="h-5 w-5" /> : <Menu className="h-5 w-5" />}
                </button>
            </div>

            {/* Mobile menu overlay */}
            {mobileOpen && (
                <div className="md:hidden border-t border-border/40 bg-background/98 backdrop-blur animate-in slide-in-from-top-2 fade-in duration-200">
                    <div className="container px-4 py-4 flex flex-col gap-1">
                        {NAV_LINKS.map((link) => (
                            <Link
                                key={link.href}
                                href={link.href}
                                className={cn(
                                    "flex items-center rounded-lg px-4 py-3 text-sm font-medium transition-colors",
                                    link.match(pathname)
                                        ? "bg-accent text-foreground"
                                        : "text-muted-foreground hover:bg-accent/50 hover:text-foreground"
                                )}
                            >
                                {link.label}
                            </Link>
                        ))}
                    </div>
                </div>
            )}
        </nav>
    );
}
