FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["services/ingest/GameHub.Ingest.csproj", "services/ingest/"]
COPY ["services/shared/GameHub.Shared.csproj", "services/shared/"]
RUN dotnet restore "services/ingest/GameHub.Ingest.csproj"

COPY . .
WORKDIR "/src/services/ingest"
RUN dotnet publish "GameHub.Ingest.csproj" -c Release -o /app/publish /p:UseAppHost=false

# aspnet:alpine is ~100-115 MB (vs ~220 MB full aspnet) and has wget + sh built in.
# Avoids all chiseled-image ABI/shell compatibility issues.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

RUN mkdir -p /app/data && chown -R $APP_UID /app/data
USER $APP_UID

ENTRYPOINT ["dotnet", "GameHub.Ingest.dll"]
