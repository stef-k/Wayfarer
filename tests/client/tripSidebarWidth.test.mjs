import assert from 'node:assert/strict';
import test from 'node:test';
import { bindSidebarResize, readSidebarPreference, sidebarWidth, sidebarKeyWidth, writeSidebarPreference } from '../../wwwroot/js/Trip/sidebarWidth.js';
import { viewerNavigation } from '../../wwwroot/js/Trip/viewerSidebar.js';

// Real shared production semantics cover both runtime namespaces and transient clamps.
test('presets follow the live workspace while Custom preserves intended width across reload and clamps', () => {
  const values = new Map();
  const storage = () => ({ getItem: key => values.get(key) ?? null, setItem: (key, value) => values.set(key, value) });
  for (const workspace of [700, 1280, 1920]) {
    assert.equal(sidebarWidth({ mode: 'standard' }, workspace), 500);
    assert.equal(sidebarWidth({ mode: 'half' }, workspace), Math.max(420, workspace / 2));
    assert.equal(sidebarWidth({ mode: 'full' }, workspace), workspace);
  }
  writeSidebarPreference('viewer', { mode: 'custom', width: 850 }, storage);
  writeSidebarPreference('editor', { mode: 'full', width: 500 }, storage);
  const custom = readSidebarPreference('viewer', storage);
  assert.equal(sidebarWidth(custom, 700), 700);
  assert.equal(sidebarWidth(custom, 1920), 850);
  assert.equal(readSidebarPreference('editor', storage).mode, 'full');
  assert.equal(sidebarWidth(custom, 300), 300);
  assert.deepEqual(readSidebarPreference('missing', storage), { mode: 'standard', width: 500 });
});

test('malformed or unavailable storage falls back safely and does not throw on writes', () => {
  for (const value of ['null', '{}', 'garbage', '{"mode":"custom","width":"850"}', '{"mode":"custom","width":-1}', '{"mode":"strange"}']) {
    assert.deepEqual(readSidebarPreference('viewer', () => ({ getItem: () => value })), { mode: 'standard', width: 500 });
  }
  const unavailable = () => { throw new Error('denied'); };
  assert.deepEqual(readSidebarPreference('viewer', unavailable), { mode: 'standard', width: 500 });
  assert.doesNotThrow(() => writeSidebarPreference('viewer', { mode: 'full', width: 500 }, unavailable));
  assert.equal(sidebarWidth({ mode: 'full' }, 1280), 1280);
});

// A native EventTarget drives the production capture owner; no duplicated resize implementation.
test('pointer completion persists once; cancellation, hide and lost capture restore the original intent', () => {
  const previousWindow = globalThis.window;
  globalThis.window = new EventTarget();
  const handle = Object.assign(new EventTarget(), { captured: null, focus() {},
    setPointerCapture(id) { this.captured = id; }, hasPointerCapture(id) { return this.captured === id; },
    releasePointerCapture() { this.captured = null; } });
  let preference = { mode: 'half', width: 500 };
  let commits = 0;
  let active = false;
  const owner = bindSidebarResize(handle, { getPreference: () => preference, getWidth: () => sidebarWidth(preference, 1280),
    getMax: () => 1280, change: value => { preference = value; }, commit: () => commits++, active: value => { active = value; } });
  const send = (type, properties = {}) => handle.dispatchEvent(Object.assign(new Event(type, { cancelable: true }),
    { pointerId: 1, button: 0, clientX: 640, ...properties }));
  try {
    send('pointerdown'); send('pointermove', { clientX: 780 }); send('pointerup');
    assert.deepEqual(preference, { mode: 'custom', width: 780 });
    assert.equal(commits, 1); assert.equal(active, false); assert.equal(handle.captured, null);
    for (const cancellation of ['pointercancel', 'lostpointercapture', 'hide', 'blur', 'Escape']) {
      preference = { mode: 'full', width: 500 };
      send('pointerdown'); send('pointermove', { clientX: 300 });
      assert.equal(preference.mode, 'custom');
      if (cancellation === 'hide') owner.cancel();
      else if (cancellation === 'blur') window.dispatchEvent(new Event('blur'));
      else if (cancellation === 'Escape') send('keydown', { key: 'Escape' });
      else send(cancellation);
      assert.deepEqual(preference, { mode: 'full', width: 500 }, cancellation);
      assert.equal(active, false); assert.equal(handle.captured, null);
      send('pointerup');
      assert.equal(commits, 1, 'late pointer release must not commit a cancelled attempt');
    }
    send('keydown', { key: 'Home' });
    assert.deepEqual(preference, { mode: 'custom', width: 420 });
    send('keydown', { key: 'ArrowRight', shiftKey: true });
    assert.equal(preference.width, 520);
    send('keydown', { key: 'End' });
    assert.equal(preference.width, 1280);
    assert.equal(sidebarKeyWidth({ key: 'Tab' }, 500, 1280), null);
  } finally { owner.dispose(); globalThis.window = previousWindow; }
});

/** Actual fitting space, rather than option shape alone, owns the navigation boundary. */
test('Viewer navigation retains usable fit space for presets, near Full and small canvases', () => {
  for (const [size, sidebar, offset] of [
    [{ x: 1280, y: 800 }, 500, 250], // Standard
    [{ x: 1280, y: 800 }, 640, 320], // Half
    [{ x: 1280, y: 800 }, 1040, 520], // Last split with 120px of fit space
    [{ x: 1280, y: 800 }, 1041, 0], // Overlay when the exposed strip cannot support margins
    [{ x: 1280, y: 800 }, 1260, 0], // End, ArrowLeft
    [{ x: 1280, y: 800 }, 1280, 0], // Full
    [{ x: 640, y: 100 }, 500, 0], // Compact and short
    [{ x: 100, y: 80 }, 0, 0], // Small embed/print canvas
    [{ x: 901, y: 60 }, 500, 250]
  ]) {
    const navigation = viewerNavigation(size, sidebar);
    const { paddingTopLeft: start, paddingBottomRight: end } = navigation.fitOptions;
    assert.equal(navigation.offsetX, offset);
    assert.equal((start[0] - end[0]) / 2, offset, 'centering and fitting share the layout offset');
    assert.ok(size.x - start[0] - end[0] >= Math.min(120, size.x / 2), 'positive usable width');
    assert.ok(size.y - start[1] - end[1] >= size.y / 2, 'at least half the height remains usable');
  }
});

/** Preference changes and responsive fallback use one reversible integer offset. */
test('Viewer navigation offsets reverse across split, overlay, hidden and breakpoint changes', () => {
  let previous = 0;
  let total = 0;
  for (const [width, sidebar] of [[1280, 501], [1280, 1260], [1280, 0], [1280, 1260],
    [1280, 501], [640, 500], [390, 0], [1280, 501], [1280, 0]]) {
    const next = viewerNavigation({ x: width, y: 600 }, sidebar).offsetX;
    total += next - previous;
    previous = next;
  }
  assert.equal(total, 0);
});
