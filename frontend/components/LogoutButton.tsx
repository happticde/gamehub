"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { Loader2, LogOut } from "lucide-react";
import { Button } from "@/components/ui/button";

/** Ends the server-side refresh session, clears HttpOnly cookies, and returns to login. */
export function LogoutButton({ mobile = false }: { mobile?: boolean }) {
    const router = useRouter();
    const [isSubmitting, setIsSubmitting] = useState(false);

    async function logout() {
        setIsSubmitting(true);
        try {
            await fetch("/api/auth/logout", { method: "POST" });
        } finally {
            // Local cookies are cleared even when Core is unavailable, so navigation should
            // always leave the authenticated UI rather than trapping the user on an error.
            router.replace("/login");
            router.refresh();
        }
    }

    return (
        <Button
            type="button"
            variant="ghost"
            size={mobile ? "default" : "icon-sm"}
            className={mobile ? "mt-2 w-full justify-start" : "text-muted-foreground"}
            onClick={logout}
            disabled={isSubmitting}
            aria-label="Sign out"
        >
            {isSubmitting ? <Loader2 className="animate-spin" /> : <LogOut />}
            {mobile && <span>{isSubmitting ? "Signing out…" : "Sign out"}</span>}
        </Button>
    );
}
