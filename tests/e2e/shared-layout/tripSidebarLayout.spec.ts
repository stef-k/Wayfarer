import { expect, test, type Locator, type Page, type TestInfo } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** Run-owned credentials and data use the established managed host and cleanup owner. */
const signIn = async (page: Page) => {
  const config = loadSharedLayoutConfig();
  await page.goto('/Identity/Account/Login');
  await page.getByLabel('Username').fill(config.username);
  await page.getByLabel('Password').fill(config.password);
  await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')),
    page.getByRole('button', { name: 'Log in' }).click()]);
};
const tripPath = (runtime: string) => runtime === 'viewer' ? `/Public/Trips/${process.env.WAYFARER_E2E_TRIP_ID}` : `/User/Trip/Edit/${process.env.WAYFARER_E2E_TRIP_ID}`;
const width = async (locator: Locator) => (await locator.boundingBox())!.width;
const expectWidth = async (locator: Locator, expected: number) => expect.poll(() => width(locator)).toBeCloseTo(expected, 0);
const noOverflow = async (page: Page) => expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
const capture = async (page: Page, info: TestInfo, name: string) => {
  const directory = process.env.WAYFARER_SIDEBAR_EVIDENCE ?? info.outputPath('captures');
  await mkdir(directory, { recursive: true });
  await page.screenshot({ path: `${directory}/${name}.png`, fullPage: true });
};
/** Desktop/compact/phone snapshots are observations, not repeated state-machine campaigns. */
const observations = async (page: Page, info: TestInfo, runtime: string) => {
  for (const size of [1280, 1920, 901, 900, 390]) {
    await page.setViewportSize({ width: size, height: 900 });
    await noOverflow(page);
    if (runtime === 'editor' && size === 901) {
      const search = page.locator('.trip-editor-map-shell .trip-editor-map-search__form');
      expect(await width(search.getByRole('button', { name: 'Search', exact: true }))).toBeGreaterThan(200);
    }
    await capture(page, info, `after-${runtime}-${size}`);
  }
  await page.setViewportSize({ width: 1280, height: 900 });
};

/** Small visual observations at the existing host seam; draft/navigation journeys are reused. */
for (const runtime of ['viewer', 'editor']) {
  test(`Trip sidebar ${runtime} subtle grip appearance and pointer input`, async ({ page }, info) => {
    await signIn(page);
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(tripPath(runtime));
    const panel = page.locator(runtime === 'viewer' ? '#sidebar-primary' : '.trip-sidebar-dock');
    const selector = panel.getByRole('combobox', { name: 'Sidebar width' });
    const handle = page.getByRole('separator', { name: 'Resize sidebar' });
    await expectWidth(panel, 500);
    const appearances = [];
    for (const theme of ['light', 'dark']) {
      await page.evaluate(value => document.documentElement.setAttribute('data-bs-theme', value), theme);
      await selector.selectOption('standard');
      await page.mouse.move(1100, 100);
      await capture(page, info, `grip-${runtime}-${theme}`);
      const normal = await handle.evaluate(el => {
        const grip = getComputedStyle(el, '::after');
        return { color: grip.backgroundColor, opacity: grip.opacity, width: grip.width,
          height: grip.height, pointerEvents: grip.pointerEvents };
      });
      expect(await width(handle)).toBe(8);
      expect(normal.pointerEvents).toBe('none');
      await handle.hover();
      const hoverOpacity = await handle.evaluate(el => getComputedStyle(el, '::after').opacity);
      expect(Number(hoverOpacity)).toBeGreaterThan(Number(normal.opacity));
      await capture(page, info, `grip-${runtime}-${theme}-hover`);
      await page.mouse.move(1100, 100);
      await handle.focus();
      await page.keyboard.press('ArrowRight');
      await expectWidth(panel, 520);
      await expect(handle).toBeFocused();
      expect(await handle.evaluate(el => getComputedStyle(el).outlineStyle)).toBe('solid');
      await capture(page, info, `grip-${runtime}-${theme}-focus`);
      appearances.push({ theme, normal, hoverOpacity });
    }
    // Drag directly from the decorative grip, then outside the hit area under pointer capture.
    const box = (await handle.boundingBox())!;
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width / 2 + 80, box.y + box.height / 2, { steps: 8 });
    await page.mouse.up();
    await expectWidth(panel, 600);
    await expect(selector).toHaveValue('custom');
    await panel.getByRole('button', { name: 'Hide sidebar' }).click();
    await expect(handle).toBeHidden();
    await page.getByRole('button', { name: 'Show sidebar' }).click();
    await expect(handle).toBeVisible();
    await page.setViewportSize({ width: 900, height: 900 });
    await expect(handle).toBeHidden();
    await page.setViewportSize({ width: 1280, height: 900 });
    if (runtime === 'viewer') {
      await page.emulateMedia({ media: 'print' });
      await expect(handle).toBeHidden();
    }
    const directory = process.env.WAYFARER_SIDEBAR_EVIDENCE ?? info.outputPath('captures');
    await writeFile(`${directory}/grip-${runtime}-observations.json`, JSON.stringify(appearances, null, 2));
  });
}

/** One rendered Viewer journey owns parity, real capture geometry, reversible map offsets and containment. */
test('Trip sidebar Viewer geometry and map continuity', async ({ page }, info) => {
  await signIn(page);
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(tripPath('viewer'));
  const root = page.locator('#trip-view');
  const legend = page.locator('#sidebar-primary');
  const details = page.locator('#sidebar-secondary');
  const selector = legend.getByRole('combobox', { name: 'Sidebar width' });
  const handle = page.getByRole('separator', { name: 'Resize sidebar' });
  const map = page.locator('#mapContainer');
  await expect(selector).toBeVisible();
  await expectWidth(legend, 500);
  await expect(page.locator('.leaflet-tile-loaded').first()).toBeVisible();
  await observations(page, info, 'viewer');
  await selector.selectOption('half');
  await expectWidth(legend, await width(root) / 2);
  await page.setViewportSize({ width: 1920, height: 900 });
  await expectWidth(legend, await width(root) / 2);
  await page.setViewportSize({ width: 1280, height: 900 });
  await expectWidth(legend, await width(root) / 2);
  const box = (await handle.boundingBox())!;
  await page.mouse.move(box.x + 4, box.y + 120);
  await page.mouse.down();
  await page.mouse.move(box.x + 145, box.y + 120, { steps: 8 });
  await page.mouse.up();
  await expect(selector).toHaveValue('custom');
  await expect(handle).toHaveAttribute('aria-valuenow', '781');
  await handle.focus();
  await page.keyboard.press('ArrowRight');
  await expectWidth(legend, 801);
  await expect(handle).toBeFocused();
  expect(await handle.evaluate(el => getComputedStyle(el).outlineStyle)).toBe('solid');
  await page.evaluate(() => { (window as any).sidebarMapNode = document.querySelector('#mapContainer'); });
  await page.locator('.place-list-item').first().click();
  await expect(details).toHaveClass(/open/);
  await expectWidth(details, 801);
  await expect(page.getByRole('button', { name: 'Show sidebar', exact: true })).toBeHidden();
  await details.getByRole('button', { name: 'Back to legend' }).click();
  await handle.focus();
  for (let step = 0; step < 3; step++) await page.keyboard.press('Shift+ArrowRight');
  await expectWidth(legend, 1101);
  await page.setViewportSize({ width: 901, height: 900 });
  await expectWidth(legend, await width(root));
  await page.setViewportSize({ width: 1280, height: 900 });
  await expectWidth(legend, 1101);
  await page.reload();
  await expectWidth(legend, 1101);
  await selector.selectOption('standard');
  const center = () => { const url = new URL(page.url()); return ['lat', 'lon', 'zoom'].map(key => url.searchParams.get(key)); };
  // Odd-pixel steps must be reversible too: Leaflet rounds individual panBy vectors.
  const before = center();
  for (let cycle = 0; cycle < 3; cycle++) {
    await handle.focus(); await page.keyboard.press('ArrowRight'); await page.keyboard.press('ArrowLeft');
    await legend.getByRole('button', { name: 'Hide sidebar' }).click();
    await page.getByRole('button', { name: 'Show sidebar' }).click();
  }
  expect(center()).toEqual(before);
  await selector.selectOption('full');
  await expectWidth(legend, await width(root));
  await expectWidth(map, await width(root));
  await legend.getByRole('button', { name: 'Hide sidebar' }).click();
  await expect(page.getByRole('button', { name: 'Show sidebar' })).toBeFocused();
  await page.getByRole('button', { name: 'Show sidebar' }).click();
  await expect(selector).toHaveValue('full');
  await selector.selectOption('half');
  const beforeReload = center();
  await page.reload();
  await expect(selector).toHaveValue('half');
  expect(center()).toEqual(beforeReload);
  await expectWidth(legend, await width(root) / 2);
  await noOverflow(page);
  await page.evaluate(() => {
    document.documentElement.setAttribute('data-bs-theme', 'dark');
    document.querySelector('#sidebar-primary h5')?.append(' — A long localized Trip title for a narrow desktop workspace');
  });
  await page.setViewportSize({ width: 640, height: 450 });
  await noOverflow(page);
  await capture(page, info, 'after-viewer-dark-compact');
  await page.setViewportSize({ width: 1280, height: 900 });
  // Stored Full must not expand embeds, and both controls and panels remain absent in print.
  await selector.selectOption('full');
  await page.goto(`${tripPath('viewer')}?embed=true`);
  await expect(legend).toHaveAttribute('data-collapsed', 'true');
  await expect(page.getByRole('button', { name: 'Show sidebar' })).toBeVisible();
  await page.getByRole('button', { name: 'Show sidebar' }).click();
  await expectWidth(legend, await width(root));
  await page.goto(`${tripPath('viewer')}?print=1`);
  await expect(legend).toBeHidden();
  await expect(handle).toBeHidden();
  await expect(page.getByRole('button', { name: 'Show sidebar' })).toBeHidden();
  await capture(page, info, 'after-viewer-print');
});

/** One real draft journey checks persistent owners, selection, map geometry and an explicit protected save. */
test('Trip sidebar Editor retains Notes and selection through layout actions', async ({ page }, info) => {
  await signIn(page);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(tripPath('editor'));
  const layout = page.locator('.trip-sidebar-layout');
  const dock = page.locator('.trip-sidebar-dock');
  const selector = layout.getByRole('combobox', { name: 'Sidebar width' });
  const handle = layout.getByRole('separator', { name: 'Resize sidebar' });
  const map = page.locator('.trip-editor-map');
  await expectWidth(dock, 500);
  await expect(page.locator('.leaflet-tile-loaded').first()).toBeVisible();
  await observations(page, info, 'editor');
  const row = page.locator('[data-place-id]').first();
  const placeId = await row.getAttribute('data-place-id');
  await row.getByRole('button', { name: 'Edit', exact: true }).click();
  const form = page.locator('#trip-editor-place-form');
  const notes = form.getByRole('textbox', { name: 'Notes', exact: true });
  await notes.fill('Sidebar draft retains selected Notes');
  await notes.evaluate(el => {
    (window as any).sidebarNotesEditor = (el as any).editor;
    (el as any).editor.commands.setTextSelection({ from: 2, to: 8 });
    (window as any).sidebarMapNode = document.querySelector('.trip-editor-map');
  });
  const selection = await notes.evaluate(el => (el as any).editor.state.selection.toJSON());
  const view = () => map.evaluate(el => [el.getAttribute('data-trip-editor-map-lat'), el.getAttribute('data-trip-editor-map-lng'), el.getAttribute('data-trip-editor-map-zoom')]);
  const beforeView = await view();
  let writes = 0;
  page.on('request', request => { if (/\/api\/trips\/.*\/editor/.test(request.url()) && ['POST', 'PUT', 'PATCH', 'DELETE'].includes(request.method())) writes++; });
  await selector.selectOption('half');
  await expectWidth(dock, await width(layout) / 2);
  const box = (await handle.boundingBox())!;
  await page.mouse.move(box.x + 4, box.y + 100); await page.mouse.down();
  await page.mouse.move(box.x + 150, box.y + 100, { steps: 8 }); await page.mouse.up();
  await expect(selector).toHaveValue('custom');
  const custom = await width(dock);
  await selector.selectOption('full');
  await expectWidth(dock, await width(layout));
  await expectWidth(map, await width(layout));
  await layout.getByRole('button', { name: 'Hide sidebar' }).click();
  await expectWidth(map, await width(layout));
  await page.getByRole('button', { name: 'Show sidebar' }).click();
  await expect(selector).toHaveValue('full');
  await handle.focus(); await page.keyboard.press('Home');
  await expectWidth(dock, 420);
  await capture(page, info, 'after-editor-minimum-form');
  await expect(notes).toHaveText('Sidebar draft retains selected Notes');
  expect(await notes.evaluate(el => (el as any).editor === (window as any).sidebarNotesEditor)).toBeTruthy();
  expect(await notes.evaluate(el => (el as any).editor.state.selection.toJSON())).toEqual(selection);
  expect(await page.evaluate(() => document.querySelector('.trip-editor-map') === (window as any).sidebarMapNode)).toBeTruthy();
  expect(await view()).toEqual(beforeView);
  expect(writes).toBe(0);
  await expect(page.getByRole('dialog')).toBeHidden();
  await page.getByRole('button', { name: 'Expand Editor', exact: true }).click();
  await expect(selector).toBeHidden();
  await page.getByRole('dialog').getByRole('button', { name: 'Dock to sidebar', exact: true }).click();
  await expectWidth(dock, 420);
  await selector.selectOption('standard');
  const save = page.waitForResponse(response => response.url().endsWith(`/places/${placeId}/conditional`) && response.request().method() === 'PATCH');
  await page.getByRole('button', { name: 'Save Place', exact: true }).click();
  expect((await save).ok()).toBeTruthy();
  const state = await (await page.request.get(`/api/trips/${process.env.WAYFARER_E2E_TRIP_ID}/editor`)).json();
  expect(state.placesById[placeId!].notesHtml).toContain('Sidebar draft retains selected Notes');
  expect(custom).toBeGreaterThan(700);
  await noOverflow(page);
});

/** Retains F1's real Leaflet route and keyboard trigger, through both production entry paths. */
test('Trip sidebar Viewer near Full route navigation stays fitted and reversible', async ({ page }, info) => {
  await signIn(page);
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.setViewportSize({ width: 1280, height: 900 });
  // Observe the retained production map exactly as the independent F1 probe did.
  await page.addInitScript(() => {
    let value: any;
    Object.defineProperty(window, 'L', { configurable: true, get: () => value, set: next => {
      value = next;
      next.Map.addInitHook(function (this: any) { (window as any).sidebarNavigationMap = this; });
    } });
  });
  // The managed sample Trip has one Place. Add only transient route markup before
  // Viewer initialization so the real renderer and row listener own this route too.
  await page.route(`**${tripPath('viewer')}*`, async route => {
    const response = await route.fetch();
    const html = await response.text();
    await route.fulfill({ response, body: html.replace('</aside>', `<ul><li class="segment-list-item"
      data-segment-id="review-route" data-route-wkt="LINESTRING (23.70 37.95, 23.76 38.01)"
      data-route-orientation="forward"><span>Review route</span>
      <button class="segment-selection-button" type="button">Select review route</button></li></ul></aside>`) });
  });
  await page.goto(tripPath('viewer'));
  const legend = page.locator('#sidebar-primary');
  const handle = page.getByRole('separator', { name: 'Resize sidebar' });
  await expect(handle).toBeVisible();
  await handle.focus();
  await page.keyboard.press('End');
  await page.keyboard.press('ArrowLeft');
  await expectWidth(legend, 1260);
  const observe = () => page.evaluate(async () => {
    const map = (window as any).sidebarNavigationMap;
    const helpers = await import('/js/Trip/tripViewerHelpers.js');
    const bounds = helpers.getSegmentPolyline('review-route').getBounds();
    const size = map.getSize();
    const centerPixel = map.project(map.getCenter(), map.getZoom());
    const northwest = map.latLngToContainerPoint(bounds.getNorthWest());
    const southeast = map.latLngToContainerPoint(bounds.getSouthEast());
    return { zoom: map.getZoom(), center: [map.getCenter().lat, map.getCenter().lng],
      centerPixel: [centerPixel.x, centerPixel.y], canvas: [size.x, size.y], containsRoute: map.getBounds().contains(bounds),
      // Overlay fallback fits the retained canvas, with its normal 60px margins.
      insideMargins: northwest.x >= 59 && northwest.y >= 59 && southeast.x <= size.x - 59 && southeast.y <= size.y - 59 };
  });
  const observations: unknown[] = [];
  const directory = process.env.WAYFARER_SIDEBAR_EVIDENCE ?? info.outputPath('captures');
  await mkdir(directory, { recursive: true });
  // Keep every observation even when a later assertion fails.
  const record = async (path: string) => {
    const observation = await observe();
    observations.push({ path, ...observation });
    await writeFile(`${directory}/correction-route.json`, JSON.stringify(observations, null, 2));
    return observation;
  };
  // Button and route hits use the same public selection owner as the retained probe.
  await legend.getByRole('button', { name: 'Select review route' }).click();
  const selection = await record('selection-button');
  expect(selection.containsRoute).toBeTruthy();
  expect(selection.insideMargins).toBeTruthy();
  expect(selection.zoom).toBeLessThan(19);
  expect(selection.center.every(Number.isFinite)).toBeTruthy();
  await legend.locator('.segment-list-item span').click();
  const row = await record('row');
  expect(row).toEqual(selection);
  // A route hit invokes this same owner; hiding reveals the complete sensible fit.
  await page.evaluate(() => (window as any).wayfarer.selectSegment('review-route'));
  let stable = selection;
  for (let cycle = 0; cycle < 3; cycle++) {
    await legend.getByRole('button', { name: 'Hide sidebar' }).click();
    const hidden = await record(`hidden-${cycle}`);
    if (cycle === 0) {
      // Leaflet invalidation clears its cached lat/lon and exposes the rounded pixel
      // origin. Bound that first quantization, then require exact stability thereafter.
      expect(Math.hypot(...hidden.centerPixel.map((value, axis) => value - selection.centerPixel[axis]))).toBeLessThan(1);
      stable = hidden;
    }
    expect(hidden).toEqual(stable);
    if (cycle === 0) await capture(page, info, 'correction-viewer-route-hidden');
    await page.getByRole('button', { name: 'Show sidebar' }).click();
    await expectWidth(legend, 1260);
    expect(await record(`shown-${cycle}`)).toEqual(stable);
  }
  // Recenter and Place details share the overlay destination; Leaflet rounds to pixels.
  await page.locator('#btn-trip-center').click();
  const centered = await page.evaluate(() => {
    const map = (window as any).sidebarNavigationMap;
    const root = document.querySelector('#trip-view') as HTMLElement;
    const target = (window as any).L.latLng(+root.dataset.tripLat!, +root.dataset.tripLon!);
    return map.project(map.getCenter(), map.getZoom()).distanceTo(map.project(target, map.getZoom()));
  });
  expect(centered).toBeLessThan(1);
  await legend.locator('.place-list-item').first().click();
  const placeCentered = await page.evaluate(() => {
    const map = (window as any).sidebarNavigationMap;
    const place = document.querySelector('.place-list-item') as HTMLElement;
    const target = (window as any).L.latLng(+place.dataset.placeLat!, +place.dataset.placeLon!);
    return map.project(map.getCenter(), map.getZoom()).distanceTo(map.project(target, map.getZoom()));
  });
  expect(placeCentered).toBeLessThan(1);
});
