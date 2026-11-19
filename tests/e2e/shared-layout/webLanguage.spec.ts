import { expect, test, type Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { mkdir, writeFile } from 'node:fs/promises';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** Adds a real Segment to the run-owned Trip using its normal Editor APIs. */
const addSegment = async (page: Page, tripId: string) => {
  await page.goto(`/User/Trip/Edit/${tripId}`);
  const token = await page.locator('#trip-editor-antiforgery input').inputValue();
  const endpoint = `/api/trips/${tripId}/editor`;
  const response = await page.request.get(endpoint);
  expect(response.ok()).toBeTruthy();
  const state = await response.json();
  const region = state.regionOrder[0];
  const from = state.placeOrderByRegionId[region][0];
  const post = async (path: string, data: unknown) => {
    const reply = await page.request.post(endpoint + path, { headers: { RequestVerificationToken: token }, data });
    expect(reply.ok(), await reply.text()).toBeTruthy();
    return (await reply.json()).data;
  };
  const to = await post(`/regions/${region}/places/conditional`, {
    expected: { regionId: region, places: [{ id: from, displayOrder: state.placesById[from].originalValues.displayOrder }] },
    value: { name: 'Panorama <north> & south', notesHtml: '', location: { latitude: 38.05, longitude: 24 },
      iconName: 'marker', markerColor: 'bg-blue', address: '', reverseGeocode: false }
  });
  return (await post('/segments', { title: 'Coastal <walk> & lookout', fromPlaceId: from, toPlaceId: to.id,
    waypointPlaceIds: [], waypointRouteVertexIndices: [], mode: state.options.transportModes[0].value,
    estimatedDistanceKm: null, estimatedDurationMinutes: 42, estimatedDurationSource: 'Manual', notesHtml: '',
    aggregateConcurrencyToken: null, route: { type: 'LineString', coordinates: [[23.73, 37.98], [24, 38.05]] }
  })).id as string;
};

/** Native keyboard input targets only a visible Chromium window owned by the Playwright worker. */
const nativeKeyboard = () => {
  const tool = process.env.WAYFARER_NATIVE_KEYBOARD_TOOL ?? 'xdotool';
  const run = (...args: string[]) => execFileSync(tool, args, { encoding: 'utf8' }).trim();
  const windows = run('search', '--onlyvisible', '--class', 'chrome|chromium').split(/\s+/);
  const window = windows.find(candidate => {
    let pid = Number(run('getwindowpid', candidate));
    for (let depth = 0; pid > 1 && depth < 20; depth++) {
      if (pid === process.pid) return true;
      const stat = readFileSync(`/proc/${pid}/stat`, 'utf8');
      pid = Number(stat.slice(stat.lastIndexOf(')') + 2).split(' ')[1]);
    }
    return false;
  });
  if (!window) throw new Error('No visible Chromium window owned by this worker');
  run('windowfocus', '--sync', window);
  return (...options: string[]) => run('key', '--window', window, '--clearmodifiers', ...options);
};

test('Area 1 Viewer to Editor language journey', async ({ page }, info) => {
  test.setTimeout(120_000);
  const credentials = loadSharedLayoutConfig();
  const folder = process.env.WAYFARER_LANGUAGE_EVIDENCE ?? info.outputPath('captures');
  await mkdir(folder, { recursive: true });
  await page.setViewportSize({ width: 1536, height: 1024 });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.goto('/Identity/Account/Login');
  await page.getByLabel('Username').fill(credentials.username);
  await page.getByLabel('Password').fill(credentials.password);
  await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')),
    page.getByRole('button', { name: 'Log in' }).click()]);

  const tripId = process.env.WAYFARER_E2E_TRIP_ID!;
  const segment = await addSegment(page, tripId);
  const viewer = `/User/Trip/View/${tripId}?lat=37.5&lon=23.1&zoom=9&seg=${segment}&ref=kept#guide`;
  await page.goto(viewer);
  const root = page.locator('#trip-view');
  await expect(root).toHaveAttribute('data-active-segment-id', segment);
  await expect(page.locator('html')).toHaveAttribute('lang', 'en-US');
  await expect(page).toHaveURL(url => url.hash === '#guide' && Number(url.searchParams.get('lat')) === 37.5);
  await page.getByRole('button', { name: 'Language', exact: true }).click();
  await page.locator('#language-dialog input[value="el-GR"]').check();
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('lang', 'el-GR');
  await expect(root).toHaveAttribute('data-active-segment-id', segment);
  await expect(page).toHaveURL(url => url.hash === '#guide' && Number(url.searchParams.get('lat')) === 37.5
    && Number(url.searchParams.get('lon')) === 23.1 && url.searchParams.get('zoom') === '9'
    && url.searchParams.get('ref') === 'kept');
  await expect(page.getByRole('button', { name: 'Απόκρυψη πλαϊνής στήλης' })).toBeVisible();
  await page.getByRole('button', { name: 'Περισσότερες ενέργειες' }).click();
  const [editor] = await Promise.all([page.context().waitForEvent('page'), page.getByRole('link', { name: 'Επεξεργασία', exact: true }).click()]);
  await expect(editor.locator('html')).toHaveAttribute('lang', 'el-GR');
  const name = editor.locator('#trip-editor-metadata-form input').first();
  await name.fill(' ');
  await editor.getByRole('button', { name: 'Αποθήκευση ρυθμίσεων και συνέχεια' }).click();
  await expect(editor.getByText(/Το όνομα (είναι υποχρεωτικό|πρέπει να έχει)/)).toBeVisible();
  await name.fill('Shared layout browser Trip');
  if (await editor.locator('body').getAttribute('data-bs-theme') !== 'dark') await editor.locator('#themeToggle').click();
  await expect(editor.locator('body')).toHaveAttribute('data-bs-theme', 'dark');
  await editor.screenshot({ path: `${folder}/greek-dark.png` });
  await editor.setViewportSize({ width: 390, height: 900 });
  expect(await editor.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await editor.screenshot({ path: `${folder}/greek-narrow.png` });

  await editor.setViewportSize({ width: 1536, height: 1024 });
  await editor.bringToFront();
  const metrics = () => editor.evaluate(() => ({ innerWidth, dpr: devicePixelRatio,
    scale: visualViewport?.scale, cssZoom: getComputedStyle(document.documentElement).zoom }));
  const key = nativeKeyboard();
  key('ctrl+0');
  const before = await metrics();
  try {
    key('--repeat', '5', '--delay', '120', 'ctrl+plus');
    await expect.poll(async () => (await metrics()).dpr).toBe(before.dpr * 2);
    const after = await metrics();
    expect(before.innerWidth / after.innerWidth).toBe(2);
    expect(after.scale).toBe(before.scale);
    expect(after.cssZoom).toBe(before.cssZoom);
    await editor.screenshot({ path: `${folder}/greek-native-200.png` });
    await writeFile(`${folder}/native-zoom.json`, JSON.stringify({ before, after }, null, 2));
  } finally {
    key('ctrl+0');
  }
});
