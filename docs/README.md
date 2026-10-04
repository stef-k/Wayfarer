---
title: Wayfarer documentation
permalink: /
---

# Wayfarer Documentation

Wayfarer is a self-hosted travel companion for private location history, trip planning
and optional live sharing. Choose the path that fits what you want to do.

## Users

For using an existing Wayfarer instance, including one run by someone else. Start
with the [User Guide](user/index.md) for accounts, privacy and your first actions.

- [Timeline and locations](user/timeline.md): browse, correct and share your history.
- [Trips](user/trips.md): plan journeys, routes and visits.
- [Groups](user/groups.md): share locations with trusted people.
- [Import and export](user/import-export.md): move history and Trip plans, including safe Mobile recovery.
- [WayfarerMobile](user/mobile.md): connect your phone, record locations and use downloaded Trips.
- [Personal location providers](user/location-providers.md): optional addresses, search and routing with privacy and cost controls.

## Self-hosters

For installing and operating your own instance:

- [Install & Self-Hosting](02-Install-and-Dependencies.md): supported platforms, prerequisites and guided Compose setup.
- [Operate Wayfarer with wayfarerctl](29-Wayfarerctl.md): status, diagnosis, backup, update and restore.
- [Security](21-Security.md): account protections, HTTPS/proxy trust and privacy expectations.

## Developers & Contributors

For local development, architecture, configuration, services, APIs, database work,
testing and contributing. The [Developer Guide](13-Developer-Guide.md) explains where
to start and how the frontend and backend fit together.

## Project Maintainers

For people maintaining Wayfarer itself: release/version/publication ownership,
lifecycle qualification, container/Compose internals, exceptional recovery authority
and platform qualification. Ordinary self-hosting and operation follow the
Self-hosters path above.

- [Versioning & Release Operations](23-Versioning.md) and [Application Image Publication](27-Application-Image-Publication.md).
- [Container & Release Contract](25-Container-Release-Contract.md), [Application Container](26-Application-Container.md) and [Production Compose](28-Production-Compose.md).
- [Advanced Native / Manual Deployment](20-Deployment.md#advanced-nativemanual-deployment).

---

Wayfarer is [MIT-licensed](https://github.com/stef-k/Wayfarer/blob/main/LICENSE.txt)
and provided as is, without warranty.
