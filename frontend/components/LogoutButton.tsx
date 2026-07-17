import { LogOut } from "lucide-react";
import { Button } from "@/components/ui/button";

/** Submits a same-origin top-level logout navigation; the server chooses the safe destination. */
export function LogoutButton({ mobile = false }: { mobile?: boolean }) {
    return (
        <form action="/api/auth/logout" method="post" className={mobile ? "w-full" : undefined}>
            <Button
                type="submit"
                variant="ghost"
                size={mobile ? "default" : "icon-sm"}
                className={mobile ? "mt-2 w-full justify-start" : "text-muted-foreground"}
                aria-label="Sign out"
            >
                <LogOut />
                {mobile && <span>Sign out</span>}
            </Button>
        </form>
    );
}
