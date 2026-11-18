import assert from 'node:assert/strict';
import test from 'node:test';
import { refreshInviteRows, removeInviteRow } from '../../wwwroot/js/util/group-invitation-rows.js';

/** Models the pending rows and their Revoke forms in a Members snapshot. */
const inviteTable = (...rows) => ({
  children: rows,
  replaceChildren(...children) { this.children = children; },
  querySelector(selector) {
    return this.querySelectorAll('tr[data-invite-id]')
      .find(row => selector === `tr[data-invite-id="${row.dataset.inviteId}"]`);
  },
  querySelectorAll(selector) {
    if (selector === 'form.js-confirm') return this.children.flatMap(row => row.revoke ? [row.revoke] : []);
    if (selector !== 'tr[data-invite-id]') return [];
    return this.children.map(row => ({
      dataset: { inviteId: row.id },
      remove: () => this.replaceChildren(...this.children.filter(child => child !== row))
    }));
  }
});

/** Confirmed Invites reconcile the current read with confirmed removals and retain success on failure. */
test('Invite refresh preserves new invitations and Revoke controls without restoring removed rows', async () => {
  const previous = { fetch: globalThis.fetch, window: globalThis.window, document: globalThis.document,
    DOMParser: globalThis.DOMParser, wayfarer: globalThis.wayfarer };
  const url = '/User/Groups/Members?groupId=the-group';
  const revoke = { id: 'revoke-new' };
  const replacement = { id: 'replacement-row', revoke };
  const expired = { id: 'expired-row' };
  const accepted = { id: 'accepted-row', revoke: { id: 'revoke-accepted' } };
  const declined = { id: 'declined-row', revoke: { id: 'revoke-declined' } };
  const revokedByNotification = { id: 'revoked-row', revoke: { id: 'revoke-notified' } };
  const newInvite = { id: 'new-invite-row', revoke: { id: 'revoke-new-invite' } };
  const snapshots = {
    newest: [replacement],
    older: [expired],
    'racing-removals': [accepted, replacement, declined, revokedByNotification, newInvite]
  };
  let current = inviteTable(expired);
  const response = body => ({ ok: true, text: async () => body });
  const alerts = [];
  const wired = [];
  const attach = form => wired.push(form);
  try {
    globalThis.window = { location: { href: url } };
    globalThis.document = { querySelector: selector => selector === '#invitesTable tbody' ? current : null };
    globalThis.DOMParser = class {
      parseFromString(body, type) {
        assert.equal(type, 'text/html');
        const incoming = inviteTable(...snapshots[body]);
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
    const olderRefresh = refreshInviteRows(attach);
    const laterRefresh = refreshInviteRows(attach);
    later.resolve(response('newest'));
    await laterRefresh;
    assert.deepEqual(current.children, [replacement]);

    older.resolve(response('older'));
    await olderRefresh;
    assert.deepEqual(current.children, [replacement]);
    assert.deepEqual(wired, [revoke]);
    assert.deepEqual(alerts, []);

    globalThis.fetch = async () => ({ ok: false });
    await refreshInviteRows(attach);
    assert.deepEqual(current.children, [replacement]);
    assert.deepEqual(wired, [revoke]);
    assert.deepEqual(alerts, [['warning', 'Invitation sent. Refresh this page to update pending invitations.']]);

    // A superseded failed read cannot replace that feedback, and a detached table cannot publish.
    const slowFailure = Promise.withResolvers();
    const failureReads = [slowFailure.promise, Promise.resolve(response('newest'))];
    globalThis.fetch = () => failureReads.shift();
    const stale = refreshInviteRows(attach);
    await refreshInviteRows(attach);
    slowFailure.resolve({ ok: false });
    await stale;
    assert.equal(alerts.length, 1);

    const previousTable = current;
    const pending = Promise.withResolvers();
    globalThis.fetch = () => pending.promise;
    const detached = refreshInviteRows(attach);
    current = inviteTable();
    pending.resolve(response('older'));
    await detached;
    assert.deepEqual(previousTable.children, [replacement]);
    assert.deepEqual(current.children, []);
    assert.equal(alerts.length, 1);

    // Acceptance is known before the read. Revoke and the other notifications overtake it.
    current = inviteTable(replacement, accepted, revokedByNotification);
    removeInviteRow(accepted.id);
    const racingRead = Promise.withResolvers();
    globalThis.fetch = () => racingRead.promise;
    wired.length = 0;
    const reconciliation = refreshInviteRows(attach);
    removeInviteRow(replacement.id);
    removeInviteRow(declined.id);
    removeInviteRow(revokedByNotification.id);
    assert.deepEqual(current.children, []);
    racingRead.resolve(response('racing-removals'));
    await reconciliation;
    assert.deepEqual(current.children, [newInvite]);
    assert.deepEqual(wired, [newInvite.revoke]);
    assert.equal(alerts.length, 1);
  } finally {
    Object.assign(globalThis, previous);
  }
});
