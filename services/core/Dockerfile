FROM golang:1.26.9-bookworm AS notice-builder
COPY services/cli/tools/license-notices/main.go /license-notices.go
RUN CGO_ENABLED=0 go build -o /license-notices /license-notices.go

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Directory.Packages.props", "./"]
COPY ["services/core/GameHub.Core.csproj", "services/core/"]
COPY ["services/shared/GameHub.Shared.csproj", "services/shared/"]
RUN dotnet restore "services/core/GameHub.Core.csproj"

COPY . .
WORKDIR "/src/services/core"
ARG VERSION=0.0.0
RUN dotnet publish "GameHub.Core.csproj" -c Release -o /app/publish /p:UseAppHost=false /p:Version=$VERSION
COPY --from=notice-builder /license-notices /usr/local/bin/license-notices
RUN license-notices -kind nuget -input obj/project.assets.json -legal /src/licensing -output /app/legal

# aspnet:alpine is ~100-115 MB (vs ~220 MB full aspnet) and has wget + sh built in.
# Avoids all chiseled-image ABI/shell compatibility issues.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
ARG VERSION=0.0.0
ARG REVISION=unknown
LABEL org.opencontainers.image.version=$VERSION \
      org.opencontainers.image.revision=$REVISION \
      org.opencontainers.image.source="https://github.com/happticde/gamehub" \
      org.opencontainers.image.licenses="AGPL-3.0-only"
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build /app/legal/LICENSE /app/legal/LICENSE
COPY --from=build /app/legal/THIRD_PARTY_NOTICES.txt /app/legal/THIRD_PARTY_NOTICES.txt

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

RUN mkdir -p /app/data && chown -R $APP_UID /app/data
USER $APP_UID

ENTRYPOINT ["dotnet", "GameHub.Core.dll"]
