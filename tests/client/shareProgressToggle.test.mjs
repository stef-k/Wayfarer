import assert from 'node:assert/strict';
import test from 'node:test';
import { initShareProgressToggle } from '../../wwwroot/js/Areas/User/Trip/shareProgressToggle.js';

// Minimal DOM boundary around the real legacy sharing action and existing alert convention.
test('rejected fetch restores sharing controls and visibly explains the ended session', async () => {
  const previous = { document: globalThis.document, fetch: globalThis.fetch, wayfarer: globalThis.wayfarer };
  const listeners = {};
  const toggle = { checked: false, dataset: { tripId: 'trip' }, addEventListener: (name, action) => { listeners[name] = action; } };
  const status = { className: '', textContent: '' };
  const alerts = [];
  globalThis.document = { getElementById: id => ({ shareProgressSwitch: toggle, shareProgressStatus: status })[id] ?? null,
    querySelector: () => ({ value: 'antiforgery' }) };
  globalThis.wayfarer = { showAlert: (type, message) => alerts.push([type, message]) };
  globalThis.fetch = async () => new Response(JSON.stringify({ message: 'Your session has ended or your account is disabled. Sign in again.' }), { status: 401 });
  try {
    initShareProgressToggle();
    toggle.checked = true;
    listeners.change();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(toggle.checked, false);
    assert.equal(status.textContent, 'Private');
    assert.equal(alerts.length, 1);
    assert.equal(alerts[0][0], 'danger');
    assert.match(alerts[0][1], /session has ended|account is disabled/i);
    assert.match(alerts[0][1], /sign in again/i);
  } finally { Object.assign(globalThis, previous); }
});
