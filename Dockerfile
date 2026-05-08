# syntax=docker/dockerfile:1.7

# ---------- Stage 1: build the React client (architecture-agnostic) ----------
FROM --platform=$BUILDPLATFORM node:20-bookworm-slim AS client
WORKDIR /src/client
COPY client/package.json client/package-lock.json* ./
RUN --mount=type=cache,target=/root/.npm \
    if [ -f package-lock.json ]; then npm ci; else npm install; fi
COPY client/ ./
RUN npm run build

# ---------- Stage 2: build the .NET server ----------
# Run the SDK on the native host (no QEMU). With /p:UseAppHost=false the
# output is portable IL, so the same /out/ works for every target arch — the
# runtime image picks the right .NET host.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS server-build
WORKDIR /src
COPY server/*.csproj server/
RUN dotnet restore server/Tergeo.Server.csproj
COPY server/ server/
COPY --from=client /src/client/dist/ server/wwwroot/
RUN dotnet publish server/Tergeo.Server.csproj \
    -c Release -o /out --no-restore /p:UseAppHost=false

# ---------- Stage 3: runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=server-build /out/ ./

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=1 \
    Storage__DataDirectory=/data \
    Storage__BooksDirectory=/books \
    ConnectionStrings__Default="Data Source=/data/tergeo.db"

RUN useradd -m -u 1001 -s /bin/bash tergeo && \
    mkdir -p /data /books && \
    chown -R tergeo:tergeo /app /data /books
USER tergeo

VOLUME ["/data", "/books"]
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s \
    CMD curl -fsS http://localhost:8080/api/auth/config || exit 1

ENTRYPOINT ["dotnet", "Tergeo.Server.dll"]
