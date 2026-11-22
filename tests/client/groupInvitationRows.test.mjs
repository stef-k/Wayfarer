import assert from 'node:assert/strict';
import test from 'node:test';

/** Models the pending rows and their Revoke forms in a Members snapshot. */
const inviteTable = (...rows) => ({
  children: rows,
  replaceChildren(...children) { this.children = children; },
  querySelector(selector) {
    return this.querySelectorAll('tr[data-invite-id]')
      .find(row => selector === `tr[data-invite-id="${row.dataset.inviteId}"]`);
  },
  querySelectorAll(selector) {
    if (selector === '.js-invite-created') return this.children.flatMap(row => row.time ? [row.time] : []);
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
  const timeZone = process.env.TZ;
  const url = '/User/Groups/Members?groupId=the-group';
  const time = { getAttribute: () => '2026-11-22T15:30:00Z', textContent: '' };
  const replacement = { id: 'replacement-row', revoke: { id: 'revoke-new' }, time };
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
  try {
    globalThis.window = { location: { href: url } };
    process.env.TZ = 'Europe/Athens';
    globalThis.document = {
      getElementById: id => id === 'wayfarer-locale' ? { textContent: JSON.stringify({
        locale: 'el-GR', fallback: 'en-US',
        messages: {
          'Groups.DuplicateInvite': 'Ο χρήστης έχει ήδη εκκρεμή πρόσκληση για την ομάδα.',
          'Groups.InviteRefreshFailed': 'Ανανεώστε τη σελίδα για να ενημερώσετε τις εκκρεμείς προσκλήσεις.'
        },
        english: {}
      }) } : null,
      querySelector: selector => selector === '#invitesTable tbody' ? current : null
    };
    const { refreshInviteRows, removeInviteRow } = await import('../../wwwroot/js/util/group-invitation-rows.js');
    const { groupError } = await import('../../wwwroot/js/util/group-web-messages.js');
    // The Manager MVC test reaches the same Greek phrase through a real redirect.
    const rejection = await groupError(Response.json({
      message: 'A pending invitation already exists for this user in the specified group'
    }, { status: 400 }), 'invite', 'Failed to send invite.');
    assert.equal(rejection, 'Ο χρήστης έχει ήδη εκκρεμή πρόσκληση για την ομάδα.');
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
    const olderRefresh = refreshInviteRows();
    const laterRefresh = refreshInviteRows();
    later.resolve(response('newest'));
    await laterRefresh;
    assert.deepEqual(current.children, [replacement]);
    assert.equal(current.children[0].revoke.id, 'revoke-new');
    assert.equal(time.textContent, '22/11/26, 5:30 μ.μ. (GMT+2 Europe/Athens)');

    older.resolve(response('older'));
    await olderRefresh;
    assert.deepEqual(current.children, [replacement]);
    assert.deepEqual(alerts, []);

    globalThis.fetch = async () => ({ ok: false });
    await refreshInviteRows();
    assert.deepEqual(current.children, [replacement]);
    assert.deepEqual(alerts, [['warning', 'Ανανεώστε τη σελίδα για να ενημερώσετε τις εκκρεμείς προσκλήσεις.']]);

    // A superseded failed read cannot replace that feedback, and a detached table cannot publish.
    const slowFailure = Promise.withResolvers();
    const failureReads = [slowFailure.promise, Promise.resolve(response('newest'))];
    globalThis.fetch = () => failureReads.shift();
    const stale = refreshInviteRows();
    await refreshInviteRows();
    slowFailure.resolve({ ok: false });
    await stale;
    assert.equal(alerts.length, 1);

    const previousTable = current;
    const pending = Promise.withResolvers();
    globalThis.fetch = () => pending.promise;
    const detached = refreshInviteRows();
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
    const reconciliation = refreshInviteRows();
    removeInviteRow(replacement.id);
    removeInviteRow(declined.id);
    removeInviteRow(revokedByNotification.id);
    assert.deepEqual(current.children, []);
    racingRead.resolve(response('racing-removals'));
    await reconciliation;
    assert.deepEqual(current.children, [newInvite]);
    assert.equal(alerts.length, 1);
  } finally {
    Object.assign(globalThis, previous);
    if (timeZone === undefined) delete process.env.TZ;
    else process.env.TZ = timeZone;
  }
});
