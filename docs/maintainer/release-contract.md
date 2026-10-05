---
title: Release contract
---

# Release contract

This page defines Wayfarer's maintained immutable release, trust, compatibility,
storage and persisted lifecycle contract. Release sequencing belongs to
[Versioning](versioning.md), application publication to
[Publication](publication.md), and production database/Compose qualification to
[Production Compose](production-compose.md).

Operator commands and recovery procedures belong to the
[wayfarerctl reference](../self-hosting/wayfarerctl.md).

## Supported release boundary

The supported Compose distribution targets:

- Linux Docker Engine with Compose v2 2.24.4+;
- native `linux/amd64` or `linux/arm64`;
- one host, one Wayfarer installation and one bundled database;
- local filesystems that provide normal Unix ownership, atomic rename and durable
  file/directory writes.

The target host does not need a source clone or host .NET, Node, Python, PostgreSQL,
Nginx or Certbot for the supported Compose path.

32-bit ARM, Docker Desktop, Swarm, Kubernetes, high-availability orchestration and
shared/network PostgreSQL data volumes are outside this release contract. Native/manual
deployment remains a separate advanced path.

## Immutable identities and trust

A deployable stable release binds all executable inputs to immutable identities:

- the application release source and version;
- the multi-platform application image index and selected native manifest;
- the accepted production database image and selected native manifest;
- the pinned Caddy image;
- the exact bundle payload;
- the bundled `wayfarerctl` and recovery executables.

Mutable tags, local image IDs, archive timestamps and directory names are not release
authority. Checksums establish byte integrity; they do not authenticate an untrusted
source by themselves.

Stable publication is create-only. An existing stable image tag, bundle asset or
bootstrap asset is never repaired by overwriting it. A dependency refresh requires a
new reviewed release identity.

## Application and database compatibility

The application container runs on the maintained .NET 10 Noble/glibc family and is
published natively for AMD64 and ARM64.

The maintained project database major for development, native/manual deployment and
guarded relational testing is PostgreSQL 18 with PostGIS.

Production Compose has a narrower immutable database contract:

- PostgreSQL **18.6** on Bookworm;
- `postgresql-18=18.6-1.pgdg12+2`;
- PostGIS **3.6.4** packages at `3.6.4+dfsg-2.pgdg12+1`;
- durable parent `/var/lib/postgresql`;
- upstream `PGDATA=/var/lib/postgresql/18/docker`.

The accepted production identity is recorded in
`tools/release/database-release.json`. Stable bundles select its exact native
manifest. There is no supported PG17 Compose baseline or ordinary PG17-to-PG18
physical-cluster update path.

## Storage and recovery authority

| Container location | Authority |
| --- | --- |
| `/app`, `/opt/wayfarer-browsers` | Immutable application/browser payload |
| `/var/lib/wayfarer` | Durable `app-data`: uploads/imports and the complete Data Protection ring |
| `/var/cache/wayfarer` | Rebuildable `app-cache`: tiles, images and thumbnails |
| `/var/log/wayfarer` | Operational logs |
| `/tmp/wayfarer` | Disposable bounded temporary work |
| `/var/lib/postgresql` | Authoritative PostgreSQL 18 Compose cluster |
| Caddy `/data`, `/config` | Managed TLS/account and proxy state |
| `/run/secrets` | Selected read-only protected file mounts |

The durable application recovery boundary is the PostgreSQL database, complete active
Data Protection key ring and durable uploads/imports. Preserve those together.
Caches, logs and Caddy certificate state are not substitutes for that set.

Application data, cache, logs and database use explicit mounts. The immutable
application and browser payload is never overlaid by writable state. Backup
destinations are separate from live application volumes.

## `release.json` authority

Canonical local bundles use `release.json` as their machine-readable release
authority. The current manifest schema, bundle contract and configuration schema are
all version 1. Its contract binds:

- release/application identity and immutable image selections;
- native platform;
- exact payload inventory, hashes and modes;
- application schema/migration and Quartz compatibility facts;
- recovery protocol/source compatibility;
- stable Data Protection identity;
- uploads/key-ring layout and credential-readiness facts;
- explicit supported update-source authority.

Installation-specific UUIDs, live database observations and PostgreSQL physical
ordinals do not become release metadata.

Candidate assembly requires explicit locally qualified identities. Stable assembly
uses the accepted database authority and exact published application identity.
Stable metadata cannot be made valid by substituting a mutable registry reference.

## Configuration, secrets and project identity

The installation project identity is explicit and persistent. The same project
selection must be used for its Compose networks, volumes and lifecycle operations;
generated container names are not an interface.

Non-secret deployment configuration is distinct from secret files. Database and
application passwords are created outside the image and mounted through bounded
read-only files with the ownership required by each consumer. Secrets do not belong
in image layers, bundle metadata, ordinary environment files, command arguments or
diagnostic output.

The configured storage roots, trusted ingress identity, database image and application
image must agree with the validated release and installation authority. File or
container existence alone does not prove a completed installation.

## Retained releases and persisted lifecycle authority

Supported lifecycle operations retain the evidence needed to continue or recover
safely: current/previous bundles, operators, images, receipts, recovery holds and
operation-specific generation state. There is no automatic garbage collection of
that authority.

Setup, backup configuration, restore and update publish protected state transitions
durably before dependent mutation. Installation/generation pointers select active
storage; unselected generations remain inert. Interrupted operations resume or abort
only when the persisted receipt and current protected state authorize that action.

After a migration or candidate writer may have crossed its irreversible boundary,
an old application image is not database rollback. Recovery follows the retained
operation authority and managed restore contract instead of deleting receipts or
selecting old bytes manually.

Executable lifecycle details live in `tools/WayfarerCtl`,
`tools/WayfarerRecovery` and the shipped bundle. Follow
[wayfarerctl](../self-hosting/wayfarerctl.md) for administrator-facing actions.
