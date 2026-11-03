---
title: Profile photos
---

# Profile photos

This backend/Web contract implements #881. Mobile rendering/native qualification
is separately owned. Photos are optional identity decoration, independent of
location visibility and Trip publication. Never put photo references in generic
Trip content DTOs, timelines, exports, cover images or proxy rewriting.

## Owner and read routes

| Method/context | Route | Contract |
| --- | --- | --- |
| Cookie owner GET | `/api/profile-photo/me` | `{stateToken,profilePhoto,showProfilePhotoOnPublicTrips}` |
| Cookie owner POST | `/api/profile-photo/me` | Streamed multipart `photo`, required `expectedStateToken`, optional complete integer `cropX,cropY,cropSize` |
| Cookie owner DELETE | `/api/profile-photo/me` | JSON `{expectedStateToken}`; accepted absence still rotates state |
| Cookie owner bytes | `/api/profile-photo/me/<version>` | Active owner and exact current version |
| Cookie Group bytes | `/api/groups/<groupId>/members/<userId>/photo/<version>` | Both active accounts/members in this exact unarchived Group |
| Bearer Group bytes | `/api/mobile/groups/<groupId>/members/<userId>/photo/<version>` | Same policy through fresh incoming-token resolution |
| Anonymous Trip bytes | `/Public/Trips/<tripId>/owner-photo/<version>` | Owner derived from exact public Trip, active owner, saved explicit consent, exact current version |

Group members add nullable `profilePhoto:{version,url}` beside unchanged
`colorHex`. Version is lowercase UUID-N file identity, never a path, credential
or mutation precondition. No owner state token enters Group/public responses,
URLs or diagnostics. No ownership/role/any-shared-Group bypass is permitted.
Photo audience remains independent of location-sharing flags.

Private bytes and owner/Group reference responses are `private, no-store`;
public bytes are `no-store`, including denials. Every byte read rechecks persisted
authority before file open. No ETag/Last-Modified/304/static redirect. Conditional
requests still authorize. Reads begun after committed revocation fail; an already
authorized/open request may finish. Downloaded/displayed copies cannot be recalled.
Clients should avoid persistent image caching and evict references on refreshed
null/version changes, membership loss and logout. Web focus/normal roster refresh
updates references; one failed reference may refresh the roster once, without loops.

Owner mutations require the application cookie and `RequestVerificationToken`
antiforgery header before body reads. POST carries real `UserFileUpload` metadata,
so existing administrator disabled/stricter upload policy applies. Limit the file
to 15 MiB and the request to 16 MiB, with bounded headers/fields and actual byte
counting. Passive endpoint metadata lets upload middleware retain the minimum of
the administrator, active host and endpoint request limits through MVC and body
consumption, including chunked requests without Content-Length.
Unsupported phone formats must prompt export/selection of JPEG, PNG or
static WebP. The server owns format, geometry, orientation, color and crop authority.

## State and explicit recovery

`ApplicationUser.ConcurrencyStamp` is an opaque nonempty owner token, including
during absence. Legacy missing stamps are persisted under the owner-row lock.
Publication compares the supplied token after processing under that lock, rotates
it on every accepted upload/removal and updates only photo identity/stamp.
Unrelated Identity changes can conflict harmlessly. Whole-user Profile saves keep
Identity's optimistic concurrency checks, preserving concurrent photo updates.

Both mutations return 200 with committed owner state, including the rotated token.
A 409 `photo_changed` or 503 `photo_outcome_unknown` requires the user's explicit
**Refresh photo state**, then a separate deliberate Save/Remove. Retain framing
and disclose saved public consent; never adopt an error token or replay silently.
Missing/empty state is 400 `invalid_photo_state`. Malformed media/crop is 400,
unsupported format 415, positive size/geometry rejection 413, disabled uploads 403,
overall timeout 408 `photo_upload_timeout`, processing/storage failure 503.
Private missing authentication is 401 without HTML redirect; authenticated absent,
forbidden and stale bytes share 404. No public diagnostic contains source paths,
decoder metadata or credentials.

The account-wide public preference defaults off and is saved independently in
the Profile form. Upload, replacement, removal and Trip publication preserve it.
Consent therefore applies to future photos too; the UI discloses this before Save.

## Processing and lifetime

The [contained helper](../../tools/image-helper/README.md#profile-photo-recipe-881)
accepts JPEG (including gray/CMYK/progressive), PNG (8/16-bit) and static WebP.
Reject declared animations, malformed/truncated media and unsupported containers.
Maximum axes are 16,384 and pixels 40,000,000. One no-queue upload slot is acquired
before body reads; one 60-second deadline covers body, helper and commit. Helper
time is at most min(30 seconds,remaining), under unchanged 1-GiB/CPU containment.
Cancellation reaches body, helper and DB work. Helper cleanup retains its two-second
watchdog and fails closed if reaping is unconfirmed; transaction cleanup uses an
independent three-second watchdog. Under functioning OS/IO the cleanup budget is
at most five seconds. No queue/rate-limit/configuration subsystem is introduced.

Crop coordinates are integer oriented source pixels. Missing crop selects the
centered square with floored origin. Output side is min(256,cropSize), never enlarged;
retain alpha, normalize supported source colors to sRGB and encode quality85 static
WebP <=256 KiB. Full decode and container inspection verify completed output;
no source EXIF/GPS/XMP/ICC/comments/orientation is inherited. Circular masking is UI.

## Durable publication, erasure and recovery

Only `StoragePaths.Uploads/profile-photos` stores generated `<guid-N>.tmp` and
`<guid-N>.webp`, with private directory/file permissions and Linux handle-relative
no-symlink/nonregular checks. Missing/misdirected/unwritable configured storage fails
closed. No original/client filename, public static path, cache store, new volume,
ledger, deletion queue or release setting is involved.

Short PostgreSQL transaction advisory lock `(881,1)` (separate two-int namespace), bounded lock/statement
waits and the active owner row lock order publication/removal and cleanup. Decode
and network work hold no DB lock. Flush the new unique temp file, rename without
overwrite and sync the directory before updating/committing the pointer. Never
reuse/reactivate a candidate key after any failure or unknown commit. A dead
session cannot reconnect to publish its old key. Definite rollback/file failures
preserve the old pointer/file. Uncertain commit retains both candidates for fresh
primary-DB authority; no request-local guess authorizes deletion.

After confirmed mutation, best-effort targeted cleanup runs outside request
admission. Startup and five-minute Quartz sweeps cover complete successive
128-entry batches. Each batch takes the same lock and one fresh committed primary
reference lookup before unlink. Database uncertainty deletes nothing. Unexpected
entries/links and IO failures remain evidence for later diagnosis/retry. Account
erasure through Admin, Manager, Identity self-service or SQL removes the sole
active reference; subsequent reads deny, with private orphan unlink at the next
healthy sweep, normally within five minutes. Row deletion is not completed unlink.

Existing recursive Uploads backup/extraction includes photos. Use the established
quiesced DB-plus-files recovery point for coherence; online capture is not atomic.
Historical backup retention remains unchanged. Restored orphans are swept; a
referenced missing file yields fallback/404 and fixed diagnostics, without silently
clearing its pointer. Import cleanup keeps its existing imports/legacy namespaces.

The generic public image proxy rejects parsed private/no-store final origins
before reading/storing/serving success, including redirects. Photos are omitted
at thumbnail screenshot and print-clone consumers. This does not recall unrelated
historical third-party cache entries or copies retained by another service.
