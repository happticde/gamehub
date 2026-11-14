#!/usr/bin/env bash
set -euo pipefail

# Only explicitly listed proof inputs enter this disposable container. No repository mount.
proof_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
proof_container="gamehub-openiddict-proof-$$"
proof_image="mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb"
trap 'docker rm -f "$proof_container" >/dev/null 2>&1 || true' EXIT
docker run -d --name "$proof_container" "$proof_image" sleep infinity >/dev/null
docker exec "$proof_container" mkdir /proof
for proof_file in Directory.Packages.props GameHub.OpenIddictProof.csproj packages.lock.json Program.cs ProofHost.cs; do
    docker cp "$proof_dir/$proof_file" "$proof_container:/proof/$proof_file"
done
docker exec -w /proof "$proof_container" dotnet --version
docker exec -w /proof "$proof_container" dotnet restore --locked-mode
docker exec -w /proof "$proof_container" dotnet list package --include-transitive --vulnerable --format json
docker network disconnect bridge "$proof_container"
docker exec -w /proof "$proof_container" dotnet run --no-restore --configuration Release
