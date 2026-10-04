# Troubleshooting

Start with the symptom below. Ordinary users should contact their instance administrator for server-side checks. Self-hosters should use the [wayfarerctl operator guide](29-Wayfarerctl.md) for the full procedure and act on the reported recovery instruction.

For installed Compose servers, the examples use `sudo ./wayfarerctl dispatch` from the trusted directory containing your root-owned bootstrap. Dispatch selects the installation's retained operator. If you use a custom installation root, put `--deployment-root /your/path` before `dispatch`; see [operator placement](29-Wayfarerctl.md#placement-and-prerequisites). Unfinished setup follows its own retry/resume instructions below.

## Setup Was Interrupted or Is Incomplete

Read the last setup message before retrying:

- **Setup has not started** — correct the reported download, verification, or prerequisite problem and rerun the original `sudo ./wayfarerctl setup` command with its original options. Preserve preparation files; `doctor` and `setup --resume` are not recovery actions at this boundary.
- **Setup has started** — preserve installation files, secrets, and volumes, correct the cause, then use `sudo ./wayfarerctl setup --resume` with the original deployment-root option.
- **Cannot safely resume** — preserve the refused state for administrator reconciliation. Repeated setup attempts or deleting files cannot establish the missing authority.

See [interrupted setup](29-Wayfarerctl.md#interrupted-setup-and-troubleshooting) and [installation prerequisites](02-Install-and-Dependencies.md#guided-production-installation), including the separate instruction for uncertain administrator bootstrap.

## The Site Is Unavailable or Unhealthy

Check the existing installation without changing it:

```sh
sudo ./wayfarerctl dispatch status
sudo ./wayfarerctl dispatch doctor
```

Follow the first reported failure. These commands diagnose; they do not start services or repair state. A deliberately stopped stack is unhealthy until you explicitly start it. See [diagnosis and service health](29-Wayfarerctl.md#diagnosis-logs-and-user-recovery).

## Managed HTTPS, DNS, or Certificates Fail

Check that the configured public hostname points at this server, that no other listener owns the ingress ports, and that 80/TCP, 443/TCP, and 443/UDP are available. Inspect managed Caddy's logs:

```sh
sudo ./wayfarerctl dispatch logs caddy --tail 80
```

Preserve Caddy's TLS volumes. Setup must verify public HTTPS health with normal certificate validation; a stale DNS endpoint or blocked certificate request is not successful setup. See [ingress modes](29-Wayfarerctl.md#choose-an-ingress-mode) and [setup troubleshooting](29-Wayfarerctl.md#interrupted-setup-and-troubleshooting).

## An External Proxy Works Locally but Fails Publicly

First check the proxy's public TLS, preserved Host header, authoritative forwarded scheme/host/client headers, and the actual configured trusted hop. External mode verifies the app's loopback endpoint; it leaves public HTTPS and proxy forwarding with the administrator. Private or loopback addresses alone do not make a proxy trusted.

See [external ingress](29-Wayfarerctl.md#choose-an-ingress-mode) and [proxy security](21-Security.md). Correct the configured trust rather than accepting arbitrary forwarded headers.

## Sign-In, Password, or Account Access Fails

Use your username and the correct instance URL. If you can sign in, change your password or manage 2FA through account management. If you cannot, ask an administrator to check account activity/lockout and reset your password; registration does not collect email for automated recovery.

For server administrator recovery:

```sh
sudo ./wayfarerctl dispatch user find admin
sudo ./wayfarerctl dispatch user reset-password admin
```

Reset collects a hidden, confirmed password; never put it in command arguments. Substitute the exact username when recovering another account. See [user recovery](29-Wayfarerctl.md#diagnosis-logs-and-user-recovery) and [password/security basics](01-Getting-Started.md#production-password-policy).

## You Need Logs or a Useful Diagnosis

Start with `status` and `doctor`, then inspect the affected service:

```sh
sudo ./wayfarerctl dispatch logs wayfarer --tail 80
sudo ./wayfarerctl dispatch logs db --tail 80
```

Admins can also use the web Log Viewer for application logs. Record the symptom and bounded error category without sharing credentials, private exports, or personal location data. See [logs and diagnosis](29-Wayfarerctl.md#diagnosis-logs-and-user-recovery) and [Security](21-Security.md).

## Backups Are Missing, Unconfigured, or Fail Verification

Check `doctor` for the backup policy, destination, and scheduler state. Confirm that the configured dedicated destination still exists with the required ownership and space. List owned complete pairs with `sudo ./wayfarerctl dispatch backups`, then verify the selected one with `sudo ./wayfarerctl dispatch verify-backup <owned-archive-basename>`.

Listing is not full verification, and integrity checks alone do not establish restore compatibility or trusted provenance. Recovery sets retain the database, uploads, and complete active Data Protection ring together. For configuration, destination rules, or interrupted configuration, follow [Compose recovery sets](29-Wayfarerctl.md#compose-recovery-sets); preserve receipts and locks instead of deleting them.

## An Update Is Refused or Needs Planning

Use `sudo ./wayfarerctl dispatch update --plan` to acquire and inspect the latest stable target. Planning may download verified release bytes; it does not authorize execution. Check the reported source/target compatibility, retained release authority, recovery configuration, and available capacity.

Execution requires the exact printed plan hash. Preserve any unresolved operation and use its reported recovery action. Do not substitute an older image for database rollback. See [public update planning](29-Wayfarerctl.md#public-stable-acquisition-and-update-planning) and [managed update/refusal](29-Wayfarerctl.md#trusted-local-managed-forward-update).

## Restore or Recovery Is Refused

Read `status`/`doctor` and the refusal before choosing another action. Preserve the selected archive/sidecar, installation receipts, held recovery sets, and candidate volumes. Verify custody, integrity, the exact target release contract, and capacity through the documented plan procedure.

Resume and abort depend on the recorded phase and whether writes may have occurred; do not delete markers, merge key rings, or start stale source data to force progress. See [managed restore](29-Wayfarerctl.md#managed-restore) and [activation and recovery](29-Wayfarerctl.md#activation-recovery-and-retained-evidence).

## An Import Fails or Appears Stuck

Confirm the selected importer matches the file format and required fields, and check the upload-size limit. Refresh Import History to read durable progress and its result; an administrator can inspect application logs if processing still fails. Inspect or retry partial/failed imports before clearing any mobile work.

Blank addresses do not mean the import failed: optional enrichment has separate progress. See [import formats and controls](07-Importing-Exporting.md#importing-data) and [mobile recovery](07-Importing-Exporting.md#offline-queue-recovery).

## Addresses or Provider Routes Are Unavailable

Check the active provider's credential, capability authorization, verification, selection, and remaining usage allowance. Mapbox also needs explicit Permanent storage consent and its separate Permanent meter capacity. Capture and imports remain usable while enrichment is paused, retaining existing address data.

For opted-in enrichment, **PausedByAuthority** needs corrected authority followed by Resume; **PausedByBudget** retains its provider-specific wake; **BackingOff** waits for its retry. Use **Retry deferred** only for eligible no-result/attempt-limit rows. See [enrichment controls and repair](07-Importing-Exporting.md#resumable-reverse-geocoding-optional).

Geoapify route previews need an explicit supported provider mode; a Segment Transport Profile does not select it. Failure or exhaustion preserves accepted geometry, with no fallback to Mapbox, public OSRM, or another routing provider. See [provider profiles and guards](24-Personal-Location-Providers.md) or [credential/key-ring recovery](24-Personal-Location-Providers.md#existing-installations-and-advanced-recovery) for unreadable credentials and legacy conflicts.

## Maps or Tiles Do Not Load

Check connectivity and the active tile provider first. Ask the administrator to check cache storage permissions and provider configuration. For **403 “Referrer is required”**, use exact public DNS hostnames in `AllowedHosts`: `wayfarer.example.com` or `wayfarer.example.com;www.wayfarer.example.com`. Entries are semicolon-separated; wildcards, IP literals, localhost/private names, ports, and URL schemes are invalid. `Application:ContactEmail` supplies the User-Agent contact identity, not Referer.

See [Maps and Tiles](03-Features.md#maps-and-tiles) and [installation configuration](02-Install-and-Dependencies.md#configuration-and-ongoing-operation). A blank mobile offline basemap has a different cause: [mobile tile caching](08-Mobile.md#offline-maps) does not guarantee complete coverage.

## Mobile Is Not Synchronizing

Test the configured server URL and API token, confirm that your account is active, and check connectivity, tracking permissions, and battery restrictions. If only live updates stop, check the app's version and [SSE recovery behavior](08-Mobile.md#real-time-updates-sse).

Do not clear app data or discard queued locations. Phone Timeline, pending delivery, and server history are separate; follow [mobile troubleshooting](08-Mobile.md#troubleshooting) and [queue recovery/reconciliation](07-Importing-Exporting.md#offline-queue-recovery).

## Disk or Storage Problems Stop Work

Check free space and permissions at the affected Docker, application-data, cache, import, or backup location. `status`/`doctor` identify installation mounts and refused state; the Admin disk-usage summary helps identify cache/upload use. Preserve durable volumes, uploads, complete keys, retained releases, and unresolved operation evidence.

Use documented cache controls for rebuildable data. Restore and update need additional staging space; see [durable state](29-Wayfarerctl.md#secrets-and-durable-state), [restore planning](29-Wayfarerctl.md#select-plan-and-authorize), and [update planning](29-Wayfarerctl.md#trusted-local-managed-forward-update).

## A Page Is Unavailable for Your Account

Check your role with the administrator. Admin, Manager, and User accounts have different capabilities, and group visibility depends on membership/settings. See [roles](01-Getting-Started.md#role-overview) and [group privacy](05-Groups.md#privacy-and-visibility).
