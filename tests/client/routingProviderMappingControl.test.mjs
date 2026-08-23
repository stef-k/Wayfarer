import test from 'node:test';
import assert from 'node:assert/strict';
import { mappingControlState } from '../../wwwroot/js/admin/routing-provider-mappings.js';

test('Geoapify adapter immediately closes mappings and preserves only allowed values', () => {
  assert.deepEqual(mappingControlState('2', 'driving'), { kind: 'select', value: '' });
  assert.deepEqual(mappingControlState('2', 'walk'), { kind: 'select', value: 'walk' });
  assert.deepEqual(mappingControlState('1', 'walk'), { kind: 'input', value: 'walk' });
  assert.deepEqual(mappingControlState('2', 'mapbox-driving'), { kind: 'select', value: '' });
});
