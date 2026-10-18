import { Gamepad2 } from "lucide-react";

/**
 * Footer — minimal site footer with branding.
 *
 * Keeps a consistent border-t styling with the Navbar.
 * Stacks vertically on mobile, row on desktop.
 */
export function Footer() {
    return (
        <footer className="border-t border-border/40 bg-background/95">
            <div className="container mx-auto px-4 md:px-8 py-6 max-w-screen-2xl">
                <div className="flex flex-col sm:flex-row items-center justify-between gap-4 text-sm text-muted-foreground">
                    <div className="flex items-center gap-2">
                        <Gamepad2 className="h-4 w-4 text-primary" />
                        <span>
                            <span className="font-semibold text-foreground">GameHub</span>
                            {" · "}Unified Gaming Library
                        </span>
                    </div>
                    <div className="flex items-center gap-4">
                        <span>© {new Date().getFullYear()}</span>
                        <a
                            href="https://github.com/jfk1987/gamehub"
                            target="_blank"
                            rel="noopener noreferrer"
                            className="hover:text-foreground transition-colors"
                        >
                            GitHub
                        </a>
                    </div>
                </div>
            </div>
        </footer>
    );
}
