import Link from "next/link";

export default function Navbar() {
    return (
        <nav className="sticky top-0 z-50 w-full border-b border-border/40 bg-background/95 backdrop-blur supports-[backdrop-filter]:bg-background/60">
            <div className="container px-8 flex h-16 max-w-screen-2xl items-center">
                <div className="mr-8 flex items-center">
                    <Link href="/" className="mr-8 flex items-center space-x-2">
                        <span className="text-xl font-bold tracking-tight text-primary">GameHub</span>
                    </Link>
                    <div className="flex gap-6 text-sm font-medium text-muted-foreground">
                        <Link
                            href="/library"
                            className="transition-colors hover:text-foreground text-foreground"
                        >
                            Unified Library
                        </Link>
                        <Link
                            href="/ingest"
                            className="transition-colors hover:text-foreground"
                        >
                            Ingest Sync
                        </Link>
                    </div>
                </div>
            </div>
        </nav>
    );
}
