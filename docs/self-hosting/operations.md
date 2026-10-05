---
title: Operate a Wayfarer instance
---

# Operate a Wayfarer instance

This guide covers ordinary administration after a supported Compose installation.
It gives the shortest safe workflow for common tasks. For exact options and refusal
semantics, use the [wayfarerctl reference](wayfarerctl.md).

Run management commands as root. If you keep a fixed bootstrap, prefer
`wayfarerctl dispatch COMMAND` so the bootstrap selects the operator retained for
the active installation.

## Check the instance

Start with read-only checks:

```sh
wayfarerctl status
wayfarerctl doctor
```

`status` reports deployment identity, service/health state, image/version and
database/setup facts.

`doctor` performs bounded cross-service diagnostics and returns nonzero when the
installation is unhealthy. It does not repair or start services.

A setup-complete marker records history, not present health. A deliberately stopped
instance is expected to appear unhealthy until started again.

## Start, stop or restart

```sh
wayfarerctl start
wayfarerctl stop
wayfarerctl restart
```

- `start` starts the configured services and waits for health. It never pulls,
  updates or migrates.
- `stop` performs a graceful stop and preserves every volume.
- `restart` stops, starts and verifies the existing configuration.

Stopping Wayfarer is **not** uninstalling it. Do not use `docker compose down -v`
as an operator shortcut; deleting volumes destroys durable state.

## Read logs

```sh
wayfarerctl logs
wayfarerctl logs wayfarer --follow
wayfarerctl logs db --tail 80
wayfarerctl logs caddy --tail 100
```

The default service is `wayfarer`. `caddy` exists only in managed ingress mode.
Tail counts must be between 1 and 10000.

The operator withholds known credential/key-material lines, but logs are still
administrator data. Do not paste secrets, tokens, private locations or connection
strings into support reports.

## Recover a user account

Find the exact username first:

```sh
wayfarerctl user find admin
```

Reset its password:

```sh
wayfarerctl user reset-password admin
```

The password is entered hidden and confirmed. For protected automation:

```sh
wayfarerctl user reset-password admin --password-stdin \
  < /root/wayfarer-recovery-password
```

Use a root-owned protected input file and remove it when no longer needed. The
operator does not guess by email, partial username or database identity.

## Protect the instance with backups

Managed backups capture the three durable recovery owners together:

- PostgreSQL;
- the complete active Data Protection key ring;
- durable Uploads.

### Configure a local destination

Create and mount the destination yourself, then configure it:

```sh
wayfarerctl backup configure \
  --destination /srv/wayfarer-backups \
  --payload /etc/wayfarer/releases/recovery-v1/wayfarer-recovery
```

The destination must already exist and be dedicated to this installation. For an
administrator-mounted filesystem, use the documented `--kind mounted` layout in
the [reference](wayfarerctl.md#backup-configuration).

Default scheduling is daily at 03:00 UTC with stable 0–15 minute jitter and seven
complete retained sets. Change the schedule/retention deliberately when required.

### Capture, list and verify

```sh
wayfarerctl backup
wayfarerctl backups
wayfarerctl verify-backup
```

`backups` is a bounded listing, not full verification. Use `verify-backup` to
check a selected/default pair before relying on it.

For the strongest migration/recovery boundary, take a quiesced capture:

```sh
wayfarerctl backup --quiesced
```

A quiesced backup leaves the application stopped. Start it again only after the
capture completes and the protected transition is resolved:

```sh
wayfarerctl start
```

Online backups are coherent per component but are **not an atomic snapshot across
PostgreSQL and the filesystem**. Plan accordingly when exact cross-component
consistency matters.

### Disable or recover backup configuration

```sh
wayfarerctl backup configure --disable
wayfarerctl backup configure --recover
```

Use `--recover` only for the protected transition it owns. If it refuses because
worker/restore/update evidence is unresolved, inspect that owning operation first.
Never delete the recovery lock or transition receipt to force progress.

## Plan and apply an update

Supported Compose update history begins at v1.9.21. Update is forward-only and
requires an exact accepted plan.

Plan the latest supported stable release:

```sh
wayfarerctl update --plan
```

Or choose an exact version:

```sh
wayfarerctl update X.Y.Z --plan
```

For a trusted local target:

```sh
wayfarerctl update --bundle /absolute/trusted/bundle --plan
```

Review the plan, then execute only that exact plan hash:

```sh
wayfarerctl update --accept-plan <printed-sha256>
```

The update owner requires a fresh verified quiesced recovery set before migration.
A recent ordinary backup is not silently substituted.

If update is interrupted, use the operation UUID reported by the operator:

```sh
wayfarerctl update --resume <operation-uuid>
```

Abort is allowed only before migration may have started:

```sh
wayfarerctl update --abort <operation-uuid>
```

After migration may have started, old images are **not** database rollback. Follow
the operator's managed restore path instead:

```sh
wayfarerctl update --restore <operation-uuid>
```

Preserve retained releases, images, receipts and held recovery evidence until the
operation reaches a terminal state.

## Restore a backup

Restore is destructive by design, so it is split into planning and explicit
authorization.

Plan the newest owned complete recovery pair:

```sh
wayfarerctl restore --restore-payload /trusted/wayfarer-recovery --plan
```

Or select an owned archive basename:

```sh
wayfarerctl restore <owned-archive-basename> \
  --restore-payload /trusted/wayfarer-recovery --plan
```

After reviewing the plan and confirming controlled custody:

```sh
wayfarerctl restore \
  --accept-plan <printed-sha256> \
  --trust-controlled-backup
```

Restore stages the database, Data Protection ring and Uploads into **fresh
candidate volumes**, validates them offline and only then activates them. It does
not merge key rings, seed replacement data or silently migrate an archive.

If restore is interrupted:

```sh
wayfarerctl restore --resume <operation-uuid>
```

`restore --abort <operation-uuid>` is permitted only before a candidate
application writer could have run. After writes may have occurred, recovery is
forward-only. Preserve the candidate and receipts; do not manually repoint volumes.

For complete-host loss, use the clean-host/new-install procedure in the
[reference](wayfarerctl.md#clean-host-disaster-recovery).

## When a protected operation is unresolved

Update, restore, setup and backup configuration record durable ownership before
irreversible steps. While one of those operations is unresolved, other mutating
commands may refuse to run.

That refusal is a safety feature.

Use this order:

1. run `status` and `doctor`;
2. read the operation UUID and recommended action;
3. preserve deployment files, volumes and recovery evidence;
4. resume/abort through the **same owning command** when permitted;
5. do not edit receipts, lock files, generated deployment files or volume pointers.

If the state is contradictory or marked unsafe to resume, stop and preserve it for
administrator investigation.

## Routine security checks

- keep the public host patched and Docker access restricted to administrators;
- use HTTPS and correct trusted-proxy settings;
- keep registration closed unless intentionally enabled;
- enable 2FA on privileged accounts;
- revoke exposed API/provider credentials;
- verify backups, not just their existence;
- keep the deployment root and backup destination non-world-readable;
- monitor disk capacity before updates/restores.

For user-facing provider privacy and billing controls, see
[Personal location providers](../user/location-providers.md).

[Self-hosting guide](index.md) · [wayfarerctl reference](wayfarerctl.md) ·
[Troubleshooting](troubleshooting.md)
