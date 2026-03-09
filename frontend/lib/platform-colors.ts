/**
 * Maps a platform name to distinct Tailwind brand-color classes.
 * Used by GameCard and the Game Detail page for consistent platform badges.
 */
export function getPlatformColor(platform: string): string {
    const p = platform.toLowerCase();
    if (p.includes("steam")) return "bg-blue-600 text-white";
    if (p.includes("psn") || p.includes("playstation")) return "bg-indigo-600 text-white";
    if (p.includes("gog")) return "bg-purple-600 text-white";
    if (p.includes("epic")) return "bg-zinc-800 text-white dark:bg-zinc-300 dark:text-black";
    if (p.includes("xbox")) return "bg-green-600 text-white";
    if (p.includes("nintendo") || p.includes("switch")) return "bg-red-600 text-white";
    return "bg-zinc-500 text-white";
}
