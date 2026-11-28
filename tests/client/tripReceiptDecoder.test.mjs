import assert from 'node:assert/strict';
import test from 'node:test';
import { build } from 'esbuild';

const module = await build({
  entryPoints: ['ClientApps/trip-editor/src/recovery/receiptApi.ts'], bundle: true, write: false, format: 'esm', platform: 'node'
});
const { lookupAttempt, sendAttempt } = await import(`data:text/javascript;base64,${Buffer.from(module.outputFiles[0].text).toString('base64')}`);

const endpoint = '/api/trips/12345678-1234-4234-8234-123456789abc/editor';
const identity = {
  contract: 'trip-metadata-receipt-2', subjectId: 'owner-a', adapter: 'web', kind: 'tags-replace',
  tripId: '12345678-1234-4234-8234-123456789abc', operationId: '87654321-1234-4234-8234-123456789abc',
  requestDigest: 'sha256:' + 'a'.repeat(64), executeBeforeUtc: '2026-11-28T10:00:00Z'
};
const attempt = { permit: { ...identity, proof: 'opaque' },
  body: '{"receiptContract":"trip-metadata-receipt-2","tags":["Day Hike"]}', submitted: { tags: 1 } };
const tag = { id: '55555555-1234-4234-8234-123456789abc', slug: 'day-hike' };
const saved = { ...identity, state: 'applied', historicalOutcome: 'applied', futureEffects: false,
  completedAtUtc: '2026-11-28T09:55:00Z', receiptRetainUntilUtc: '2026-12-28T10:00:00Z',
  tagResult: { addedJoins: [tag], removedJoins: [], createdCatalogue: [tag], deletedCatalogue: [],
    resultingTags: [{ ...tag, name: 'Day Hike' }] } };

/** Exercises the same decoder used by Editor, Viewer, and My Trips recovery. */
test('tag recovery accepts the same identity across arrays and rejects malformed scoped members', async t => {
  const wire = JSON.stringify(saved);
  const fake = t.mock.method(globalThis, 'fetch', async () => new Response(wire));
  assert.deepEqual(await lookupAttempt(endpoint, attempt), saved);
  assert.equal(fake.mock.calls[0].arguments[0], endpoint + '/tags/conditional/operations/' + identity.operationId);
  const invalid = [
    wire.replace(`"id":"${tag.id}"`, `"id":"${tag.id}","\\u0069d":"${tag.id}"`),
    wire.replace('"deletedCatalogue":[]', '"unknownEffect":[]'),
    wire.replace('"name":"Day Hike"', '"name":20')
  ];
  for (const malformed of invalid) {
    fake.mock.mockImplementationOnce(async () => new Response(malformed));
    await assert.rejects(lookupAttempt(endpoint, attempt), /Trip receipt/);
  }
});

/** A 400 only releases custody when its complete refusal matches the retained permit. */
test('validation settlement requires a correlated refusal with field guidance', async t => {
  const { tagResult, ...common } = saved;
  const refusal = { ...common, state: 'terminalUnapplied', historicalOutcome: 'unapplied',
    code: 'mutation-request-invalid', errors: { tags: ['Tag too long (max 64 characters).'] } };
  const fake = t.mock.method(globalThis, 'fetch', async () => Response.json(refusal, { status: 400 }));
  assert.deepEqual(await sendAttempt(endpoint, 'anti', attempt), refusal);
  fake.mock.mockImplementationOnce(async () => Response.json(refusal));
  assert.deepEqual(await lookupAttempt(endpoint, attempt), refusal);

  for (const [body, status] of [
    [{ code: refusal.code, errors: refusal.errors }, 400],
    [{ ...refusal, requestDigest: 'sha256:' + 'b'.repeat(64) }, 400],
    [{ ...refusal, errors: { tags: [64] } }, 400],
    [refusal, 500]
  ]) {
    fake.mock.mockImplementationOnce(async () => Response.json(body, { status }));
    await assert.rejects(sendAttempt(endpoint, 'anti', attempt));
  }
});
