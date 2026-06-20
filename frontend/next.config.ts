import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  output: "standalone",
  async rewrites() {
    // In a Devcontainer on Mac/Windows, 'localhost' is the devcontainer itself.
    // We use host.docker.internal to break out of the container and hit the host ports
    // mapped by the external docker-compose.yml
    const ingestUrl = process.env.INGEST_URL || "http://host.docker.internal:5000";

    return [
      {
        source: "/api/ingest/:path*",
        destination: `${ingestUrl}/api/:path*`, // Proxy to Ingest Service
      },
    ];
  },
};

export default nextConfig;
