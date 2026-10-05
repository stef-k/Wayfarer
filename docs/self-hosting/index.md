---
title: Self-host Wayfarer
---

# Self-host Wayfarer

This guide is for the person who installs and operates a Wayfarer instance. The
supported production path is Linux with Docker Engine and Compose, managed by the
self-contained `wayfarerctl` operator.

If you only use an existing instance, start with the [user guide](../user/).
If you intentionally run Wayfarer directly under systemd/Nginx instead of Compose,
use the [advanced native/manual guide](native-manual.md).

## Supported production path

Wayfarer supports **Linux AMD64 and ARM64** through the same Compose lifecycle. The
normal installation consists of:

- the Wayfarer application;
- PostgreSQL with PostGIS;
- either managed Caddy HTTPS or an administrator-owned external reverse proxy;
- durable database and application-data volumes;
- retained release/operator state under the deployment root.

The supported Compose lifecycle starts at **v1.9.21**. Current operators reject
public setup/update selectors below that floor. Existing native/systemd
installations are a separate deployment family: automated native-to-Compose
migration is not currently supported.

You do **not** need a source checkout or host installations of .NET, Node,
PostgreSQL, Nginx or Certbot for the supported Compose path.

## Before you begin

Prepare a host with:

- 64-bit Linux on AMD64 or ARM64;
- Docker Engine using the local `/var/run/docker.sock`;
- Docker Compose v2 **2.24.4 or newer**;
- root or sudo access;
- a filesystem that preserves normal Unix ownership and modes;
- enough disk space for container images, retained releases, backups and your data;
- outbound HTTPS access to GitHub/GHCR and the pinned Caddy image registry.

Remote Docker contexts, Docker Desktop and rootless Docker are outside the accepted
production topology.

For **managed HTTPS**, also prepare a public DNS hostname that resolves to the host
and make 80/TCP, 443/TCP and 443/UDP available. If you already operate a reverse
proxy, use external mode instead.

## Download and verify the bootstrap

From the official [Wayfarer Releases](https://github.com/stef-k/Wayfarer/releases),
download the platform archive:

- `wayfarerctl-linux-amd64.tar.gz`, or
- `wayfarerctl-linux-arm64.tar.gz`.

The archive contains the standalone `wayfarerctl` executable, not the full
deployment bundle.

Verify the archive SHA-256 against the **REST asset digest for that exact GitHub
Release** before running it. The adjacent `.sha256` file is useful as an integrity
check, but it is not a substitute for authenticating the Release asset itself.

AMD64 example:

```sh
sha256sum wayfarerctl-linux-amd64.tar.gz
sha256sum --check wayfarerctl-linux-amd64.tar.gz.sha256
tar -xzf wayfarerctl-linux-amd64.tar.gz
chmod +x wayfarerctl
```

Keep the verified bootstrap at a stable root-owned location for later management,
for example `/usr/local/lib/wayfarer-bootstrap/`.

## Run guided setup

From an interactive root terminal:

```sh
sudo ./wayfarerctl setup
```

Bare setup resolves the latest supported stable release for your platform,
downloads and verifies its deployment bundle, pulls the exact immutable container
images named by that bundle, then guides you through:

- public hostname;
- managed or external proxy mode;
- network/loopback choices when needed;
- the initial administrator password.

The administrator password is entered hidden and confirmed. Production requires at
least 15 characters with uppercase, lowercase, a digit and a non-alphanumeric
character.

Setup then performs the protected database/application initialization and does not
report success until the selected ingress path is healthy.

### Install one exact stable version

```sh
sudo ./wayfarerctl setup --version X.Y.Z
```

Use this when you intentionally need an exact supported release. It follows the
same acquisition and verification path as bare setup.

### Use a trusted local bundle

For controlled offline/staging work:

```sh
sudo ./wayfarerctl setup --bundle /absolute/trusted/bundle
```

The bundle must be complete, canonical and trusted, and its exact images must
already be present locally. `--version` and `--bundle` are mutually exclusive.

## Choose an ingress mode

### Managed Caddy

Managed mode is the simplest public deployment. Caddy owns the public listener and
automatic HTTPS. PostgreSQL remains private.

Setup verifies public HTTPS health before completion. DNS, firewall, port conflicts
and public CA reachability are administrator responsibilities; the operator does
not modify them for you.

### External reverse proxy

External mode runs no Caddy service and exposes Wayfarer only on
`127.0.0.1:PORT` (default 8080). Your proxy must:

- forward to that loopback endpoint;
- preserve the public Host;
- replace untrusted forwarded headers with authoritative scheme, host and client
  identity;
- support long-lived SSE responses.

Wayfarer verifies loopback readiness, while public TLS/proxy correctness remains
your responsibility.

## Understand installation state

The default deployment root is:

```text
/etc/wayfarer
```

It contains retained releases, generated deployment inputs, secrets, lifecycle
receipts and recovery state. Treat the **whole deployment root** as protected
operator state. Do not edit generated files independently or move retained release
directories to make a command succeed.

After setup, run normal commands through the stable bootstrap so it can select the
correct retained operator:

```sh
/usr/local/lib/wayfarer-bootstrap/wayfarerctl \
  --deployment-root /etc/wayfarer dispatch status
```

See [wayfarerctl reference](wayfarerctl.md#dispatch-and-retained-operators).

## First checks after installation

Run:

```sh
wayfarerctl status
wayfarerctl doctor
```

Then:

1. open the public HTTPS address;
2. sign in as `admin`;
3. enable two-factor authentication;
4. review **Admin > Settings**;
5. leave registration closed unless you deliberately want public account creation;
6. configure and verify backups before treating the instance as durable.

For daily work, continue with [Operations](operations.md).

## What must be backed up

A recoverable Wayfarer instance is more than its PostgreSQL database.

Keep together:

- PostgreSQL data;
- the complete active application-data volume, including Uploads;
- the complete active ASP.NET Core Data Protection key ring.

The key ring is required to decrypt durable protected data such as personal
provider credentials. Database-only backup is therefore incomplete once protected
credentials exist.

Caches, thumbnails and logs are rebuildable and are not recovery substitutes.

Use the managed backup/restore path in [Operations](operations.md#protect-the-instance-with-backups)
rather than assembling ad-hoc copies while writers are active.

## Supported boundaries

The Compose operator intentionally fails closed instead of guessing around
contradictory state.

Important limits:

- uninstall is not implemented;
- automated native/systemd-to-Compose migration is not implemented;
- `start` and `restart` never pull images or run migrations;
- update and restore require explicit plans/authorization;
- restore never treats an old image as database rollback;
- changing secret files manually is not password rotation;
- setup does not manage your public DNS or firewall;
- external-proxy correctness remains administrator-owned.

If a command reports unresolved protected state, preserve that state and follow the
reported resume/recovery action. Do not delete receipts, locks, volumes or generated
files merely to get past a refusal.

## Next steps

- [Operations](operations.md) — routine status, logs, lifecycle, account recovery,
  backup, update and restore.
- [Troubleshooting](troubleshooting.md) — start from a symptom and the safest first
  check.
- [wayfarerctl reference](wayfarerctl.md) — complete command surface and exact
  safety/refusal semantics.
- [Advanced native/manual deployment](native-manual.md) — source/systemd/Nginx
  installations.

[Documentation home](../README.md)
