# Linux/amd64 production payload. Update pinned bases only through a qualified source change.
FROM node:24-bookworm-slim@sha256:5cbc7caba8c2c0f0bca675d1b61b9f2857e1cf1853c6164ee9dd409501a936e7 AS node
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:28e7a5db4f5d40cc805acd939a065668ba2e17d697a09153054dce98db240d0e AS build
COPY --from=node /usr/local/ /usr/local/
WORKDIR /src
COPY Wayfarer.csproj Version.props ./
RUN dotnet restore Wayfarer.csproj -r linux-x64
COPY . .
RUN npm ci && npm run build \
    && dotnet build Wayfarer.csproj -c Release -r linux-x64 --no-restore \
    && dotnet publish Wayfarer.csproj -c Release -r linux-x64 --self-contained false --no-build --no-restore -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:ed6a2d26633ddcd3d42a1d9f9866214ecbbc11ba6ac5e0e843da02c13da24072 AS runtime
WORKDIR /app
COPY --from=build /publish/.playwright/ ./.playwright/
ENV PLAYWRIGHT_BROWSERS_PATH=/opt/wayfarer-browsers
# Use the published version's bundled installer/Node driver, never a separately versioned CLI.
RUN .playwright/node/linux-x64/node .playwright/package/cli.js install-deps chromium \
    && .playwright/node/linux-x64/node .playwright/package/cli.js install chromium \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /publish/ ./
RUN chmod -R a-w /app /opt/wayfarer-browsers \
    && mkdir -p /var/lib/wayfarer /var/cache/wayfarer /var/log/wayfarer /tmp/wayfarer \
    && chown app:app /var/lib/wayfarer /var/cache/wayfarer /var/log/wayfarer /tmp/wayfarer \
    && chmod 700 /var/lib/wayfarer /tmp/wayfarer \
    && chmod 750 /var/cache/wayfarer /var/log/wayfarer
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    Kestrel__Endpoints__Http__Url=http://0.0.0.0:8080 \
    Storage__DataRoot=/var/lib/wayfarer \
    Storage__CacheRoot=/var/cache/wayfarer \
    Storage__LogRoot=/var/log/wayfarer \
    Storage__TempRoot=/tmp/wayfarer \
    DataProtection__KeyRingPath=/var/lib/wayfarer/data-protection \
    TMPDIR=/tmp/wayfarer \
    DOTNET_EnableDiagnostics=0
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 CMD ["dotnet", "Wayfarer.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "Wayfarer.dll"]
