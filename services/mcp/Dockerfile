FROM golang:1.27.3-bookworm AS notice-builder
COPY services/cli/tools/license-notices/main.go /license-notices.go
RUN CGO_ENABLED=0 go build -o /license-notices /license-notices.go

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY ["global.json", "Directory.Packages.props", "./"]
COPY ["services/mcp/GameHub.Mcp.csproj", "services/mcp/"]
RUN dotnet restore "services/mcp/GameHub.Mcp.csproj"

COPY services/mcp/ services/mcp/
COPY licensing/ licensing/
WORKDIR /src/services/mcp
ARG VERSION=0.0.0
RUN dotnet publish "GameHub.Mcp.csproj" -c Release -o /app/publish /p:UseAppHost=false /p:Version=$VERSION --no-restore
COPY --from=notice-builder /license-notices /usr/local/bin/license-notices
RUN license-notices -kind nuget -input obj/project.assets.json -legal /src/licensing -output /app/legal

# The stdio server needs the .NET runtime, with no HTTP listener or SDK.
FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine
ARG VERSION=0.0.0
ARG REVISION=unknown
ARG SOURCE_REPOSITORY=happticde/gamehub
LABEL org.opencontainers.image.version=$VERSION \
      org.opencontainers.image.revision=$REVISION \
      org.opencontainers.image.source="https://github.com/${SOURCE_REPOSITORY}" \
      org.opencontainers.image.licenses="AGPL-3.0-only"
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build /app/legal/ /app/legal/
USER $APP_UID
ENTRYPOINT ["dotnet", "GameHub.Mcp.dll"]
