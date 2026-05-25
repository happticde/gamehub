"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useState } from "react";
import { cn } from "@/lib/utils";
import { Menu, X, Sun, Moon, Terminal } from "lucide-react";
import { useTheme } from "@/components/ThemeProvider";
import GlobalSearchBar from "@/components/GlobalSearchBar";

/** Navigation links shared between desktop and mobile menus */
const NAV_LINKS = [
    { href: "/library", label: "Unified Library", match: (p: string) => p.startsWith("/library") || p === "/" },
    { href: "/stats", label: "Stats", match: (p: string) => p.startsWith("/stats") },
    { href: "/admin", label: "Admin", match: (p: string) => p.startsWith("/admin") },
] as const;

export default function Navbar() {
    const pathname = usePathname();
    const { theme, setTheme } = useTheme();
    const [mobileOpen, setMobileOpen] = useState(false);
    const [desktopThemeOpen, setDesktopThemeOpen] = useState(false);
    const [mobileThemeOpen, setMobileThemeOpen] = useState(false);

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

    const getThemeIcon = (t: string, sizeClass = "h-4 w-4") => {
        if (t === "retro-terminal") return <Terminal className={sizeClass} />;
        if (t === "light") return <Sun className={sizeClass} />;
        return <Moon className={sizeClass} />;
    };

    return (
        <nav className="sticky top-0 z-50 w-full border-b border-border/40 bg-background/95 backdrop-blur supports-[backdrop-filter]:bg-background/60">
            <div className="container px-4 md:px-8 flex h-16 max-w-screen-2xl items-center justify-between">
                {/* Logo */}
                <div className="flex items-center">
                    <Link href="/" className="flex items-center space-x-2" onClick={() => setMobileOpen(false)}>
                        <span className="text-xl font-bold tracking-tight text-primary">GameHub</span>
                    </Link>
                </div>

                {/* Desktop Global Search Bar - Top Middle */}
                <div className="hidden md:flex flex-1 justify-center max-w-md mx-4">
                    <GlobalSearchBar />
                </div>

                {/* Desktop nav links — hidden on mobile */}
                <div className="hidden md:flex items-center gap-6 text-sm font-medium">
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

                    {/* Desktop Theme Switcher */}
                    <div className="relative">
                        <button
                            type="button"
                            onClick={() => setDesktopThemeOpen(!desktopThemeOpen)}
                            className="flex h-9 w-9 items-center justify-center rounded-md text-muted-foreground hover:text-foreground hover:bg-accent transition-colors"
                            aria-label="Toggle theme selection"
                        >
                            {getThemeIcon(theme)}
                        </button>
                        {desktopThemeOpen && (
                            <>
                                <div className="fixed inset-0 z-30" onClick={() => setDesktopThemeOpen(false)} />
                                <div className="absolute right-0 mt-2 w-44 z-40 rounded-md border border-border bg-popover p-1 text-popover-foreground shadow-md animate-in fade-in duration-100">
                                    {(["light", "dark", "retro-terminal"] as const).map((t) => (
                                        <button
                                            key={t}
                                            type="button"
                                            onClick={() => {
                                                setTheme(t);
                                                setDesktopThemeOpen(false);
                                            }}
                                            className={cn(
                                                "flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-sm hover:bg-accent hover:text-accent-foreground transition-colors text-left",
                                                theme === t ? "text-primary font-semibold" : "text-muted-foreground"
                                            )}
                                        >
                                            {getThemeIcon(t, "h-3.5 w-3.5")}
                                            <span className="capitalize">{t === "retro-terminal" ? "Retro Terminal" : t + " Mode"}</span>
                                        </button>
                                    ))}
                                </div>
                            </>
                        )}
                    </div>
                </div>

                {/* Mobile right-side controls */}
                <div className="flex md:hidden items-center gap-1">
                    {/* Mobile Global Search Button */}
                    <GlobalSearchBar />

                    {/* Mobile Theme Switcher */}
                    <div className="relative">
                        <button
                            type="button"
                            onClick={() => setMobileThemeOpen(!mobileThemeOpen)}
                            className="flex h-10 w-10 items-center justify-center rounded-md text-muted-foreground hover:text-foreground hover:bg-accent transition-colors"
                            aria-label="Toggle theme selection"
                        >
                            {getThemeIcon(theme, "h-5 w-5")}
                        </button>
                        {mobileThemeOpen && (
                            <>
                                <div className="fixed inset-0 z-30" onClick={() => setMobileThemeOpen(false)} />
                                <div className="absolute right-0 mt-2 w-44 z-40 rounded-md border border-border bg-popover p-1 text-popover-foreground shadow-md animate-in fade-in duration-100">
                                    {(["light", "dark", "retro-terminal"] as const).map((t) => (
                                        <button
                                            key={t}
                                            type="button"
                                            onClick={() => {
                                                setTheme(t);
                                                setMobileThemeOpen(false);
                                            }}
                                            className={cn(
                                                "flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-sm hover:bg-accent hover:text-accent-foreground transition-colors text-left",
                                                theme === t ? "text-primary font-semibold" : "text-muted-foreground"
                                            )}
                                        >
                                            {getThemeIcon(t, "h-4 w-4")}
                                            <span className="capitalize">{t === "retro-terminal" ? "Retro Terminal" : t + " Mode"}</span>
                                        </button>
                                    ))}
                                </div>
                            </>
                        )}
                    </div>

                    {/* Mobile hamburger button */}
                    <button
                        type="button"
                        className="flex h-10 w-10 items-center justify-center rounded-md text-muted-foreground hover:text-foreground hover:bg-accent transition-colors"
                        onClick={() => setMobileOpen(!mobileOpen)}
                        aria-label={mobileOpen ? "Close menu" : "Open menu"}
                        aria-expanded={mobileOpen}
                    >
                        {mobileOpen ? <X className="h-5 w-5" /> : <Menu className="h-5 w-5" />}
                    </button>
                </div>
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
