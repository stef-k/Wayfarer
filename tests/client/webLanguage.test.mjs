import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
import { createMessages } from '../../wwwroot/js/localization/messages.js';
import { holdLanguageNavigation, isLanguageUnload, languageDestination, navigateInLanguage } from '../../wwwroot/js/localization/languageNavigation.js';
import { resolveViewerCamera, writeViewerCamera } from '../../wwwroot/js/Trip/viewerCamera.js';
import { pdfProgressText } from '../../wwwroot/js/Trip/viewerExports.js';
import { populateViewerPrint } from '../../wwwroot/js/Trip/viewerPrint.js';
import { visitScanSummary } from '../../wwwroot/js/Areas/User/Trip/visitScanText.js';
import { bindHiddenAreaDraft } from '../../wwwroot/js/Areas/User/HiddenAreas/drawing.js';

test('registered test catalog uses Polish categories and English grammar for missing messages', async () => {
  const source = await readFile(new URL('../Wayfarer.Tests/Resources/Shell.pl-PL.resx', import.meta.url), 'utf8');
  const messages = Object.fromEntries(Array.from(source.matchAll(/<data name="([^"]+)"[^>]*>\s*<value>([^<]+)<\/value>/g),
    ([, key, value]) => [key, value]));
  const { WebLocales: [choice] } = JSON.parse(await readFile(new URL('../Wayfarer.Tests/Resources/WebLocaleProbe.json', import.meta.url)));
  const { message, plural } = createMessages({ locale: choice.Tag, fallback: 'en-US', messages, english: {} });
  assert.equal(message('Shell.Language', 'Language'), 'Język');
  assert.equal(plural('Shell.InvitationsPending', 3, 'You have {0} invitation.', 'You have {0} invitations.'), 'Masz 3 oczekujące zaproszenia.');
  assert.equal(plural('Shell.InvitationsPending', 12, 'You have {0} invitation.', 'You have {0} invitations.'), 'Masz 12 oczekujących zaproszeń.');
  assert.equal(plural('Shell.Missing', 0, '{0} thing', '{0} things'), '0 things');
  assert.equal(plural('Shell.Missing', 1, '{0} thing', '{0} things'), '1 thing');
  assert.equal(plural('Shell.Missing', 3, '{0} thing', '{0} things'), '3 things');
});

test('Trip visit scan uses Greek cardinal forms for pending actions', async () => {
  const source = await readFile(new URL('../../Resources/Editor.el-GR.resx', import.meta.url), 'utf8');
  const messages = Object.fromEntries(Array.from(source.matchAll(/<data name="(Editor\.TripsScan[^"]+)"[^>]*>\s*<value>([^<]+)<\/value>/g),
    ([, key, value]) => [key, value]));
  const { plural } = createMessages({ locale: 'el-GR', fallback: 'en-US', messages, english: {} });
  const firstRow = confirmed => visitScanSummary({ confirmed, suggested: 0, stale: 0, existing: 0 }, plural)[0];
  assert.deepEqual(firstRow(1), ['summary-create', 1, 'Θα δημιουργηθεί 1 επιβεβαιωμένη επίσκεψη']);
  assert.deepEqual(firstRow(2), ['summary-create', 2, 'Θα δημιουργηθούν 2 επιβεβαιωμένες επισκέψεις']);
});

test('safe destination preserves query/fragment and permits only the one shared unload event', () => {
  const current = 'https://wayfarer.test/User/Trip/View/one?lat=37.5&seg=s&progress=1#guide';
  assert.equal(languageDestination(current, ''), current);
  assert.equal(languageDestination(current, '/User/Timeline/Settings'), 'https://wayfarer.test/User/Timeline/Settings');
  assert.equal(languageDestination(current, '//another.test/'), null);

  assert.equal(isLanguageUnload({}), false);
  const event = {};
  let calls = 0;
  navigateInLanguage(current, target => {
    calls++;
    assert.equal(target, current);
    // Metadata and Planning consult the same browser event.
    assert.equal(isLanguageUnload(event), true);
    assert.equal(isLanguageUnload(event), true);
  });
  assert.equal(calls, 1);
  assert.equal(isLanguageUnload({}), false);
});

test('complete camera qualifies for retention and a partial or invalid camera preserves per-field fallback', () => {
  const defaults = [20, 3, 6];
  assert.deepEqual(resolveViewerCamera('?lat=37.1&lon=23.2&zoom=9&seg=chosen', defaults), { view: [37.1, 23.2, 9], complete: true });
  assert.deepEqual(resolveViewerCamera('?lat=37.1&zoom=100&seg=chosen', defaults), { view: [37.1, 3, 6], complete: false });
  assert.deepEqual(resolveViewerCamera('?lat=no&lon=23.2&zoom=', defaults), { view: [20, 23.2, 6], complete: false });
  const state = { current: 'existing' };
  const entry = { state, replaceState(s, title, url) {
    assert.equal(s, state);
    assert.equal(title, '');
    assert.equal(url.href, 'https://wayfarer.test/Trip?seg=a&progress=1&other=xyz&lat=37.500000&lon=23.100000&zoom=9#guide');
  } };
  writeViewerCamera('https://wayfarer.test/Trip?seg=a&progress=1&other=xyz#guide', entry, { lat: 37.5, lng: 23.1 }, 9);
});

test('progress text uses structured state and treats the authored argument as text', () => {
  assert.equal(pdfProgressText({ presentation: 'Viewer.PdfRegion', arguments: [2, 5, '<A> & B'] }), 'Region 2/5: <A> & B');
  assert.equal(pdfProgressText({ message: '<img>', presentation: 'invented' }), 'Export is still running…');
});

test('browser print assigns authored title and active language through DOM text properties', () => {
  const attributes = {};
  const title = '<b>Trip</b> & notes';
  const body = { append: (...children) => { attributes.children = children; } };
  const target = { documentElement: {}, body, createElement: tag => ({ tag }) };
  const clone = { childNodes: [{ textContent: 'Already rendered' }], querySelectorAll: () => [] };
  populateViewerPrint(target, { ownerDocument: { documentElement: { lang: 'el-GR' } }, cloneNode: () => clone }, title, 'https://wayfarer.test');
  assert.equal(target.documentElement.lang, 'el-GR');
  assert.equal(target.title, title);
  assert.deepEqual(attributes.children, [{ tag: 'h5', textContent: title }, ...clone.childNodes]);
});

/** Exercises the same selector for an ordinary GET and an admitted POST at its own safe GET URL. */
const verifyLanguageForm = async (safeGet, editPolygon = false) => {
  const original = Object.fromEntries(['document', 'fetch', 'FormData', 'HTMLSelectElement', 'location', 'window'].map(name => [name, globalThis[name]]));
  const handlers = new Map();
  let focused;
  const control = id => ({
    hidden: false, dataset: {},
    addEventListener: (event, callback) => handlers.set(`${id}:${event}`, callback),
    setAttribute() {}, focus: () => { focused = id; }
  });
  const ids = ['language-dialog', 'language-form', 'language-open', 'language-cancel', 'language-keep',
    'language-apply', 'language-confirm', 'language-options', 'language-discard', 'language-error'];
  const controls = Object.fromEntries(ids.map(id => [id, control(id)]));
  const form = controls['language-form'];
  const dialog = controls['language-dialog'];
  const input = { form: {}, type: editPolygon ? 'hidden' : 'text',
    value: editPolygon ? 'POLYGON((23 37, 24 37, 24 38, 23 37))' : 'saved title' };
  const originalValue = input.value;
  const mapEvents = new Map();
  const events = { EDITSTART: 'draw:editstart', EDITED: 'draw:edited', EDITSTOP: 'draw:editstop' };
  const map = { on(name, handle) { mapEvents.set(name, handle); return this; } };
  const release = editPolygon ? bindHiddenAreaDraft(map, events, input, {}) : () => {};
  const submits = [];
  const navigations = [];
  const current = editPolygon
    ? 'https://wayfarer.test/User/HiddenAreas/Edit/7#hidden-area-map'
    : 'https://wayfarer.test/Account?returnUrl=%2Felsewhere';
  let result = 500;
  form.dataset.url = '/Language/Set';
  form.reset = () => {};
  dialog.dataset.safeGet = safeGet;
  dialog.showModal = () => { dialog.open = true; };
  dialog.close = () => { dialog.open = false; handlers.get('language-dialog:close')(); };
  globalThis.document = { getElementById: id => controls[id], querySelectorAll: () => [input], addEventListener() {} };
  globalThis.location = {
    href: current,
    reload: () => navigations.push(['reload', current]),
    assign: target => navigations.push(['assign', target])
  };
  globalThis.window = Object.assign(new EventTarget(), { location: globalThis.location });
  globalThis.HTMLSelectElement = class {};
  globalThis.FormData = class { get(name) { assert.equal(name, 'locale'); return 'el-GR'; } };
  globalThis.fetch = async (url, options) => {
    submits.push({ url, method: options.method, busy: controls['language-apply'].disabled });
    return { status: result };
  };
  try {
    await import(`../../wwwroot/js/localization/languageSelector.js?form=${editPolygon ? 'polygon' : safeGet ? 'post' : 'get'}`);
    const open = () => handlers.get('language-open:click')();
    const submit = () => handlers.get('language-form:submit')({ preventDefault() {} });
    if (!safeGet && !editPolygon) {
      // A feature hold takes precedence even over an already-open preference dialog.
      open();
      const releaseHold = holdLanguageNavigation();
      try {
        assert.equal(controls['language-open'].hidden, true);
        assert.equal(dialog.open, false);
        open();
        await submit();
        assert.deepEqual(submits, []);
      } finally {
        releaseHold();
      }
      assert.equal(controls['language-open'].hidden, false);
    }
    if (editPolygon) {
      // During a drag the feature group owns the draft and the hidden field still holds the saved geometry.
      mapEvents.get(events.EDITSTART)();
      open();
      await submit();
      assert.equal(controls['language-confirm'].hidden, false);
      handlers.get('language-keep:click')();
      assert.equal(input.value, originalValue);
      assert.deepEqual(submits, []);
      const ring = [{ lng: 23.1234567890123, lat: 37.6543210987654 }, { lng: 24, lat: 37 }, { lng: 24, lat: 38 }];
      mapEvents.get(events.EDITED)({ layers: { eachLayer: edit => edit({ getLatLngs: () => [ring] }) } });
      mapEvents.get(events.EDITSTOP)();
    } else {
      input.value = 'draft <text>';
    }
    const draft = editPolygon
      ? 'POLYGON((23.1234567890123 37.6543210987654, 24 37, 24 38, 23.1234567890123 37.6543210987654))'
      : 'draft <text>';
    assert.equal(input.value, draft);
    open();
    await submit();
    assert.equal(controls['language-confirm'].hidden, false);
    assert.equal(focused, 'language-keep');
    handlers.get('language-keep:click')();
    assert.equal(focused, 'language-open');
    assert.deepEqual(submits, []);
    assert.equal(input.value, draft);
    open();
    await submit();
    await submit();
    assert.equal(dialog.open, true);
    assert.deepEqual(navigations, []);
    assert.equal(controls['language-error'].hidden, false);
    assert.equal(input.value, draft);
    result = 204;
    await submit();
    assert.deepEqual(submits, Array(2).fill({ url: '/Language/Set', method: 'POST', busy: true }));
    assert.deepEqual(navigations, [[safeGet ? 'assign' : 'reload', current]]);
    assert.equal(isLanguageUnload({}), true);
    assert.equal(isLanguageUnload({}), false);
  } finally {
    release();
    for (const [key, value] of Object.entries(original)) {
      if (value === undefined) Reflect.deleteProperty(globalThis, key);
      else globalThis[key] = value;
    }
  }
};

test('changed ordinary form guards preference writes and keeps the current document on cancel or failure',
  () => verifyLanguageForm(''));

test('POST-rendered form uses an assigned GET even when its URL is already current',
  () => verifyLanguageForm('/Account?returnUrl=%2Felsewhere'));


test('polygon-only edits survive keeping the drawing and a failed preference write',
  () => verifyLanguageForm('', true));
