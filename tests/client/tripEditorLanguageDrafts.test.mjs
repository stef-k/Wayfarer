import assert from 'node:assert/strict';
import test from 'node:test';
import { mountDraft } from './tripEditorExitHost.mjs';
import { hasLanguageDraft, navigateInLanguage } from '../../wwwroot/js/localization/languageNavigation.js';

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
