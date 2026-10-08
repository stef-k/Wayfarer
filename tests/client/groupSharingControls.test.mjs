import assert from 'node:assert/strict';
import test from 'node:test';
import { bindGroupSharingControls, refreshGroupState } from '../../wwwroot/js/groupSharingControls.js';

/** Exercises actual control bindings at the DOM/request seam without a map fixture. */
const controls = () => {
  const elements = new Map();
  for (const id of ['orgPeerVisibilityToggle', 'peerVisibilityToggle', 'groupSharingError', 'currentUserId']) {
    elements.set(id, { checked: true, hidden: true, value: 'user/id', addEventListener: (_name, handler) => { elements.get(id).change = handler; } });
  }
  return { elements, getElementById: id => elements.get(id), querySelector: () => ({ value: 'page-token' }) };
};

test('group and personal switches preserve anti-forgery and published disabled semantics', async () => {
  const root = controls();
  const requests = [];
  let reloads = 0;
  bindGroupSharingControls('group', root, async (url, options) => {
    requests.push({ url, options });
    return { ok: true, json: async () => JSON.parse(options.body) };
  }, () => reloads++);
  await root.elements.get('orgPeerVisibilityToggle').change();
  await root.elements.get('peerVisibilityToggle').change();
  assert.equal(requests[0].url, '/api/groups/group/settings/org-peer-visibility');
  assert.deepEqual(JSON.parse(requests[0].options.body), { enabled: true });
  assert.equal(requests[1].url, '/api/groups/group/members/user%2Fid/org-peer-visibility-access');
  assert.deepEqual(JSON.parse(requests[1].options.body), { disabled: false });
  for (const request of requests) assert.equal(request.options.headers.RequestVerificationToken, 'page-token');
  assert.equal(reloads, 2);
});

test('rejected policy mutation restores the control and exposes retry feedback', async () => {
  const root = controls();
  let reloads = 0;
  bindGroupSharingControls('group', root, async () => ({ ok: false }), () => reloads++);
  const toggle = root.elements.get('orgPeerVisibilityToggle');
  await toggle.change();
  assert.equal(toggle.checked, false);
  assert.equal(toggle.disabled, false);
  assert.equal(root.elements.get('groupSharingError').hidden, false);
  assert.equal(reloads, 0);
});

test('policy and membership invalidation refresh all state while location events keep the live map', () => {
  let reloads = 0;
  const reload = () => reloads++;
  assert.equal(refreshGroupState({ type: 'visibility-changed' }, reload), true);
  assert.equal(refreshGroupState({ type: 'member-removed', userId: 'peer' }, reload), true);
  assert.equal(refreshGroupState({ type: 'location' }, reload), false);
  assert.equal(refreshGroupState({ type: 'location-deleted' }, reload), false);
  assert.equal(reloads, 2);
});
