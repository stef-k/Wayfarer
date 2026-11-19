import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
import { createMessages } from '../../wwwroot/js/localization/messages.js';
import { isLanguageUnload, languageDestination, navigateInLanguage } from '../../wwwroot/js/localization/languageNavigation.js';
import { resolveViewerCamera, writeViewerCamera } from '../../wwwroot/js/Trip/viewerCamera.js';
import { pdfProgressText } from '../../wwwroot/js/Trip/viewerExports.js';
import { populateViewerPrint } from '../../wwwroot/js/Trip/viewerPrint.js';

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
