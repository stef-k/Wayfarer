# Wayfarer-owned packaging only: official PG18 Debian base plus signed PGDG packages.
# Refresh pins together in a reviewed revision; never apt-upgrade a running database.
FROM postgres:18.6-bookworm@sha256:3725f4e2499eef5134592b3b4ab79a543ed7f8e533b05b5b637af926630f6650

LABEL org.opencontainers.image.source="https://github.com/stef-k/Wayfarer" \
      org.opencontainers.image.title="Wayfarer PostgreSQL/PostGIS" \
      org.opencontainers.image.description="PostgreSQL 18.6 and PGDG PostGIS 3.6.4 on Debian Bookworm"

# Bootstrap TLS roots from signed Debian packages before contacting the Bookworm
# archive. Use the base's PGDG key to authenticate both historical PostGIS packages.
# Restore the base's source after installation and reject an unexpected server upgrade.
RUN mv /etc/apt/sources.list.d/pgdg.list /tmp/pgdg.list \
    && apt-get -o APT::Update::Error-Mode=any update \
    && apt-get install -y --no-install-recommends ca-certificates=20250419~deb12u2 \
    && printf '%s\n' 'deb [signed-by=/usr/local/share/keyrings/postgres.gpg.asc] https://apt-archive.postgresql.org/pub/repos/apt bookworm-pgdg-archive main' \
        > /etc/apt/sources.list.d/pgdg.list \
    && apt-get -o APT::Update::Error-Mode=any update \
    && apt-get install -y --no-install-recommends \
        postgresql-18-postgis-3=3.6.4+dfsg-2.pgdg12+1 \
        postgresql-18-postgis-3-scripts=3.6.4+dfsg-2.pgdg12+1 \
    && test "$(dpkg-query -W -f='${Version}' postgresql-18)" = '18.6-1.pgdg12+2' \
    && mv /tmp/pgdg.list /etc/apt/sources.list.d/pgdg.list \
    && rm -rf /var/lib/apt/lists/*

# No credentials, init policy, app migrations or data are baked into this image.
# Compose mounts its existing fresh-cluster role/database initialization separately.
