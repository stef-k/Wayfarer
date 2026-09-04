import assert from 'node:assert/strict';
import test from 'node:test';
import { renderFeatureMetadata } from '../../wwwroot/js/util/feature-metadata.js';

test('named specific features keep the detected place without provider terminology', () => {
  const html = renderFeatureMetadata({
    resolvedFeatureName: 'Customs <office>',
    resolvedFeatureType: 'amenity'
  });

  assert.match(html, /Detected place:/);
  assert.match(html, /Customs &lt;office&gt;/);
  assert.doesNotMatch(html, /Feature type|Approximate address/);
});

test('postcode-level results show plain-language address precision', () => {
  const html = renderFeatureMetadata({ resolvedFeatureType: 'postcode' });

  assert.match(html, /Approximate address — resolved to postcode level\./);
  assert.doesNotMatch(html, /Feature type|<strong>Postcode/);
});
