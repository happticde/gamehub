"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { cn } from "@/lib/utils";

export default function Navbar() {
    const pathname = usePathname();

    return (
        <nav className="sticky top-0 z-50 w-full border-b border-border/40 bg-background/95 backdrop-blur supports-[backdrop-filter]:bg-background/60">
            <div className="container px-8 flex h-16 max-w-screen-2xl items-center">
                <div className="mr-8 flex items-center">
                    <Link href="/" className="mr-8 flex items-center space-x-2">
                        <span className="text-xl font-bold tracking-tight text-primary">GameHub</span>
                    </Link>
                    <div className="flex gap-6 text-sm font-medium">
                        <Link
                            href="/library"
                            className={cn(
                                "transition-colors hover:text-foreground",
                                pathname.startsWith("/library") || pathname === "/"
                                    ? "text-foreground"
                                    : "text-muted-foreground"
                            )}
                        >
                            Unified Library
                        </Link>
                        <Link
                            href="/stats"
                            className={cn(
                                "transition-colors hover:text-foreground",
                                pathname.startsWith("/stats")
                                    ? "text-foreground"
                                    : "text-muted-foreground"
                            )}
                        >
                            Stats
                        </Link>
                        <Link
                            href="/admin"
                            className={cn(
                                "transition-colors hover:text-foreground",
                                pathname.startsWith("/admin")
                                    ? "text-foreground"
                                    : "text-muted-foreground"
                            )}
                        >
                            Admin
                        </Link>
                    </div>
                </div>
            </div>
        </nav>
    );
}
