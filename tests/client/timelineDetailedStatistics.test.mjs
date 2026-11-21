import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';
import { renderPrivateStatistics } from '../../wwwroot/js/Areas/User/Timeline/calendar-display.js';
import { renderStatistics } from '../../wwwroot/js/Areas/User/Timeline/statistics.js';

const timelineScripts = [
  'wwwroot/js/Areas/User/Timeline/Index.js',
  'wwwroot/js/Areas/User/Timeline/calendar-display.js'
];

test('the shared public Timeline requests statistics for the resolved username on mount and live updates', async () => {
  const source = await readFile('wwwroot/js/Areas/Public/UsersTimeline/Timeline.js', 'utf8');
  const refresh = source.match(/const refreshLiveTimeline =[\s\S]*?\n};/)?.[0];
  assert.ok(refresh, 'live SSE handler exists');
  assert.match(source, /stream\.onmessage = refreshLiveTimeline/);
  assert.match(source.slice(0, source.indexOf(refresh)), /getUserStats\(\);/);
  assert.match(refresh, /getUserStats\(\);/);
  assert.match(source, /GetPublicStats\/\$\{encodeURIComponent\(username\)\}/);
});

test('all-history Timeline uses the Wayfarer alert for detailed-statistics failures', async () => {
  for (const path of timelineScripts.slice(0, 1)) {
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
  if (path.endsWith('calendar-display.js')) return renderPrivateStatistics({ statistics: stats, endpoints: [] }, 'period', 'capture');
  const source = await readFile(path, 'utf8');
  const start = source.indexOf('const generateStatsModalContent =');
  const end = source.indexOf('\n};', start) + 3;
  return vm.runInNewContext(`${source.slice(start, end)}; generateStatsModalContent(stats, 'countries')`, {
    stats, renderStatistics, viewerTimeZone: 'UTC', formatDate: () => 'date',
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
    const recordedCountry = html.slice(html.indexOf('id="country-heading-0"'), html.indexOf('id="country-heading-1"'));
    const missingCountry = html.slice(html.indexOf('id="country-heading-1"'));
    assert.ok(recordedCountry.includes('country-only-city') && !recordedCountry.includes('parentless-city'), path);
    assert.ok(missingCountry.includes('orphan-region') && missingCountry.includes('region-only-city'), path);
    const missingRegion = missingCountry.slice(missingCountry.indexOf('Region not recorded'));
    assert.ok(missingRegion.includes('parentless-city') && !missingRegion.includes('region-only-city'), path);
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

// The private renderer resolves clocks by geographic endpoint identity, never an averaged representative coordinate.
test('private geographic endpoints retain different capture clocks at the same event instant', () => {
  const clock = local => ({ status: 'available', local, zoneId: 'qualified', offsetSeconds: 0 });
  const stats = { totalLocations: 2, countries: ['A', 'B'].map(name => ({ name, visitCount: 1, firstVisit: 'same', lastVisit: 'same' })), regions: [], cities: [] };
  const response = { statistics: stats, endpoints: ['A', 'B'].map((country, index) => ({ kind: 'country', country, region: '', place: '', first: true, last: true,
    eventUtc: 'same', captureTime: clock(index ? '2026-05-18T01:00' : '2026-05-17T23:00'), viewerTime: clock('2026-05-18T00:00') })) };
  const html = renderPrivateStatistics(response, 'selected period', 'capture');
  assert.match(html, /2026-05-17 23:00/); assert.match(html, /2026-05-18 01:00/);
});
