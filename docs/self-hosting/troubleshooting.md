---
title: Troubleshoot a Wayfarer instance
---

# Troubleshoot a Wayfarer instance

Start with the symptom, make the smallest safe observation, then follow the owning
recovery command. This page intentionally does not duplicate the full
[wayfarerctl reference](wayfarerctl.md).

## First safe checks

For an existing installation:

```sh
wayfarerctl status
wayfarerctl doctor
```

Then inspect only the relevant logs, for example:

```sh
wayfarerctl logs wayfarer --tail 100
wayfarerctl logs db --tail 100
```

Do not begin by deleting containers, volumes, lock files, lifecycle receipts or
generated deployment files.

## Setup failed

### “Setup has not started”

The attempt failed before canonical installation state was committed.

Correct the reported cause and rerun the **same plain setup command**, including
the same options:

```sh
sudo ./wayfarerctl setup
```

Typical causes are:

- GitHub/GHCR/Caddy registry connectivity;
- missing release asset;
- image pull failure;
- host/Compose prerequisite failure;
- port/network conflict;
- integrity/ownership refusal.

Verified downloads or private preparation may remain and can be reused. Do not run
`setup --resume` merely because a private staging directory exists.

### “Setup has started”

Preserve the installation root, secrets and volumes. Correct the reported cause and
resume:

```sh
sudo wayfarerctl --deployment-root /etc/wayfarer setup --resume
```

If admin bootstrap still needs protected input, enter it at the prompt or use a
protected `--password-stdin` file.

If setup reports that protected state **cannot safely resume**, stop. Do not delete
the state and start over on top of the same volumes. Preserve it for investigation.

## Managed HTTPS does not become healthy

Check:

1. the hostname resolves publicly to this host;
2. ports 80/TCP, 443/TCP and 443/UDP reach Caddy;
3. no other listener owns those ports;
4. outbound HTTPS/DNS works;
5. `wayfarerctl logs caddy --tail 100`;
6. `wayfarerctl doctor`.

Setup requires normal certificate validation. A DNS or ACME problem is not a
successful managed installation.

## External proxy returns 502 or wrong scheme/client

Confirm the application itself is healthy:

```sh
wayfarerctl status
wayfarerctl doctor
```

Then verify your proxy forwards to the configured loopback port and replaces
untrusted forwarding headers with authoritative values. It must preserve the
public Host and support SSE without buffering/timeouts that cut long-lived
connections.

The operator can verify loopback readiness; public TLS and proxy behavior remain
administrator-owned in external mode.

## Wayfarer is stopped or unhealthy

Use:

```sh
wayfarerctl status
wayfarerctl doctor
wayfarerctl logs wayfarer --tail 200
wayfarerctl logs db --tail 100
```

If the installation is intentionally stopped and no protected operation is
unresolved:

```sh
wayfarerctl start
```

Do not use `restart` to bypass an unresolved setup/update/restore receipt.

## Database is unhealthy

Check the DB service through `status`, `doctor` and DB logs. Do not attach an
ad-hoc host PostgreSQL client to mutate schema while a managed lifecycle operation
is unresolved.

A restore/update refusal related to schema, image or release identity is not fixed
by manually running migrations. Follow the owning restore/update operation.

## Administrator cannot sign in

First confirm the installation is healthy. Then use exact username recovery:

```sh
wayfarerctl user find admin
wayfarerctl user reset-password admin
```

A not-found result is not permission to guess by email or edit Identity tables
directly.

## Backup command is busy or recovery is refused

Run `status` and `doctor` and identify the active owner.

Common safe actions:

- let a running backup finish;
- resume the interrupted restore/update that owns the exclusion;
- use `backup configure --recover` only when the operator identifies a recoverable
  backup-policy transition.

Never delete `recovery.lock`, transition files or worker reservations to make a
busy result disappear.

## Backup exists but restore refuses it

`backups` is only a bounded listing. Verify the pair:

```sh
wayfarerctl verify-backup
```

Restore can still refuse a structurally valid archive if source/target compatibility,
custody, platform, release or recovery authority does not match. Do not edit the
archive manifest or sidecar to force acceptance.

## Restore or update was interrupted

Use the exact operation UUID printed by the owning command:

```sh
wayfarerctl restore --resume <operation-uuid>
wayfarerctl update --resume <operation-uuid>
```

Abort is allowed only before the operation's writer/migration cutoff. If the
operator refuses abort, preserve the state and follow forward recovery.

For update after migration begins, use the managed restore route rather than trying
to run the old image against the changed database.

## Protected provider credentials became unreadable

Personal provider credentials depend on both database records and the **complete
active Data Protection key ring**.

If the database was restored without its matching key ring:

1. stop writers;
2. restore the matching complete ring with correct ownership and restricted
   permissions;
3. verify the configured/resolved key-ring path;
4. start Wayfarer and re-check provider settings.

Do not merge arbitrary key rings or choose keys by modification date.

For user-level key replacement or provider limits, use the
[provider guide](../user/location-providers.md). For native key-ring selection,
see [native/manual deployment](native-manual.md#data-protection-key-ring).

## Disk space is low

Check host/Docker/backup destination capacity before update or restore. These
operations intentionally retain old/candidate generations and recovery evidence
rather than deleting them to make space.

Do not remove a failed candidate volume or held emergency backup while its operation
is unresolved.

Rebuildable application caches may be cleared through supported application/admin
controls when appropriate; durable database, Uploads, key-ring and recovery data
must not be treated as cache.

## Maps or tiles fail to load

Check general connectivity and the active tile provider first. If upstream tile
requests return **403 “Referrer is required”**, verify the public host identity
used by Wayfarer.

`AllowedHosts` must contain semicolon-separated **exact public DNS hostnames**, for
example:

```text
AllowedHosts=wayfarer.example.com
AllowedHosts=wayfarer.example.com;www.wayfarer.example.com
```

Do not use wildcards, URL schemes, ports, IP literals, localhost or private-only
names for this public origin identity. Wayfarer derives the origin-only provider
Referer only after the effective public hostname is authorized by `AllowedHosts`.

Also configure `Application:ContactEmail` (environment form
`Application__ContactEmail`) to a monitored address for the tile-provider
User-Agent contact identity. That setting does **not** configure the Referer.

For an external reverse proxy, correct Host/forwarded-header handling as well; a
valid `AllowedHosts` entry cannot repair a proxy that supplies the wrong effective
public host.

## Mobile does not synchronize

For a single user:

- confirm the public HTTPS URL works;
- confirm the API token has not been revoked;
- confirm the user account is active;
- inspect Mobile queue state;
- confirm the server is healthy.

Do not clear the phone queue merely because an export exists. The
[user import/export guide](../user/import-export.md#recover-mobile-queue-and-local-history-safely)
owns recovery/reconciliation.

## Native/manual installation problems

Do not apply Compose recovery commands to a native/systemd installation. Use the
[native/manual guide](native-manual.md), systemd journal, native PostgreSQL and
reverse-proxy diagnostics for that deployment family.

Automated migration from native/systemd to Compose is not currently supported.

[Self-hosting guide](index.md) · [Operations](operations.md) ·
[wayfarerctl reference](wayfarerctl.md)
