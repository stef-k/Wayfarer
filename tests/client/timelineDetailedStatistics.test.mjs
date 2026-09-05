import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';

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

// Execute the production renderer in isolation from map/network bootstrapping.
const render = async (path, stats) => {
  const source = await readFile(path, 'utf8');
  const start = source.indexOf('const generateStatsModalContent =');
  const end = source.indexOf('\n};', start) + 3;
  return vm.runInNewContext(`${source.slice(start, end)}; generateStatsModalContent(stats, 'countries')`, {
    stats, viewerTimeZone: 'UTC', formatDate: () => 'date',
    formatDateDisplay: () => 'period', currentDate: new Date(), currentViewType: 'day'
  });
};

test('both production renderers place missing parents once and encode geographic labels', async () => {
  const detail = { visitCount: 1, coordinates: { latitude: 40, longitude: 25 } };
  const stats = {
    totalLocations: 4,
    countries: [{ ...detail, name: '<country>' }],
    regions: [
      { ...detail, name: '<region>', countryName: '<country>' },
      { ...detail, name: 'orphan-region', countryName: '' }
    ],
    cities: [
      { ...detail, name: '<img src=x onerror="boom">', countryName: '<country>', regionName: '<region>' },
      { ...detail, name: 'country-only-city', countryName: '<country>', regionName: '' },
      { ...detail, name: 'region-only-city', countryName: '', regionName: 'orphan-region' },
      { ...detail, name: 'parentless-city', countryName: '', regionName: '' }
    ]
  };
  const original = JSON.stringify(stats);
  for (const path of timelineScripts) {
    const html = await render(path, stats);
    assert.ok(html.includes('Country not recorded'), path);
    assert.equal(html.split('Region not recorded').length - 1, 2, path);
    for (const label of ['&lt;country&gt;', '&lt;region&gt;', 'orphan-region',
      '&lt;img src=x onerror=&quot;boom&quot;&gt;', 'country-only-city', 'region-only-city', 'parentless-city']) {
      assert.equal(html.split(label).length - 1, 1, `${path}: ${label}`);
    }
    assert.ok(!html.includes('<img'), path);
    assert.ok(html.includes('Countries (1)'), path);
    assert.equal((html.match(/title="View on map"/g) ?? []).length, 7, path);
    assert.ok(html.includes('?lat=40.000000&lng=25.000000&zoom=13'), path);
    const orphanOnly = await render(path, { ...stats, countries: [], regions: [], cities: [stats.cities[3]] });
    assert.ok(orphanOnly.includes('parentless-city'), path);
    assert.ok(orphanOnly.includes('Countries (0)'), path);
  }
  assert.equal(JSON.stringify(stats), original);
});
