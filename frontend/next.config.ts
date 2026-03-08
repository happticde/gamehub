import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  async rewrites() {
    // In a Devcontainer on Mac/Windows, 'localhost' is the devcontainer itself.
    // We use host.docker.internal to break out of the container and hit the host ports
    // mapped by the external docker-compose.yml
    const backendHost = process.env.BACKEND_HOST || "host.docker.internal";

    return [
      {
        source: "/api/core/:path*",
        destination: `http://${backendHost}:5001/api/:path*`, // Proxy to Core Service
      },
      {
        source: "/api/ingest/:path*",
        destination: `http://${backendHost}:5000/api/:path*`, // Proxy to Ingest Service
      },
    ];
  },
};

export default nextConfig;
