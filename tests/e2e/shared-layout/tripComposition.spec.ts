import { expect, test, type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** One integrated journey at the established managed owner/account/database seam. State/race matrices remain client tests. */
test('Batch 4 Layout B start copy edit return resume and Finish', async ({ page }, testInfo) => {
  test.setTimeout(180_000);
  const credentials = loadSharedLayoutConfig();
  const a = process.env.WAYFARER_E2E_TRIP_ID!;
  const evidence = process.env.WAYFARER_COMPOSITION_EVIDENCE ?? testInfo.outputPath('captures');
  await mkdir(evidence, { recursive: true });
  const signIn = async () => {
    await page.goto('/Identity/Account/Login');
    await page.getByLabel('Username').fill(credentials.username); await page.getByLabel('Password').fill(credentials.password);
    await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')), page.getByRole('button', { name: 'Log in' }).click()]);
  };
  await signIn(); await page.setViewportSize({ width: 1536, height: 1024 });
  await page.goto(`/User/Trip/Edit/${a}`);
  await expect(page.locator('.trip-editor-workspace')).toBeVisible();
  const token = await page.locator('#trip-editor-antiforgery input').inputValue();
  const post = async (url: string, data: unknown) => {
    const response = await page.request.post(url, { headers: { RequestVerificationToken: token }, data });
    expect(response.ok(), await response.text()).toBeTruthy(); return await response.json();
  };
  // Prepare a distinct owned B through ordinary production construction/editing APIs.
  const version = await post('/api/trip-compositions/versions', { sourceA: a, name: 'Coastal ideas B' });
  const b = version.tripId;
  const endpoint = `/api/trips/${b}/editor`;
  const initial = await (await page.request.get(endpoint)).json();
  const region = initial.regionOrder[0];
  const from = initial.placeOrderByRegionId[region][0];
  const to = (await post(`${endpoint}/regions/${region}/places/conditional`, {
    expected: { regionId: region, places: initial.placeOrderByRegionId[region].map((id: string) => ({ id, displayOrder: initial.placesById[id].originalValues.displayOrder })) },
    value: { name: 'Coastal viewpoint', notesHtml: '<p>Captured coastal idea</p>', location: { latitude: 38.05, longitude: 24 }, iconName: 'marker', markerColor: 'bg-blue', address: '', reverseGeocode: false }
  })).data;
  const route = (await post(`${endpoint}/segments`, { title: 'Coastal alternative', fromPlaceId: from, toPlaceId: to.id, waypointPlaceIds: [], waypointRouteVertexIndices: [],
    mode: initial.options.transportModes.find((m: { value: string }) => m.value === 'walk')?.value ?? initial.options.transportModes[0].value,
    estimatedDistanceKm: null, estimatedDurationMinutes: null, estimatedDurationSource: 'Automatic', notesHtml: '<p>Full custom route</p>', aggregateConcurrencyToken: null,
    route: { type: 'LineString', coordinates: [[23.73, 37.98], [23.77, 38.1], [24, 38.05]] } })).data;
  await post(`${endpoint}/regions/${region}/areas`, { name: 'Coastal Area with hole', notesHtml: '', fillHex: '#ecb663', geometry: {
    type: 'Polygon', coordinates: [[[23.7, 37.95], [24.02, 37.95], [24.02, 38.12], [23.7, 38.12], [23.7, 37.95]],
      [[23.8, 38], [23.8, 38.03], [23.84, 38.03], [23.84, 38], [23.8, 38]]]
  } });
  await page.goto('/User/Trip/Compose');
  await page.getByLabel('Find eligible B', { exact: true }).fill('Coastal ideas B');
  await page.getByRole('button', { name: 'Search sources / refresh unfinished work', exact: true }).click();
  await expect(page.getByLabel('Source B', { exact: true }).locator(`option[value="${b}"]`)).toHaveCount(1);
  await page.getByLabel('Source A', { exact: true }).selectOption(a);
  await page.getByLabel('Source B', { exact: true }).selectOption(b);
  await page.getByLabel('New Trip name (optional)').fill('Coastal comparison C');
  const creation = page.waitForResponse(r => r.url().endsWith('/api/trip-compositions') && r.request().method() === 'POST');
  // Observe the new page's own read; a separate GET would compete with the UI and change the observation.
  const workspaceRead = page.waitForResponse(r => r.request().method() === 'GET'
    && /^\/api\/trip-compositions\/[0-9a-f-]+$/.test(new URL(r.url()).pathname));
  await page.getByRole('button', { name: 'Start New composition', exact: true }).click();
  expect((await creation).ok()).toBeTruthy();
  await expect(page).toHaveURL(/\/User\/Trip\/Compose\/[0-9a-f-]+$/);
  // The response belongs to the destination document, so navigation cannot discard the creation body's readback.
  const composition = new URL(page.url()).pathname.split('/').at(-1)!;
  const response = await workspaceRead;
  expect(new URL(response.url()).pathname).toBe(`/api/trip-compositions/${composition}`);
  expect(response.ok(), await response.text()).toBeTruthy();
  const created = await response.json(); const c = created.tripId;
  expect(created.compositionId).toBe(composition); expect(c).not.toBe(a); expect(c).not.toBe(b);
  const inspector = page.getByRole('region', { name: 'Contextual comparison inspector' });
  const bTree = page.getByRole('region', { name: 'Source B hierarchy' });
  // The unavailable shell also has .composition-workspace; only populated content qualifies as rendered.
  const map = page.getByRole('region', { name: 'Shared A B C Trip map' });
  await expect(map.locator('.leaflet-overlay-pane path').first()).toBeVisible();
  await expect(page.getByRole('region', { name: 'Source A hierarchy' }).getByRole('button', { name: /Trip details/ })).toBeVisible();
  await expect(bTree.getByRole('heading', { name: /Coastal ideas B/ })).toBeVisible();
  await expect(inspector.getByRole('heading', { name: 'Select a source idea', exact: true })).toBeVisible();
  await expect(page.locator('.composition-feedback [role="alert"]')).toHaveCount(0);
  await bTree.getByText('Browser region', { exact: true }).click();
  await bTree.getByRole('button', { name: /Place · Coastal viewpoint/ }).click();
  const cState = await (await page.request.get(`/api/trips/${c}/editor`)).json();
  await inspector.getByLabel('Destination Region', { exact: true }).selectOption(cState.regionOrder[0]);
  let saved = page.waitForResponse(r => r.url().endsWith(`/trip-compositions/${composition}/actions`));
  await inspector.getByRole('button', { name: 'Apply reviewed action to C', exact: true }).click();
  expect((await saved).ok()).toBeTruthy(); await expect(page.locator('.composition-feedback')).toContainText('Saved to C.');
  await bTree.getByText('↝ Trip-level Segments', { exact: true }).click();
  await bTree.getByRole('button', { name: /Coastal alternative/ }).click();
  const current = await (await page.request.get(`/api/trips/${c}/editor`)).json();
  const fromC = Object.values(current.placesById).find((p: any) => p.name === 'Browser place') as any;
  const anchors = inspector.getByRole('group', { name: /From · Browser place/ });
  await anchors.getByLabel('Approved current C Place').selectOption(fromC.id);
  saved = page.waitForResponse(r => r.url().endsWith(`/trip-compositions/${composition}/actions`));
  await inspector.getByRole('button', { name: 'Apply reviewed action to C', exact: true }).click();
  expect((await saved).ok()).toBeTruthy(); await expect(page.locator('.composition-feedback')).toContainText('Saved to C.');
  // Rendered SVG bounds prove the route fits the visible map, including after keyboard resizing.
  await expectRouteInsideMap(page);
  const handle = page.getByRole('separator', { name: 'Resize comparison drawer' });
  await handle.focus(); const before = Number(await handle.getAttribute('aria-valuenow'));
  await page.keyboard.press('ArrowUp'); await expect(handle).toHaveAttribute('aria-valuenow', String(before + 3));
  await expectRouteInsideMap(page);
  await inspector.evaluate(el => { el.scrollTop = 0; }); await page.screenshot({ path: `${evidence}/trip-composition-desktop.png`, fullPage: true });
  await page.setViewportSize({ width: 390, height: 900 });
  await page.getByRole('button', { name: 'Inspector', exact: true }).click();
  await expectRouteInsideMap(page);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.screenshot({ path: `${evidence}/trip-composition-narrow.png`, fullPage: true });
  await page.setViewportSize({ width: 1536, height: 1024 });
  await page.getByRole('link', { name: 'Edit C / publish' }).click();
  const metadata = page.locator('#trip-editor-metadata-form'); await expect(metadata).toBeVisible();
  await metadata.getByLabel('Name', { exact: true }).fill('Edited current C');
  const editSave = page.waitForResponse(r => r.url().endsWith('/metadata/conditional') && r.request().method() === 'PATCH');
  await page.getByRole('button', { name: 'Save settings & Continue', exact: true }).click(); expect((await editSave).ok()).toBeTruthy();
  await page.getByRole('link', { name: 'Return to saved comparison', exact: true }).click();
  await expect(page.locator('.composition-header')).toContainText('Edited current C'); await expect(inspector).toContainText('Coastal alternative');
  await expectRouteInsideMap(page);
  await page.goto('/User/Trip');
  await page.locator('#unfinished-compositions').getByRole('link', { name: 'Continue Composition', exact: true }).click();
  await expect(page).toHaveURL(new RegExp(composition));
  const resumeRead = page.waitForResponse(r => r.url().endsWith(`/api/trip-compositions/${composition}`) && r.request().method() === 'GET');
  await page.reload();
  const resumeResponse = await resumeRead; expect(resumeResponse.ok(), await resumeResponse.text()).toBeTruthy();
  expect((await resumeResponse.json()).tripId).toBe(c);
  await expect(page.locator('.composition-header')).toContainText('Edited current C');
  const completion = page.waitForResponse(r => r.url().endsWith(`/trip-compositions/${composition}/finish`));
  await page.getByRole('button', { name: 'Finish comparison', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Finish comparison?' }); await expect(dialog).toContainText('historical comparison cannot reopen');
  await dialog.getByRole('button', { name: 'Finish comparison', exact: true }).click(); expect((await completion).ok()).toBeTruthy();
  await expect(page.getByRole('heading', { name: 'Comparison finished', exact: true })).toBeVisible();
  const finalC = await (await page.request.get(`/api/trips/${c}/editor`)).json();
  expect(finalC.metadata.name).toBe('Edited current C'); expect(Object.values(finalC.segmentsById).some((s: any) => s.title === 'Coastal alternative')).toBeTruthy();
  expect((await page.request.get(`/api/trip-compositions/${composition}`)).status()).toBe(409);
  // The managed account cleanup owns all descendants. No human/shared Trip is mutated.
  expect(route.id).toBeTruthy();
});

/** Pixel bounds of the selected route must sit inside the actual rendered map rather than behind a drawer. */
async function expectRouteInsideMap(page: Page): Promise<void> {
  const map = page.getByRole('region', { name: 'Shared A B C Trip map' });
  const selected = map.locator('path[stroke-width="7"]').first(); await expect(selected).toBeVisible();
  await expect.poll(async () => {
    const outer = await map.boundingBox(); const route = await selected.boundingBox();
    return Boolean(outer && route && route.x >= outer.x && route.y >= outer.y && route.x + route.width <= outer.x + outer.width && route.y + route.height <= outer.y + outer.height);
  }).toBeTruthy();
}
