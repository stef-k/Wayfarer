# Wayfarer-owned packaging only: official PG17 Debian base plus signed PGDG packages.
# Refresh pins together in a reviewed revision; never apt-upgrade a running database.
FROM postgres:17.11-bookworm@sha256:91eb910c44c7ed13f7f1a4ccadaa9ca72ef14cddc04cacb6e070e48eb44731a3

LABEL org.opencontainers.image.source="https://github.com/stef-k/Wayfarer" \
      org.opencontainers.image.title="Wayfarer PostgreSQL/PostGIS" \
      org.opencontainers.image.description="PostgreSQL 17.11 and PGDG PostGIS 3.6.4 on Debian Bookworm"

# The base owns PGDG's signing key/repository and PostgreSQL entrypoint/data layout.
# Pin both extension packages; reject an unexpected server upgrade during assembly.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        postgresql-17-postgis-3=3.6.4+dfsg-2.pgdg12+1 \
        postgresql-17-postgis-3-scripts=3.6.4+dfsg-2.pgdg12+1 \
    && test "$(dpkg-query -W -f='${Version}' postgresql-17)" = '17.11-1.pgdg12+2' \
    && rm -rf /var/lib/apt/lists/*

# No credentials, init policy, app migrations or data are baked into this image.
# Compose mounts its existing fresh-cluster role/database initialization separately.
