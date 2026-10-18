import { expect, test } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** One real persistence journey covers #847 selection and the merged #848 preservation boundary. */
test('Unassigned Segment pickers persist endpoints and waypoints after Region preservation', async ({ page }, testInfo) => {
  const config = loadSharedLayoutConfig();
  const tripId = process.env.WAYFARER_E2E_TRIP_ID!;
  const endpoint = `/api/trips/${tripId}/editor`;
  await page.goto('/Identity/Account/Login');
  await page.getByLabel('Username').fill(config.username);
  await page.getByLabel('Password').fill(config.password);
  await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')), page.getByRole('button', { name: 'Log in' }).click()]);
  await page.goto(`/User/Trip/Edit/${tripId}`);
  await expect(page.locator('.trip-editor-workspace')).toBeVisible();
  const token = await page.locator('#trip-editor-antiforgery input').inputValue();
  const headers = { RequestVerificationToken: token };
  const state = async () => (await page.request.get(endpoint)).json();
  const post = async (path: string, data: unknown) => {
    const response = await page.request.post(endpoint + path, { headers, data });
    expect(response.ok(), await response.text()).toBeTruthy();
    return (await response.json()).data;
  };
  const source = await post('/regions', { name: 'Preserved route Places', notesHtml: null, center: null, coverImage: null });
  const createPlace = (name: string, longitude: number) => post(`/regions/${source.id}/places`, {
    name, notesHtml: null, address: '', location: { longitude, latitude: 38 }, iconName: 'marker', markerColor: 'bg-blue', reverseGeocode: false
  });
  const from = await createPlace('Unassigned From', 23);
  const via = await createPlace('Unassigned Via', 23.1);
  const to = await createPlace('Unassigned To', 23.2);
  const alternate = await createPlace('Unassigned Alternate', 23.15);
  const mode = (await state()).options.transportModes[0].value;
  const route = { type: 'LineString', coordinates: [[23,38],[23.05,38.01],[23.1,38],[23.2,38]] };
  const existing = await post('/segments', { fromPlaceId: from.id, toPlaceId: to.id, waypointPlaceIds: [via.id],
    waypointRouteVertexIndices: [2], mode, route, notesHtml: '<p>Preserved custom route</p>', estimatedDistanceKm: null,
    estimatedDurationMinutes: 42, estimatedDurationSource: 'Manual', aggregateConcurrencyToken: null });
  const before = (await state()).segmentsById[existing.id];

  // Execute the shipped Keep/Keep workflow, then compare the full Segment projection after reload.
  await page.reload();
  const card = page.locator(`[data-region-id="${source.id}"]`);
  await card.locator('.trip-editor-region-card__header').getByRole('button', { name: 'Edit', exact: true }).click();
  await card.getByRole('button', { name: 'Delete', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Delete Region ‘Preserved route Places’?' });
  await dialog.getByRole('button', { name: 'Delete Region · keep Places and Areas', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await page.reload();
  const moved = await state();
  const canonical = Object.values(moved.regionsById).find((region: any) => region.isShadow) as any;
  for (const place of [from, via, to, alternate]) expect(moved.placesById[place.id].regionId).toBe(canonical.id);
  // Data Protection issues randomized token ciphertext on each read; compare all actual Segment fields.
  expect({ ...moved.segmentsById[existing.id], aggregateConcurrencyToken: null }).toEqual({ ...before, aggregateConcurrencyToken: null });
  const form = page.locator('#trip-editor-segment-form');
  const open = async (id: string) => {
    await page.locator(`[data-segment-id="${id}"] .trip-editor-list-button`).click();
    await expect(form).toBeVisible();
  };
  await open(existing.id);
  await expect(form.getByLabel('From place')).toHaveValue(from.id);
  await expect(form.getByLabel('To place')).toHaveValue(to.id);
  await expect(form.getByLabel(/Intermediate place 1:/)).toHaveValue(via.id);
  await expect(form.getByLabel(/Intermediate place 1:/).locator(':checked')).toBeEnabled();
  await expect(form.getByLabel('From place').locator('optgroup[label="Unassigned Places"]')).toHaveCount(1);
  await expect(form.getByLabel('To place').locator('optgroup[label="Unassigned Places"]')).toHaveCount(1);
  await page.screenshot({ path: testInfo.outputPath('unassigned-segment-pickers.png') });

  // An ordinary metadata save retains custom geometry, anchor indices and manual measurements.
  await form.locator('.ql-editor').fill('Edited preserved route');
  const savedExisting = page.waitForResponse(r => r.request().method() === 'PUT' && r.url().endsWith(`/segments/${existing.id}`));
  await page.getByRole('button', { name: 'Save Segment', exact: true }).click();
  expect((await savedExisting).ok()).toBeTruthy();
  await page.reload();
  const edited = (await state()).segmentsById[existing.id];
  expect(edited).toMatchObject({ fromPlaceId: from.id, toPlaceId: to.id, waypointPlaceIds: [via.id],
    waypointRouteVertexIndices: [2], route, estimatedDurationSource: 'Manual', estimatedDurationMinutes: 42 });
  expect(edited.estimatedDistanceKm).toBe(before.estimatedDistanceKm);

  // Create a fallback Segment through all three live pickers, including replacement and reselection.
  await page.getByRole('button', { name: 'Add Segment', exact: true }).click();
  await form.getByLabel('From place').selectOption(from.id);
  await form.getByLabel('To place').selectOption(to.id);
  await form.getByLabel('Transport mode').selectOption(mode);
  await form.getByLabel('Place to add', { exact: true }).selectOption(via.id);
  await form.getByRole('button', { name: 'Add intermediate place', exact: true }).click();
  const waypoint = form.getByLabel(/Intermediate place 1:/);
  await waypoint.selectOption(alternate.id);
  await waypoint.selectOption(via.id);
  for (const id of [from.id, to.id, via.id]) await expect(form.getByLabel('Place to add', { exact: true }).locator(`option[value="${id}"]`)).toHaveCount(0);
  const createdResponse = page.waitForResponse(r => r.request().method() === 'POST' && r.url().endsWith('/segments'));
  await page.getByRole('button', { name: 'Save Segment', exact: true }).click();
  const response = await createdResponse;
  expect(response.ok(), await response.text()).toBeTruthy();
  const created = (await response.json()).data;
  await page.reload();
  await open(created.id);
  await expect(form.getByLabel('From place')).toHaveValue(from.id);
  await expect(form.getByLabel('To place')).toHaveValue(to.id);
  await expect(waypoint).toHaveValue(via.id);
  const persisted = (await state()).segmentsById[created.id];
  expect(persisted).toMatchObject({ fromPlaceId: from.id, toPlaceId: to.id, waypointPlaceIds: [via.id], waypointRouteVertexIndices: [null], route: null });
  expect(persisted.effectiveRoute.coordinates).toEqual([[23,38],[23.1,38],[23.2,38]]);

  // Picker eligibility grants no protected-Region edit/reorder/delete or direct Area creation authority.
  const protectedCard = page.locator(`[data-region-id="${canonical.id}"]`);
  expect(canonical.capabilities).toMatchObject({ canEdit: false, canDelete: false, canReorder: false, canAddChildren: false });
  await expect(protectedCard.locator('.trip-editor-region-card__header').getByRole('button', { name: 'Edit', exact: true })).toHaveCount(0);
  await expect(protectedCard.getByRole('button', { name: 'Add Area', exact: true })).toHaveCount(0);
  const denied = await page.request.post(`${endpoint}/regions/${canonical.id}/areas`, { headers,
    data: { name: 'Forbidden Area', notesHtml: null, fillHex: '#123456', geometry: { type: 'Polygon', coordinates: [[[23,38],[24,38],[24,39],[23,38]]] } } });
  expect(denied.status()).toBe(403);
  expect((await state()).areasById).toEqual(moved.areasById);

  // Deliberately incomplete catalogue response: missing references and locationless Places remain disabled,
  // with saved draft IDs visible. This negative is client-only; the positive persistence assertions above use real HTTP.
  await page.route(`**${endpoint}`, async route => {
    const response = await route.fetch();
    const incomplete = await response.json();
    delete incomplete.placesById[from.id];
    delete incomplete.placesById[via.id];
    incomplete.placesById[to.id].location = null;
    await route.fulfill({ response, json: incomplete });
  });
  await page.reload();
  await open(created.id);
  for (const [selector, id] of [[form.getByLabel('From place'), from.id], [waypoint, via.id], [form.getByLabel('To place'), to.id]] as const) {
    await expect(selector).toHaveValue(id);
    await expect(selector.locator(':checked')).toBeDisabled();
  }
  await expect(waypoint.locator(':checked')).toHaveText('Place no longer available');
  await expect(form.getByLabel('To place').locator(':checked')).toContainText('location required');
});
