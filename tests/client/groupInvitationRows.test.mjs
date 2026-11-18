import assert from 'node:assert/strict';
import test from 'node:test';
import { refreshInviteRows } from '../../wwwroot/js/util/group-invitation-rows.js';

/** A confirmed Invite refreshes persisted rows and a failed GET preserves the current list. */
test('Invite refresh adopts the authorized Members rows and keeps success when the read fails', async () => {
  const previous = { fetch: globalThis.fetch, window: globalThis.window, document: globalThis.document,
    DOMParser: globalThis.DOMParser, wayfarer: globalThis.wayfarer };
  const url = '/User/Groups/Members?groupId=the-group';
  const revoke = { id: 'revoke-new' };
  const replacement = { id: 'replacement-row' };
  const current = { children: [{ id: 'expired-row' }], replaceChildren(...children) { this.children = children; } };
  const incoming = { children: [replacement], querySelectorAll: selector => selector === 'form.js-confirm' ? [revoke] : [] };
  const alerts = [];
  const wired = [];
  try {
    globalThis.window = { location: { href: url } };
    globalThis.document = { querySelector: selector => selector === '#invitesTable tbody' ? current : null };
    globalThis.DOMParser = class {
      parseFromString(body, type) {
        assert.equal(type, 'text/html');
        assert.equal(body, 'Members result');
        return { querySelector: selector => selector === '#invitesTable tbody' ? incoming : null };
      }
    };
    globalThis.wayfarer = { showAlert: (...message) => alerts.push(message) };
    globalThis.fetch = async (requested, options) => {
      assert.equal(requested, url);
      assert.equal(options, undefined);
      return { ok: true, text: async () => 'Members result' };
    };
    await refreshInviteRows(form => wired.push(form));
    assert.deepEqual(current.children, [replacement]);
    assert.deepEqual(wired, [revoke]);
    assert.deepEqual(alerts, []);

    globalThis.fetch = async () => ({ ok: false });
    await refreshInviteRows(form => wired.push(form));
    assert.deepEqual(current.children, [replacement]);
    assert.deepEqual(wired, [revoke]);
    assert.match(alerts[0][1], /Invitation sent.*Refresh/);
  } finally {
    Object.assign(globalThis, previous);
  }
});
