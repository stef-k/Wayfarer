---
title: Production Compose
---

# Production Compose

This page owns Wayfarer's production Compose topology, substrate boundaries, exact
database image contract, protected configuration, disposable qualification, database
publication/promotion and native platform selection.

Ordinary setup, update, backup and restore belong to
[Self-hosting](../self-hosting/index.md) and
[wayfarerctl](../self-hosting/wayfarerctl.md).

## Topology and state

`deploy/compose/` is the production substrate source. A stable bundle supplies
immutable application, database and Caddy identities and does not require a source
or application build toolchain on the target host.

The topology is:

| Authority | Services | State/exposure |
| --- | --- | --- |
| `backend` | Wayfarer, database | Internal network; PostgreSQL has no host port |
| `edge` | Wayfarer, managed Caddy | Application egress plus managed ingress |
| `app-data` | Wayfarer | Durable uploads/imports and complete Data Protection ring |
| `app-cache` | Wayfarer | Rebuildable tile/image/thumbnail state |
| `app-logs` | Wayfarer | Operational logs |
| `db-data` | database | Authoritative PostgreSQL 18 cluster |
| `caddy-data`, `caddy-config` | Caddy | Managed TLS/account/proxy state |

The application container is non-root with a read-only root and explicit state
mounts. Managed Caddy alone publishes the public HTTP/HTTPS ports. The database is
never published to the host.

The Compose project name is part of installation identity. Persist a non-default
project choice and use it consistently; generated container names are not an
interface.

## Request and response-header boundaries

Managed Caddy does not own Wayfarer's application upload policy. The application
retains its fixed global request ceiling and its dynamic authenticated upload limits;
an external proxy may impose an equal or stricter operator-owned ceiling.

Wayfarer owns its route-aware browser security headers. Managed Caddy passes those
headers through unchanged; do not add generic proxy header overrides that break
intentional public embed routes.

Server-Sent Events rely on Caddy's normal event-stream handling. The production proxy
does not use a blanket response timeout or a forced flush setting that changes
upstream cancellation semantics.

## Exact database image contract

Production Compose uses a Wayfarer-owned packaging layer built from the official
PostgreSQL Bookworm image and signed PGDG PostGIS packages.

The maintained Compose database contract is exactly:

- PostgreSQL **18.6**;
- package `postgresql-18=18.6-1.pgdg12+2`;
- PostGIS **3.6.4** packages at `3.6.4+dfsg-2.pgdg12+1`;
- Debian Bookworm/glibc;
- UTF8 with `C.UTF-8` for fresh clusters;
- required `postgis` and `citext` extensions;
- durable mount `/var/lib/postgresql`;
- upstream `PGDATA=/var/lib/postgresql/18/docker`.

Exact upstream image/package pins live in `deploy/compose/db/Dockerfile`. Accepted
published DB identity lives in `tools/release/database-release.json`.

The runtime bundle selects the accepted **derived native manifest digest**, not the
upstream base digest, a mutable tag or a local image ID. There is no supported PG17
Compose baseline or direct reuse of a PG17 physical data directory as an ordinary
update.

A PostgreSQL major or PostGIS extension upgrade requires an explicit migration and
qualification contract; refreshing an image does not authorize it.

## Protected configuration and secrets

Guided setup generates the protected installation authority. Raw Compose files are
substrate, not a substitute for `installation.json`, receipts and lifecycle state.

Non-secret deployment configuration carries the public host, proxy mode, edge network
and immutable image selections. Passwords are separate read-only files with bounded
ownership:

- database bootstrap administrator password: upstream database bootstrap;
- application-role database password: database initialization;
- application password-file copy: Wayfarer connection seam.

The application-role copies represent the same database credential with ownership
appropriate to each consumer. Never make secret files world-readable, bake them into
images, place them in ordinary environment files or print them during verification.

Database initialization creates required extensions only for an empty cluster. It
does not run EF/Quartz migrations, seed application data or reset credentials.
Application maintenance remains explicit and serialized.

## Managed and external ingress

Managed mode uses the pinned Caddy image and automatic public HTTPS. Caddy is the
single public ingress and forwards to Wayfarer on the edge network. Wayfarer trusts
the configured managed peer, not the entire network.

External mode exposes Wayfarer only on the configured loopback port. The external
proxy owns TLS and must replace client-supplied forwarding headers with authoritative
Host, scheme and client-IP values. A different container/private-network proxy
topology is not implied by the host-native external-proxy qualification.

Switching ingress mode is an operator lifecycle concern; profile omission alone must
not be treated as proof that a previously running proxy has stopped.

## Disposable Compose qualification

`tools/compose/qualify.py` owns disposable production-substrate qualification. It
uses isolated project-labelled state and exact test image overrides without changing
production pins.

The full qualification proves, at the appropriate native seam:

- configuration validation and both ingress modes;
- PostgreSQL 18.6/PostGIS 3.6.4 package and executable identity;
- non-superuser application migration/seed/bootstrap;
- application readiness and health;
- storage/network/mount boundaries and recreation persistence;
- database/key-ring/upload recovery authority and logical dump/restore;
- authenticated/public API, upload, embed, SSE and browser/PDF/thumbnail paths;
- forwarded-client spoof resistance;
- managed TLS behavior against the fixture certificate authority;
- bounded cleanup of only the owned disposable resources.

This evidence is not public-CA issuance, arbitrary proxy support, native-to-Compose
migration or production-host acceptance.

## Database publication and evidence promotion

`.github/workflows/database-image.yml` is the explicit database-publication
workflow. It is manually dispatched from an exact reviewed source and does not create
an application tag, GitHub Release or application bundle.

Publication is create-only:

1. native AMD64 and ARM64 jobs build the pinned recipe, verify actual package/server
   versions and run the required Compose qualification;
2. each native image is published under source-bound immutable identity;
3. fresh read-only jobs anonymously pull the recorded native digest and re-run the
   native qualification;
4. the index job binds the two qualified native manifests.

A successful publication does not automatically become stable bundle authority.
Preserve and independently review the publication, anonymous-qualification and index
evidence. Promotion is a separate reviewed source change that copies the actual
`db-index.json` artifact unchanged to `tools/release/database-release.json`.

Stable application publication validates that accepted file before publishing any
application manifest.

## SSE proxy qualification

`tools/compose/qualify_sse.py` exercises the production Caddy transport with the
actual server-side SSE implementation. It verifies event delivery, heartbeat,
downstream cancellation, cleanup and reconnect through the same running proxy.

The probe is a transport qualification. It does not replace application/mobile
authorization tests or establish production-network performance characteristics.

## Retained bundles and native platform selection

Validated releases are retained immutably beneath the installation release root.
Import/adoption never replaces an occupied release identity. Current/previous bundles,
images, operators and lifecycle evidence remain available for recovery; there is no
automatic release/image garbage collection.

`wayfarerctl` derives the supported native platform from the host and release.
The same validated platform selects application, database and Caddy artifacts and is
used by recovery/update helpers. It is not a free-form deployment option.

Candidate/stable image and bundle identity semantics are defined by the
[Release contract](release-contract.md); release sequencing is defined by
[Versioning](versioning.md).
