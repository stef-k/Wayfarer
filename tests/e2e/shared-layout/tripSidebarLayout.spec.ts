import { expect, test, type Locator, type Page, type TestInfo } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
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
    await capture(page, info, `after-${runtime}-${size}`);
  }
  await page.setViewportSize({ width: 1280, height: 900 });
};

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
