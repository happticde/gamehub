import Link from "next/link";
import { Gamepad2, Library, Globe, Sparkles } from "lucide-react";

/**
 * Landing page — the first thing users see when visiting GameHub.
 *
 * Features:
 * - Animated gradient mesh background for visual depth
 * - Headline with gradient accent text
 * - Quick stats counters (fetched SSR from the backend)
 * - Feature highlight cards with hover effects
 */

/** Fetches basic library stats for the landing page counters */
async function getLibraryStats(): Promise<{ totalGames: number; totalPlatforms: number }> {
  try {
    // Fetch a minimal page just to get totalCount
    const backendHost = process.env.BACKEND_HOST || "host.docker.internal";
    const res = await fetch(`http://${backendHost}:5001/api/library?pageSize=1`, {
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
    <div className="relative flex flex-col items-center justify-center min-h-[calc(100vh-4rem)] overflow-hidden">
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
  );
}
