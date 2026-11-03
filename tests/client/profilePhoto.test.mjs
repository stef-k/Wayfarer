import { test } from 'node:test';
import assert from 'node:assert/strict';
import { PhotoIntent, photoCrop } from '../../wwwroot/js/profilePhotoEditor.js';

test('conflict/unknown outcomes require explicit state refresh and a separate deliberate intent', () => {
  for (const code of ['photo_changed', 'photo_outcome_unknown']) {
    const intent = new PhotoIntent();
    intent.adopt({ stateToken: 'S0', profilePhoto: null, showProfilePhotoOnPublicTrips: true });
    intent.failed(code);
    assert.equal(intent.canSubmit, false);
    assert.equal(intent.state.stateToken, 'S0');
    intent.adopt({ stateToken: 'S2', profilePhoto: null, showProfilePhotoOnPublicTrips: true });
    assert.equal(intent.canSubmit, true);
    assert.equal(intent.state.showProfilePhotoOnPublicTrips, true);
  }
});

test('definite processing failure preserves the current reference and owner intent', () => {
  const intent = new PhotoIntent();
  const winner = { stateToken: 'S1', profilePhoto: { version: 'winner' } };
  intent.adopt(winner); intent.failed('invalid_photo');
  assert.equal(intent.state, winner); assert.equal(intent.canSubmit, true);
});

test('centered and adjusted crops use integer oriented source pixels without enlargement', () => {
  assert.deepEqual(photoCrop(31, 20), { cropX: 5, cropY: 0, cropSize: 20 });
  assert.deepEqual(photoCrop(20, 31, 2, 100, 0), { cropX: 10, cropY: 0, cropSize: 10 });
});

// The same badge owner supplies User/Manager maps, rosters and selected details.
const { photoInitial } = await import('../../wwwroot/js/profilePhotos.js');
test('fallback uses a Unicode letter and never invents an emoji/punctuation initial', () => {
  assert.equal(photoInitial(' \u200eΆννα', 'anna'), 'Ά');
  assert.equal(photoInitial(' 123…', ' Stefan'), 'S');
  assert.equal(photoInitial('🎈 …', '123'), '');
});
