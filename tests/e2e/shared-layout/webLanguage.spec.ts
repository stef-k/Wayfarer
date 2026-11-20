import { expect, test, type Locator, type Page } from '@playwright/test';
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

/** Checks a rendered dark control against the closest painted surface and keeps the measured ratio. */
const textContrast = async (control: Locator): Promise<number> => {
  await expect(control).toBeVisible();
  return control.evaluate(element => {
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = 1;
    const context = canvas.getContext('2d')!;
    const rgba = (color: string) => {
      context.clearRect(0, 0, 1, 1);
      context.fillStyle = color;
      context.fillRect(0, 0, 1, 1);
      return [...context.getImageData(0, 0, 1, 1).data];
    };
    const luminance = (rgb: number[]) => rgb.slice(0, 3).reduce((total, channel, index) => {
      const value = channel / 255;
      return total + [0.2126, 0.7152, 0.0722][index] * (value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4);
    }, 0);
    const foreground = rgba(getComputedStyle(element).color);
    const layers = [];
    for (let node: Element | null = element; node; node = node.parentElement) {
      const paint = rgba(getComputedStyle(node).backgroundColor);
      layers.push(paint);
      if (paint[3] === 255) break;
    }
    const background = layers.reverse().reduce((under, paint) =>
      under.map((value, index) => value * (1 - paint[3] / 255) + paint[index] * paint[3] / 255), [255, 255, 255]);
    const ink = luminance(foreground), surface = luminance(background);
    return (Math.max(ink, surface) + 0.05) / (Math.min(ink, surface) + 0.05);
  });
};

/** Captures the actual Greek controls through responsive reflow and native browser zoom. */
const captureDarkLayouts = async (editor: Page, folder: string) => {
  if (await editor.locator('body').getAttribute('data-bs-theme') !== 'dark') await editor.locator('#themeToggle').click();
  await expect(editor.locator('body')).toHaveAttribute('data-bs-theme', 'dark');
  const language = editor.locator('#language-open');
  const toolbarAction = editor.locator('.trip-editor-toolbar__actions button:enabled').first();
  const verify = async (control: Locator) => {
    await expect.poll(() => textContrast(control)).toBeGreaterThanOrEqual(4.5);
    return textContrast(control);
  };
  await editor.locator('#themeToggle').blur();
  const desktop = { language: await verify(language), toolbar: await verify(toolbarAction) };
  await editor.screenshot({ path: `${folder}/greek-dark.png` });

  await editor.setViewportSize({ width: 390, height: 900 });
  expect(await editor.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  const toggler = editor.locator('#mainNavbar .navbar-toggler');
  const navbar = editor.locator('#mainNavbarCollapse');
  await toggler.click();
  await expect(navbar).toHaveClass(/\bshow\b/);
  await language.scrollIntoViewIfNeeded();
  await expect(language).toBeInViewport({ ratio: 1 });
  const narrow = { language: await verify(language) };
  await editor.screenshot({ path: `${folder}/greek-narrow.png` });
  await toggler.click();
  await expect(navbar).toBeHidden();

  await editor.setViewportSize({ width: 1536, height: 1024 });
  await editor.bringToFront();
  const metrics = () => editor.evaluate(() => ({ innerWidth, dpr: devicePixelRatio,
    scale: visualViewport?.scale, cssZoom: getComputedStyle(document.documentElement).zoom }));
  const key = nativeKeyboard();
  key('ctrl+0');
  const before = await metrics();
  try {
    // Chromium can coalesce a burst of native shortcuts while reflowing the previous step.
    for (let step = 0; step < 5; step++) {
      const previous = (await metrics()).dpr;
      key('ctrl+plus');
      await expect.poll(async () => (await metrics()).dpr).toBeGreaterThan(previous);
    }
    const after = await metrics();
    expect(after.dpr).toBe(before.dpr * 2);
    expect(before.innerWidth / after.innerWidth).toBe(2);
    expect(after.scale).toBe(before.scale);
    expect(after.cssZoom).toBe(before.cssZoom);
    expect(await editor.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
    const checkPlan = editor.locator('.plan-check > .trip-editor-panel__line button').first();
    await editor.evaluate(() => window.scrollTo(0, 0));
    await expect(checkPlan).toBeInViewport({ ratio: 1 });
    const native = { checkPlan: await verify(checkPlan) };
    await editor.screenshot({ path: `${folder}/greek-native-200.png` });
    await writeFile(`${folder}/native-zoom.json`, JSON.stringify({ before, after }, null, 2));
    await writeFile(`${folder}/dark-contrast.json`, JSON.stringify({ desktop, narrow, native }, null, 2));
  } finally {
    key('ctrl+0');
  }
};

test('Area 1 Viewer to Editor language journey', async ({ page }, info) => {
  test.setTimeout(120_000);
  page.setDefaultTimeout(15_000);
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
  editor.setDefaultTimeout(15_000);
  const name = editor.locator('#trip-editor-metadata-form input').first();
  await name.fill(' ');
  await editor.getByRole('button', { name: 'Αποθήκευση ρυθμίσεων και συνέχεια' }).click();
  await expect(editor.getByText(/Το όνομα (είναι υποχρεωτικό|πρέπει να έχει)/)).toBeVisible();
  await name.fill('Shared layout browser Trip');
  await captureDarkLayouts(editor, folder);
});

/** Waits for the actual Place details to mount before checking the camera retained by the Viewer. */
test('Area 1 delayed Place focus retains the linked camera and Segment', async ({ page }) => {
  const { username, password } = loadSharedLayoutConfig();
  await page.setViewportSize({ width: 1536, height: 1024 });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.goto('/Identity/Account/Login');
  await page.getByLabel('Username').fill(username);
  await page.getByLabel('Password').fill(password);
  await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')),
    page.getByRole('button', { name: 'Log in' }).click()]);

  const tripId = process.env.WAYFARER_E2E_TRIP_ID!;
  const segment = await addSegment(page, tripId);
  const viewer = `/User/Trip/View/${tripId}`;
  await page.goto(viewer);
  const place = await page.locator('.place-list-item').first().getAttribute('data-place-id');
  expect(place).toBeTruthy();

  const details = page.locator('#sidebar-secondary [data-details-content]');
  await page.goto(`${viewer}?lat=37.5&lon=23.1&zoom=9&seg=${segment}&placeId=${place}#guide`);
  await expect(details.locator('.btn-back')).toBeVisible();
  await expect(page.locator('.selected-marker')).toHaveCount(1);
  await expect(page.locator('#trip-view')).toHaveAttribute('data-active-segment-id', segment);
  await expect(page).toHaveURL(url => Number(url.searchParams.get('lat')) === 37.5
    && Number(url.searchParams.get('lon')) === 23.1 && url.searchParams.get('zoom') === '9'
    && url.searchParams.get('placeId') === place && url.hash === '#guide', { timeout: 2000 });

  await details.locator('.btn-back').click();
  await page.locator(`.place-list-item[data-place-id="${place}"]`).click();
  await expect(details.locator('.btn-back')).toBeVisible();
  await expect(page).toHaveURL(url => Number(url.searchParams.get('zoom')) >= 12);

  await page.goto(`${viewer}?placeId=${place}`);
  await expect(details.locator('.btn-back')).toBeVisible();
  await expect(page).toHaveURL(url => Number(url.searchParams.get('zoom')) >= 12);
});
