---
title: Companion mobile protected Location delivery
---

# Companion mobile protected Location delivery

Backend/Web #947 implements the approved
[wire and recovery contract](../development/location-mutations.md). Companion
WayfarerApp source and native qualification are separately owned under App
#16/#18/#34. [T3 at App main 2ec54e5](https://github.com/stef-k/WayfarerApp/blob/2ec54e53e85c3a194f2dbcd1b813855fb10d33a1/docs/planning/timeline-editing-contract.md)
now aligns with approved #947 after PR #75 merged during this implementation.
Retained earlier proposal/compatibility wording is superseded by the live approved
issue. This handoff does not claim installed/native/production
acceptance or authorize #951 batch transport.

## Wire and identity obligations

Use owner detail plus POST/GET on
`/api/location/{id}/mutations/{baseId}/{actionId}`. Persist the configured server,
subject, target/key-null identity, edit-start originals, exact desired generation,
selected fields and every immutable attempt. The linked wire reference specifies
all exact shapes, tagged availability, expected-group unions, strict parsing,
normalization, raw delete proof, seven-frame digest, fixed vectors, outcomes and
finite server resource values. Copy those vectors into the existing client suite;
do not create a cross-platform harness or implement a client sanitizer.

Compute the fingerprint before first handoff from exact persisted body bytes and
canonical route/adapter/subject identity. MayHaveAttempted must commit before any
possible network handoff. Token refresh may change headers only. Restart is not a
new action. Validate even the first recovered receipt against the pre-send digest,
all correlation members, known state/shape and current configured account/server.
Malformed/mismatching response retains Unknown. Late replies update only the old
partition/action, never replacement-account work.

Keep ObservationRepository/LocationSync original-first delivery: repeated local
edits do not change/re-key the original capture or turn a correction into the
first create payload. A keyed winner proves linkage, not desired correction.
Preserved authored originals remain the base even when another create won.
Genuinely server-only dependencies need inspected, informed adoption; never
invent nulls. A keyless server row has no invented original-create obligation.

## Long-offline recovery

Phone captures, edits, deletes, originals and possible attempts never expire by
age. Storage follows actual configured policy/available space; no new phone quota
or TTL. Failure to durably save new typing is Not saved; it cannot prune older
protected work. Preserve saved pause and newer obligations independently of
attempt count and receipt history.

Never-attempted expired authority can renew base/handle/action while retaining
expected originals exactly. Possibly attempted work first recovers its original
action. Retained terminal receipt excludes future effects of that action; live
Pending/no-row before expiry still permits them. Keep newer intent durable while
recovering/exact-retrying or reaching synchronized signed expiry. Exclude all
relevant predecessors before reconciliation. A failed detail read does not erase
known applied evidence or authorize another save.

After exclusion, inspect fresh authorized state and reconcile only the captured
desired generation in a local transaction; a newer generation defeats settlement:

| Fresh facts | Permitted action/label |
| --- | --- |
| All required retained originals still equal | New conditional action with fresh authority and unchanged originals; final server comparison owns races |
| Exact selected desired state/full footprint satisfied | Scoped current-state observation, historical outcome unknown; no fabricated receipt or replacement mutation |
| Changed original and incomplete desired footprint | Needs attention with original/draft/fresh facts; informed new generation, cancellation of unattempted overlay or retained pending work |
| Keyed absent plus affirmative owner/key marker | Deletion verified, actor unknown; absorb keyed non-resurrection without claiming the old action's success |
| Keyless absent plus qualified location-target-absence-1 | Target currently absent, historical outcome unknown; retain evidence and never match a recreated row by values |
| Proof/auth/storage unavailable | Specific actionable recovery debt; no speculative execution or age-based abandonment |

Address-dependent satisfaction requires fresh
originalCreateAddressPublication closed. Pending plus all-null/equal address does
not exclude original-create output. After predecessor exclusion the already
desired explicit position/manual command may close pending authority with retained
matching originals, even when value-equal; it does not rerun enrichment. Coordinates
alone do not prove the full footprint. Notes/Activity-only progress does not wait
for irrelevant address-history attribution. Mutation expiry separately excludes
its executor, not the original-create publisher or later authorized enrichment.
Position-only work preserves the independently authoritative capture instant;
explicit time-changing generations evaluate their own eventUtc.

For deletion before an unsent create, suppress handoff only when never-attempted
is atomically proven at finalHandoff. Any possible create requires linkage/recovery,
unchanged original replay when safe, inspected equivalent content and protected
delete. Unknown key/404 is not deletion proof. Phone erasure still requires its
existing protected-loss authority. Server deletion uses retained raw observed
content proof, including damaged rows, rather than edit validators.

## Automatic bounded retry re-admission

Extend the existing LocationSync, LocationWorkRepository/LocationWork.sq,
ObservationRepository/Delivery.sq and SessionOwner boundaries. Make the existing
four-handoff budget per burst, separate from cumulative handoff_count and Unknown
history. Persist burst count plus positive next-eligible cooldown/backoff and the
full applicable Retry-After, including values beyond one day, atomically with the
outcome/evidence. Preserve the later not-before value. Wake/restart/session renewal
must not reset it.

Existing connectivity/session/nextWake/timer paths reconsider due retry-only work.
Already-unpaused work automatically re-admits one safe bounded burst after the
full cooldown; it does not require another Resume. Recheck owner/session, actual
saved pause, predecessor exclusion, key/deletion/cancellation evidence and
immutable replay safety, including immediately at finalHandoff. Reset only the
per-burst counter; keep cumulative attempts and possible-handoff evidence. Due
exhausted work remains discoverable without spin or an immediate Held loop.

Only protected explicit Resume clears an actual saved user pause, and it still
honors cooldown/Retry-After. Reconnect, Apply, renewal and older success never
resume a paused queue. Auth loss/conflict/missing proof keep their specific
requirements. No guaranteed OS background execution or new queue engine follows.

## Bounded companion acceptance

Use existing deterministic domain/storage owners and fake clocks. A15–A17 belong
to mobile; backend tests do not qualify them. Cover six months and beyond receipt
retention, repeated local edits, raw original create preservation, possible-create
deletion, every reconciliation branch, newer-generation survival, same-owner
restart/account isolation and saved pause. Reuse backend A13's held-create
Set → Clear publication rule in the client recovery scenarios.

A17 must exhaust an unpaused burst, show no send before persisted full cooldown,
restart/reconnect after eligibility and observe one bounded safe burst without
another Resume. Keep cumulative attempts/Unknown intact. Separately prove saved
pause prevents handoff until protected Resume, which still honors cooldown. Low
space may prevent a new durable save but must not prune original/base/Unknown or
newer work. Use no months-long waits, paid-provider calls, repeated backlog imports
or new harness.
