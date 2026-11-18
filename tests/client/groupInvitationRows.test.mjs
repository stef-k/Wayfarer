import assert from 'node:assert/strict';
import test from 'node:test';
import { refreshInviteRows } from '../../wwwroot/js/util/group-invitation-rows.js';

/** Confirmed Invites publish only the current Members read and retain success on failure. */
test('Invite refresh keeps the newest invitation and Revoke control', async () => {
  const previous = { fetch: globalThis.fetch, window: globalThis.window, document: globalThis.document,
    DOMParser: globalThis.DOMParser, wayfarer: globalThis.wayfarer };
  const url = '/User/Groups/Members?groupId=the-group';
  const revoke = { id: 'revoke-new' };
  const replacement = { id: 'replacement-row', revoke };
  const expired = { id: 'expired-row' };
  let current = { children: [expired], replaceChildren(...children) { this.children = children; } };
  const response = body => ({ ok: true, text: async () => body });
  const alerts = [];
  const wired = [];
  try {
    globalThis.window = { location: { href: url } };
    globalThis.document = { querySelector: selector => selector === '#invitesTable tbody' ? current : null };
    globalThis.DOMParser = class {
      parseFromString(body, type) {
        assert.equal(type, 'text/html');
        const incoming = {
          children: body === 'newest' ? [replacement] : [expired],
          querySelectorAll: selector => selector === 'form.js-confirm' && body === 'newest' ? [revoke] : []
        };
        return { querySelector: selector => selector === '#invitesTable tbody' ? incoming : null };
      }
    };
    globalThis.wayfarer = { showAlert: (...message) => alerts.push(message) };
    const older = Promise.withResolvers();
    const later = Promise.withResolvers();
    const reads = [older.promise, later.promise];
    globalThis.fetch = (requested, options) => {
      assert.equal(requested, url);
      assert.equal(options, undefined);
      return reads.shift();
    };
    const olderRefresh = refreshInviteRows(form => wired.push(form));
    const laterRefresh = refreshInviteRows(form => wired.push(form));
    later.resolve(response('newest'));
    await laterRefresh;
    assert.deepEqual(current.children, [replacement]);

    older.resolve(response('older'));
    await olderRefresh;
    assert.deepEqual(current.children, [replacement]);
    assert.deepEqual(wired, [revoke]);
    assert.deepEqual(alerts, []);

    globalThis.fetch = async () => ({ ok: false });
    await refreshInviteRows(form => wired.push(form));
    assert.deepEqual(current.children, [replacement]);
    assert.deepEqual(wired, [revoke]);
    assert.deepEqual(alerts, [['warning', 'Invitation sent. Refresh this page to update pending invitations.']]);

    // A superseded failed read cannot replace that feedback, and a detached table cannot publish.
    const slowFailure = Promise.withResolvers();
    const failureReads = [slowFailure.promise, Promise.resolve(response('newest'))];
    globalThis.fetch = () => failureReads.shift();
    const stale = refreshInviteRows(form => wired.push(form));
    await refreshInviteRows(form => wired.push(form));
    slowFailure.resolve({ ok: false });
    await stale;
    assert.equal(alerts.length, 1);

    const previousTable = current;
    const pending = Promise.withResolvers();
    globalThis.fetch = () => pending.promise;
    const detached = refreshInviteRows(form => wired.push(form));
    current = { ...previousTable, children: [] };
    pending.resolve(response('older'));
    await detached;
    assert.deepEqual(previousTable.children, [replacement]);
    assert.deepEqual(current.children, []);
    assert.equal(alerts.length, 1);
  } finally {
    Object.assign(globalThis, previous);
  }
});
