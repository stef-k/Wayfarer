import assert from 'node:assert/strict';
import test from 'node:test';
import { mountDraft } from './tripEditorExitHost.mjs';
import { hasLanguageDraft, isLanguageNavigationHeld, navigateInLanguage } from '../../wwwroot/js/localization/languageNavigation.js';

test('Metadata and Planning both retain drafts and guard unload until the authorized language navigation', () => {
  const previous = globalThis.window;
  const guards = [];
  globalThis.window = {
    addEventListener(type, callback) { if (type === 'beforeunload') guards.push(callback); },
    removeEventListener() {}
  };
  const owner = mountDraft(true);
  try {
    owner.editor.draft.name = 'Unsaved <Trip>';
    const planning = owner.planning.open({ kind: 'metadata', mode: 'edit' });
    planning.entries.push({ label: 'Private Plan edit', when: { form: 'undated' } });
    assert.equal(guards.length, 2);
    assert.equal(hasLanguageDraft(), true);

    const unload = () => {
      let blocked = 0;
      const event = { preventDefault() { blocked++; }, returnValue: undefined };
      guards.forEach(guard => guard(event));
      return blocked;
    };

    assert.equal(unload(), 2);
    navigateInLanguage('/User/Trip/Edit/trip?lat=38&lng=23&zoom=9', () => assert.equal(unload(), 0));
    assert.equal(unload(), 2);
    assert.equal(owner.editor.draft.name, 'Unsaved <Trip>');
    assert.equal(planning.entries[0].label, 'Private Plan edit');
  } finally {
    owner.dispose();
    globalThis.window = previous;
  }
  assert.equal(hasLanguageDraft(), false);
});


/** A confirmed refusal releases the save hold, while losing a reply keeps it until unmount. */
test('the mounted Planning owner distinguishes edits from pending and unconfirmed saves', async t => {
  const previous = globalThis.window;
  globalThis.window = new EventTarget();
  const owner = mountDraft(true);
  try {
    const draft = owner.planning.open({ kind: 'metadata', mode: 'edit' });
    draft.entries.push({ label: 'Retained Planning edit', when: { form: 'undated' } });
    assert.equal(hasLanguageDraft(), true);
    assert.equal(isLanguageNavigationHeld(), false);

    const refusal = Promise.withResolvers();
    const lostReply = Promise.withResolvers();
    const fetch = t.mock.method(globalThis, 'fetch', () => refusal.promise);
    const first = owner.planning.save(draft);
    assert.equal(isLanguageNavigationHeld(), true);
    refusal.resolve(Response.json({ code: 'mutation-base-stale', status: 409 }, { status: 409 }));
    await first;
    assert.equal(isLanguageNavigationHeld(), false);
    assert.equal(hasLanguageDraft(), true);

    fetch.mock.mockImplementation(() => lostReply.promise);
    const second = owner.planning.save(draft);
    assert.equal(isLanguageNavigationHeld(), true);
    lostReply.reject(new TypeError('Lost save reply'));
    await second;
    assert.equal(isLanguageNavigationHeld(), true);
    assert.match(draft.error, /earlier change may still finish/);
    assert.equal(draft.entries[0].label, 'Retained Planning edit');
  } finally {
    owner.dispose();
    globalThis.window = previous;
  }
  assert.equal(isLanguageNavigationHeld(), false);
});
