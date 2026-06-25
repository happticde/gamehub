"use client";

/**
 * ThemeProvider — manages dark/light mode across the app.
 *
 * How it works:
 *   1. Reads the saved theme from localStorage through a hydration-safe external store
 *   2. Applies the "dark" class on <html> when theme is "dark"
 *   3. Exposes `theme` and `setTheme` via React Context
 *   4. Persists changes to localStorage so the preference survives reloads
 *
 * Why manual instead of next-themes?
 *   We avoid adding a dependency for a simple toggle. This is ~40 lines
 *   of code vs pulling in a package.
 */

import { createContext, useContext, useEffect, useSyncExternalStore } from "react";

export type Theme = "light" | "dark" | "retro-terminal";

const DEFAULT_THEME: Theme = "dark";
const THEME_STORAGE_KEY = "gamehub-theme";
const THEME_CHANGE_EVENT = "gamehub:theme-change";
const THEMES: readonly Theme[] = ["light", "dark", "retro-terminal"];

function isTheme(value: string | null): value is Theme {
    return value !== null && THEMES.some((theme) => theme === value);
}

function getStoredTheme(): Theme {
    const savedTheme = localStorage.getItem(THEME_STORAGE_KEY);
    return isTheme(savedTheme) ? savedTheme : DEFAULT_THEME;
}

function subscribeToTheme(onStoreChange: () => void): () => void {
    const handleStorage = (event: StorageEvent) => {
        if (event.key === THEME_STORAGE_KEY) onStoreChange();
    };

    window.addEventListener("storage", handleStorage);
    window.addEventListener(THEME_CHANGE_EVENT, onStoreChange);

    return () => {
        window.removeEventListener("storage", handleStorage);
        window.removeEventListener(THEME_CHANGE_EVENT, onStoreChange);
    };
}

const getServerThemeSnapshot = () => DEFAULT_THEME;

interface ThemeContextType {
    theme: Theme;
    setTheme: (theme: Theme) => void;
}

const ThemeContext = createContext<ThemeContextType>({
    theme: DEFAULT_THEME,
    setTheme: () => {},
});

/** Hook to access the current theme and toggle function */
export function useTheme() {
    return useContext(ThemeContext);
}

export function ThemeProvider({ children }: { children: React.ReactNode }) {
    const theme = useSyncExternalStore(
        subscribeToTheme,
        getStoredTheme,
        getServerThemeSnapshot
    );

    // Apply the class on <html> whenever theme changes
    useEffect(() => {
        const root = document.documentElement;
        
        // Clean up previous theme classes
        root.classList.remove("light", "dark", "retro-terminal");
        
        // Add the current theme class
        root.classList.add(theme);
    }, [theme]);

    const setTheme = (newTheme: Theme) => {
        localStorage.setItem(THEME_STORAGE_KEY, newTheme);
        window.dispatchEvent(new Event(THEME_CHANGE_EVENT));
    };

    return (
        <ThemeContext.Provider value={{ theme, setTheme }}>
            {children}
        </ThemeContext.Provider>
    );
}
