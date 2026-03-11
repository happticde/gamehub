"use client";

/**
 * ThemeProvider — manages dark/light mode across the app.
 *
 * How it works:
 *   1. On mount, reads the saved theme from localStorage (default: "dark")
 *   2. Applies the "dark" class on <html> when theme is "dark"
 *   3. Exposes `theme` and `setTheme` via React Context
 *   4. Persists changes to localStorage so the preference survives reloads
 *
 * Why manual instead of next-themes?
 *   We avoid adding a dependency for a simple toggle. This is ~40 lines
 *   of code vs pulling in a package.
 */

import { createContext, useContext, useEffect, useState } from "react";

type Theme = "light" | "dark";

interface ThemeContextType {
    theme: Theme;
    setTheme: (theme: Theme) => void;
}

const ThemeContext = createContext<ThemeContextType>({
    theme: "dark",
    setTheme: () => {},
});

/** Hook to access the current theme and toggle function */
export function useTheme() {
    return useContext(ThemeContext);
}

export function ThemeProvider({ children }: { children: React.ReactNode }) {
    // Initialize with "dark" — we'll read localStorage on mount
    const [theme, setThemeState] = useState<Theme>("dark");
    const [mounted, setMounted] = useState(false);

    // Read saved preference on first mount
    useEffect(() => {
        const saved = localStorage.getItem("gamehub-theme") as Theme | null;
        if (saved === "light" || saved === "dark") {
            setThemeState(saved);
        }
        setMounted(true);
    }, []);

    // Apply the class on <html> whenever theme changes
    useEffect(() => {
        if (!mounted) return;
        const root = document.documentElement;
        if (theme === "dark") {
            root.classList.add("dark");
        } else {
            root.classList.remove("dark");
        }
        localStorage.setItem("gamehub-theme", theme);
    }, [theme, mounted]);

    const setTheme = (newTheme: Theme) => {
        setThemeState(newTheme);
    };

    return (
        <ThemeContext.Provider value={{ theme, setTheme }}>
            {children}
        </ThemeContext.Provider>
    );
}
