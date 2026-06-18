# Multi-stage build for the SwarmRoute host (.NET 10). Build context is the repo root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the whole tree (the host's project graph spans Shared/, the four contexts, Coordination, Simulation,
# src/vendor and lib/NetDevPack). third-party/ is reference-only and never restored by the host graph.
COPY . .

RUN dotnet restore host/SwarmRoute.Host/SwarmRoute.Host.csproj
RUN dotnet publish host/SwarmRoute.Host/SwarmRoute.Host.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "SwarmRoute.Host.dll"]
