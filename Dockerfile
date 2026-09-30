# Native Linux AMD64/ARM64 payload; indexes retain the previously qualified AMD64 base manifests.
FROM node:24-bookworm-slim@sha256:0e0ff40c39bc087845bfb27465a0df4ea419520094bc35842ff83dd8cbe6f9b6 AS node
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS build
ARG TARGETARCH
COPY --from=node /usr/local/ /usr/local/
WORKDIR /src
COPY Wayfarer.csproj Version.props ./
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac \
    && dotnet restore Wayfarer.csproj -r "$rid"
COPY . .
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac \
    && npm ci && npm run build \
    && dotnet build Wayfarer.csproj -c Release -r "$rid" --no-restore \
    && dotnet publish Wayfarer.csproj -c Release -r "$rid" --self-contained false --no-build --no-restore -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:2d584d8147faddb0d678c5748d47953e5b8e18621ed4fb7049a91381d9d7746f AS runtime
ARG TARGETARCH
WORKDIR /app
COPY --from=build /publish/.playwright/ ./.playwright/
ENV PLAYWRIGHT_BROWSERS_PATH=/opt/wayfarer-browsers
# Use the published version's bundled installer/Node driver, never a separately versioned CLI.
RUN case "$TARGETARCH" in amd64) driver=linux-x64 ;; arm64) driver=linux-arm64 ;; *) exit 1 ;; esac \
    && .playwright/node/$driver/node .playwright/package/cli.js install-deps chromium \
    && .playwright/node/$driver/node .playwright/package/cli.js install chromium \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /publish/ ./
# Unmounted state roots remain root-owned: omitted mounts must fail, not store durable state in a layer.
RUN chmod -R a-w /app /opt/wayfarer-browsers \
    && mkdir -p /var/lib/wayfarer /var/cache/wayfarer /var/log/wayfarer /tmp/wayfarer \
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
