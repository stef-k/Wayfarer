import assert from 'node:assert/strict';
import test from 'node:test';
import { build } from 'esbuild';

const { outputFiles: [bundle] } = await build({
  entryPoints: ['ClientApps/trip-editor/src/recovery/dependentDispatch.ts'],
  bundle: true, write: false, format: 'esm',
});
const { dependentRefusal } = await import(`data:text/javascript;base64,${Buffer.from(bundle.text).toString('base64')}`);

test('a qualified route-clear refusal releases the Segment invocation for explicit confirmation', () => {
  const id = '3456789a-4567-4456-8456-456789abcdef';
  const url = `/api/trips/12345678-1234-4234-8234-123456789abc/editor/segments/${id}`;
  const problem = {
    code: 'segment-route-clear-confirmation-required', operation: 'update', currentSegment: { id },
    warning: 'Confirm clearing the route.', expiresAt: '2026-11-29T12:00:00Z',
  };
  const reply = new Response(null, { status: 409, headers: { 'X-Wayfarer-Clear-Route-Confirmation': 'test-approval' } });
  assert.equal(dependentRefusal(reply, problem, 'PUT', url), true);
  assert.equal(dependentRefusal(reply, { ...problem, currentSegment: { id: crypto.randomUUID() } }, 'PUT', url), false);
  assert.equal(dependentRefusal(new Response(null, { status: 409 }), problem, 'PUT', url), false);
  assert.equal(dependentRefusal(reply, { code: 'segment-unspecified', status: 409 }, 'PUT', url), false);
});
