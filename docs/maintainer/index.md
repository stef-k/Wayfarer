---
title: Project maintainers
---

# Project maintainers

This section is for people maintaining Wayfarer itself: preparing releases, publishing
immutable artifacts, qualifying supported platforms and preserving the release,
database and lifecycle contracts that operators depend on.

If you are installing or operating an instance, use the
[Self-hosting guide](../self-hosting/index.md). If you are changing application
source, use the [Developer Guide](../development/index.md).

## Maintainer responsibilities

Project maintainers are responsible for:

- choosing the correct application-only, database-only or combined release path;
- preserving immutable release, image, bundle and bootstrap identities;
- reviewing and promoting accepted database publication evidence;
- keeping release tooling, workflows and documentation aligned;
- qualifying Linux AMD64 and ARM64 release artifacts at the boundaries owned by
  each workflow;
- preserving the storage, recovery and persisted lifecycle authorities used by
  setup, backup, restore and update;
- retaining enough evidence to support release claims without treating CI fixtures
  as production-host evidence.

The maintained PostgreSQL major for development, native/manual operation and guarded
relational testing is PostgreSQL 18 with PostGIS. Production Compose uses the
separately pinned PostgreSQL 18.6 + PostGIS 3.6.4 database image contract described
in [Production Compose](production-compose.md).

## Authority map

| Owner | Responsibility |
| --- | --- |
| [Versioning](versioning.md) | Application version source, release-path choice, sequencing, support floor and public acceptance policy |
| [Release contract](release-contract.md) | Immutable identities, trust, compatibility, storage/recovery authority, `release.json` and persisted lifecycle authority |
| [Application container](application-container.md) | Application image build/runtime, explicit maintenance, health, Chromium and disposable/offline qualification |
| [Publication](publication.md) | Application image/index, bundle/bootstrap publication, anonymous qualification and public acceptance |
| [Production Compose](production-compose.md) | Production topology, exact database image, ingress boundaries, Compose qualification and database publication/promotion |
| [wayfarerctl reference](../self-hosting/wayfarerctl.md) | Operator commands, refusals, recovery actions and ordinary lifecycle behavior |
| [Release INSTALL](../../tools/release/INSTALL.md) | Instructions shipped with the bundle in hand |
| `Version.props`, `tools/release/`, `deploy/compose/`, workflows | Executable and machine-readable authority for the behavior they implement |

Documentation explains the maintained contract. Executable workflows, release tools,
manifests and source remain authoritative for their machine-enforced details. When a
maintainer-facing document and executable behavior disagree, investigate the mismatch
instead of inventing a second contract in prose.

## Release orientation

Start with [Versioning](versioning.md) before changing application versions, release
metadata, container publication, database release authority, bundles, bootstrap
assets or release workflows.

From there:

1. choose the application-only, database-only or combined release path;
2. follow the specialized application or database publication owner;
3. preserve immutable publication and qualification evidence;
4. require the repository's exact-head CI, independent review and maintainer
   acceptance before merging source changes;
5. require the public acceptance owned by the publication workflow before claiming
   released installation support.

Operator setup, update, backup and restore procedures remain in the
[Self-hosting documentation](../self-hosting/index.md); this section does not duplicate
them.
