import Link from "next/link";
import { Gamepad2, Library, Globe, Sparkles } from "lucide-react";
import { fetchCoreApi } from "@/lib/core-api";

/** Fetches basic library stats for the landing page counters */
async function getLibraryStats(): Promise<{ totalGames: number; totalPlatforms: number }> {
  try {
    // Fetch a minimal page just to get totalCount
    const res = await fetchCoreApi("/library?pageSize=1", {
      cache: "no-store",
    });
    if (res.ok) {
      const data = await res.json();
      return { totalGames: data.totalCount || 0, totalPlatforms: 4 };
    }
  } catch {
    // Silently fallback — landing page should never break
  }
  return { totalGames: 0, totalPlatforms: 4 };
}

/** Feature cards displayed below the hero */
const FEATURES = [
  {
    icon: Library,
    title: "Unified Library",
    description:
      "All your games from Steam, GOG, PSN, and Epic in one searchable, filterable collection.",
  },
  {
    icon: Sparkles,
    title: "IGDB Metadata",
    description:
      "Rich cover art, ratings, genres, screenshots, and developer info powered by IGDB.",
  },
  {
    icon: Globe,
    title: "Multi-Platform",
    description:
      "Cross-platform deduplication with slug-based matching. Merge and split entries as needed.",
  },
] as const;

export default async function Home() {
  const stats = await getLibraryStats();

  return (
    <div className="relative min-h-[calc(100vh-4rem)] flex items-center justify-center overflow-hidden w-full">
      
      {/* ============================================================ */}
      {/* 1. MODERN LAYOUT (Visible in light/dark mode, hidden in retro) */}
      {/* ============================================================ */}
      <div className="retro-terminal:hidden flex flex-col items-center justify-center w-full animate-in fade-in duration-500">
        {/* Animated gradient mesh background */}
        <div className="pointer-events-none absolute inset-0 -z-10 overflow-hidden">
          <div className="landing-gradient-orb landing-orb-1" />
          <div className="landing-gradient-orb landing-orb-2" />
          <div className="landing-gradient-orb landing-orb-3" />
        </div>

        {/* Hero Section */}
        <section className="mx-auto flex max-w-[980px] flex-col items-center gap-2 py-8 md:py-12 md:pb-8 lg:py-24 lg:pb-16 animate-in fade-in slide-in-from-bottom-4 duration-700">
          <div className="mb-4 inline-flex items-center gap-2 rounded-full border border-border/50 bg-muted/50 backdrop-blur px-4 py-1.5 text-sm text-muted-foreground">
            <Gamepad2 className="h-4 w-4" />
            <span>Your game collection, unified</span>
          </div>

          <h1 className="text-center text-4xl font-extrabold leading-tight tracking-tighter md:text-6xl lg:text-7xl lg:leading-[1.1]">
            Your ultimate unified{" "}
            <br className="hidden sm:inline" />
            <span className="bg-gradient-to-r from-indigo-400 via-purple-400 to-pink-400 bg-clip-text text-transparent">
              gaming library.
            </span>
          </h1>

          <p className="max-w-[750px] text-center text-lg text-muted-foreground sm:text-xl mt-4">
            GameHub seamlessly aggregates your Steam, GOG, and PlayStation
            libraries into one beautiful interface. Powered by IGDB metadata.
          </p>

          {/* Quick stats */}
          {stats.totalGames > 0 && (
            <div className="mt-6 flex items-center gap-8 text-center">
              <div>
                <div className="text-3xl font-bold tabular-nums text-foreground">
                  {stats.totalGames}
                </div>
                <div className="text-sm text-muted-foreground">Games</div>
              </div>
              <div className="h-8 w-px bg-border" />
              <div>
                <div className="text-3xl font-bold tabular-nums text-foreground">
                  {stats.totalPlatforms}
                </div>
                <div className="text-sm text-muted-foreground">Platforms</div>
              </div>
            </div>
          )}

          <div className="flex w-full items-center justify-center space-x-4 py-8 md:pb-10">
            <Link
              href="/library"
              className="inline-flex items-center justify-center whitespace-nowrap rounded-lg text-sm font-medium transition-all focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 bg-gradient-to-r from-indigo-500 to-purple-600 text-white shadow-lg shadow-indigo-500/25 hover:shadow-indigo-500/40 hover:scale-[1.02] active:scale-[0.98] h-11 px-8 py-2"
            >
              Enter Library
            </Link>
            <Link
              href="/admin"
              className="inline-flex items-center justify-center whitespace-nowrap rounded-lg text-sm font-medium ring-offset-background transition-all focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 border border-border bg-background/80 backdrop-blur hover:bg-accent hover:text-accent-foreground h-11 px-8 py-2"
            >
              Admin Dashboard
            </Link>
          </div>
        </section>

        {/* Feature Cards */}
        <section className="mx-auto grid max-w-5xl grid-cols-1 gap-6 px-6 pb-16 sm:grid-cols-3 animate-in fade-in slide-in-from-bottom-6 duration-700 delay-200 fill-mode-backwards">
          {FEATURES.map((feature, idx) => (
            <div
              key={feature.title}
              className="group relative rounded-xl border border-border/50 bg-card/50 backdrop-blur p-6 shadow-sm transition-all hover:shadow-md hover:border-border hover:-translate-y-1"
              style={{ animationDelay: `${200 + idx * 100}ms` }}
            >
              <div className="mb-4 flex h-10 w-10 items-center justify-center rounded-lg bg-gradient-to-br from-indigo-500/20 to-purple-500/20 text-indigo-400">
                <feature.icon className="h-5 w-5" />
              </div>
              <h3 className="text-lg font-semibold mb-2">{feature.title}</h3>
              <p className="text-sm text-muted-foreground leading-relaxed">
                {feature.description}
              </p>
            </div>
          ))}
        </section>
      </div>

      {/* ============================================================ */}
      {/* 2. RETRO ARCADE LAYOUT (Visible only in retro-terminal mode) */}
      {/* ============================================================ */}
      <div className="retro-terminal:block hidden w-full max-w-5xl mx-auto px-4 py-8 md:py-16 font-mono text-[#33ff33] animate-in fade-in duration-500">
        
        {/* Retro scanline grid overlay */}
        <div className="retro-terminal-grid" />

        {/* Outer Arcade Cabinet Border Screen */}
        <div className="border-4 border-[#33ff33] shadow-[0_0_20px_rgba(51,255,51,0.20)] bg-[#050705] p-6 md:p-10 relative overflow-hidden">
          
          {/* Epic ASCII Art GameHub Logo */}
          <pre className="text-[0.4rem] xs:text-[0.55rem] sm:text-[0.65rem] md:text-sm leading-none font-bold text-[#33ff33] text-center my-6 overflow-x-auto select-none tracking-tight py-2 px-1">
{`██████╗  █████╗ ███╗   ███╗███████╗██╗  ██╗██╗   ██╗██████╗ 
██╔════╝ ██╔══██╗████╗ ████║██╔════╝██║  ██║██║   ██║██╔══██╗
██║  ███╗███████║██╔████╔██║█████╗  ███████║██║   ██║██████╔╝
██║   ██║██╔══██║██║╚██╔╝██║██╔══╝  ██╔══██║██║   ██║██╔══██╗
╚██████╔╝██║  ██║██║ ╚═╝ ██║███████╗██║  ██║╚██████╔╝██████╔╝
 ╚═════╝ ╚═╝  ╚═╝╚═╝     ╚═╝╚══════╝╚═╝  ╚═╝ ╚═════╝ ╚═════╝ `}
          </pre>

          {/* Subtitle */}
          <h2 className="text-center text-xs md:text-sm font-extrabold tracking-widest text-[#ffb000] uppercase my-3">
            {"*** YOUR UNIFIED GAMING LIBRARY INDEX ***"}
          </h2>

          {/* Sleek integrated stat bar */}
          <div className="flex flex-wrap items-center justify-center gap-x-6 gap-y-2 text-xs font-bold uppercase tracking-wider text-[#33ff33] my-4 border-t border-b border-[#33ff33]/15 py-3 max-w-2xl mx-auto font-mono">
            <div>INDEXED: <span className="text-[#ffb000]">{stats.totalGames} GAMES</span></div>
            <span className="text-[#33ff33]/20 hidden sm:inline">|</span>
            <div>PLATFORMS: <span className="text-[#ffb000]">{stats.totalPlatforms} UNITS</span></div>
            <span className="text-[#33ff33]/20 hidden sm:inline">|</span>
            <div>STATUS: <span className="text-[#33ff33]">ONLINE</span></div>
          </div>

          <p className="max-w-[650px] mx-auto text-center text-xs md:text-sm text-[#33ff33]/80 leading-relaxed my-4">
            {"Seamlessly aggregating Steam, GOG, PlayStation, and Epic libraries into a unified monospaced console. Powered by secure IGDB provider index."}
          </p>

          {/* Dynamic Tactile Navigation Buttons */}
          <div className="flex flex-col sm:flex-row items-center justify-center gap-4 py-4 font-bold">
            <Link
              href="/library"
              className="border-2 border-[#33ff33] bg-[#33ff33] text-[#050705] hover:bg-transparent hover:text-[#33ff33] px-8 py-2.5 text-xs md:text-sm tracking-widest transition-all duration-200 uppercase select-none w-full sm:w-auto text-center"
            >
              [ ENTER ARCHIVE ]
            </Link>
            <Link
              href="/admin"
              className="border-2 border-[#ffb000] text-[#ffb000] hover:bg-[#ffb000] hover:text-[#050705] px-8 py-2.5 text-xs md:text-sm tracking-widest transition-all duration-200 uppercase select-none w-full sm:w-auto text-center"
            >
              [ ADMIN CONTROL ]
            </Link>
          </div>

          {/* Floppy Disk / Cartridge Feature Showcase */}
          <div className="grid grid-cols-1 sm:grid-cols-3 gap-6 my-8 pt-4 border-t border-[#33ff33]/20">
            {FEATURES.map((feature, idx) => (
              <div
                key={feature.title}
                className="border-2 border-[#33ff33]/30 bg-[#070b07] p-5 flex flex-col justify-between hover:border-[#ffb000]/60 transition-colors duration-200"
              >
                <div>
                  <div className="text-[10px] text-[#ffb000] font-bold tracking-widest mb-1">
                    [CARTRIDGE 0{idx + 1}]
                  </div>
                  <h3 className="text-sm font-bold text-[#33ff33] mb-2 uppercase tracking-wide">
                    {feature.title}
                  </h3>
                  <p className="text-xs text-[#33ff33]/85 leading-relaxed">
                    {feature.description}
                  </p>
                </div>
                <div className="text-[10px] text-[#33ff33]/40 mt-4 font-semibold text-right tracking-widest uppercase">
                  SECTOR_LOAD_OK
                </div>
              </div>
            ))}
          </div>

          {/* Bottom Scrolling Marquee Ticker */}
          <div className="w-full bg-black border-2 border-[#33ff33]/30 h-8 flex items-center overflow-hidden mt-6 relative select-none">
            <div className="animate-marquee whitespace-nowrap text-[10px] text-[#ffb000] uppercase tracking-[0.2em] py-1">
              {"◄◄ CORES RUNNING... // DATABASE CACHED: "}{stats.totalGames}{" TITLES LOADED // DIRECT IGDB MATCH OVERRIDE SYSTEM ENGAGED // ENJOY RETRO MONOSPACE VIEW ◄◄"}
            </div>
          </div>

        </div>
      </div>

    </div>
  );
}
