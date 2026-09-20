# Stage 1: Build Vue 3 SPA
FROM node:22-alpine AS frontend-build
WORKDIR /app/client
COPY src/AudioYotoShelf.ClientApp/package*.json ./
RUN npm clean-install

COPY src/AudioYotoShelf.ClientApp/ ./
RUN npm run build

# Stage 2: Build .NET 10
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src

# Restore dependencies (layer caching)
COPY Directory.Build.props Directory.Packages.props ./
COPY src/AudioYotoShelf.Core/AudioYotoShelf.Core.csproj src/AudioYotoShelf.Core/
COPY src/AudioYotoShelf.Infrastructure/AudioYotoShelf.Infrastructure.csproj src/AudioYotoShelf.Infrastructure/
COPY src/AudioYotoShelf.Api/AudioYotoShelf.Api.csproj src/AudioYotoShelf.Api/
RUN dotnet restore src/AudioYotoShelf.Api/AudioYotoShelf.Api.csproj

# Copy source and build
COPY src/ src/
COPY --from=frontend-build /app/client/../AudioYotoShelf.Api/wwwroot/ src/AudioYotoShelf.Api/wwwroot/
RUN dotnet publish src/AudioYotoShelf.Api/AudioYotoShelf.Api.csproj \
    -c Release \
    -o /app \
    --no-restore

# Stage 3: Runtime with FFmpeg
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl is for the HEALTHCHECK below; the aspnet base image does not include it.
RUN apt-get update && \
    apt-get install -y --no-install-recommends ffmpeg curl && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=backend-build /app ./

# Create temp directory for audio processing
RUN mkdir -p /app/temp && chown $APP_UID:$APP_UID /app/temp

USER $APP_UID

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV TMPDIR=/app/temp

# Liveness only: /api/health also probes Postgres, Redis and ffmpeg, so a slow first boot (EF
# migrations against a cold database) would report unhealthy — and proxies skip unhealthy containers.
HEALTHCHECK --interval=30s --timeout=5s --retries=3 --start-period=60s \
    CMD curl -f http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "AudioYotoShelf.Api.dll"]
