# Patched bundled Node roots, independently inspected at this immutable index (#915).
FROM node:24.22.1-bookworm-slim@sha256:55fb5f5a527ea2c5c4a95f963e48ef90aa38c7b346054fdc8ee9957a2b55d205 AS node
# Build the locked helper natively. Rust and codec build tools stay in this stage.
FROM rust:1.90.0-slim-bookworm@sha256:64232e656c058f4468e8d024e990acff04f0fd5a5c0a88a574dc37773d7325c9 AS image-helper
# Signed Debian metadata/package verification precedes Cargo's first HTTPS download.
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates=20250419~deb12u2 build-essential cmake nasm pkg-config \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /helper
COPY tools/image-helper/Cargo.toml tools/image-helper/Cargo.lock ./
COPY tools/image-helper/src/ ./src/
RUN cargo build --release --locked

FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
ARG TARGETARCH
COPY --from=node /usr/local/ /usr/local/
# Signed Noble CA correction must precede NuGet/npm acquisition, even on a cached base.
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates=20260816ubuntu0.24.04.1 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY Wayfarer.csproj Version.props ./
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac \
    && dotnet restore Wayfarer.csproj -r "$rid"
COPY . .
COPY --from=image-helper /helper/target/release/wayfarer-image /image-helper/wayfarer-image
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac \
    && npm ci && npm run build \
    && dotnet build Wayfarer.csproj -c Release -r "$rid" --no-restore -p:ImageHelperPrebuilt=/image-helper/wayfarer-image \
    && dotnet publish Wayfarer.csproj -c Release -r "$rid" --self-contained false --no-build --no-restore -p:ImageHelperPrebuilt=/image-helper/wayfarer-image -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee AS runtime
ARG TARGETARCH
WORKDIR /app
COPY --from=build /publish/.playwright/ ./.playwright/
ENV PLAYWRIGHT_BROWSERS_PATH=/opt/wayfarer-browsers
# Correct the actual final OS store before browser acquisition. Mount patched Node
# transiently; keep the published Playwright CLI/browser revision and runtime driver.
RUN --mount=from=node,source=/usr/local/bin/node,target=/tmp/wayfarer-installer-node \
    apt-get update && apt-get install -y --no-install-recommends ca-certificates=20260816ubuntu0.24.04.1 \
    && /tmp/wayfarer-installer-node .playwright/package/cli.js install-deps chromium \
    && /tmp/wayfarer-installer-node .playwright/package/cli.js install chromium \
    && apt-get update && apt-get install -y --no-install-recommends util-linux \
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
