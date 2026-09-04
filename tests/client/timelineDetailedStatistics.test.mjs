import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const timelineScripts = [
  'wwwroot/js/Areas/User/Timeline/Index.js',
  'wwwroot/js/Areas/User/Timeline/Chronological.js'
];

test('both Timeline views use the Wayfarer alert for detailed-statistics failures', async () => {
  for (const path of timelineScripts) {
    const source = await readFile(path, 'utf8');
    assert.match(source,
      /wayfarer\.showAlert\('danger', 'Failed to load detailed statistics\. Please try again\.'\)/);
    assert.doesNotMatch(source, /alert\('Failed to load detailed statistics/);
    assert.doesNotMatch(source, /Error fetching detailed stats/);
    assert.doesNotMatch(source, /Error \$\{response\.status\}: \$\{await response\.text\(\)\}/);
  }
});
