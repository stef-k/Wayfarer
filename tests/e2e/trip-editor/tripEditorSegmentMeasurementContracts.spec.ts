import { expect, test, type Locator, type Page } from '@playwright/test';
import {
  absoluteUrl,
  editorApiPath,
  editorPath,
  expectMountedWorkspace,
  loadEditorStateFixture,
  regionCard,
  signIn,
  uniqueName
} from './tripEditorTestUtils';

type EditorState = Record<string, any>;

// Exercises #405 against a disposable persisted zero-waypoint aggregate through the real editor and mutation endpoints.
test('persisted zero-waypoint Segment keeps server-authoritative Automatic and Manual measurements', async ({ page }) => {
  test.setTimeout(90_000);
  const runIdentity = uniqueName('issue-405-measurement');
  const regionName = `${runIdentity} region`;
  const fromName = `${runIdentity} origin`;
  const toName = `${runIdentity} destination`;
  let regionId = '';
  let fromId = '';
  let toId = '';
  let segmentId = '';

  await signIn(page);
  const editorResponse = await page.goto(absoluteUrl(editorPath), { waitUntil: 'domcontentloaded' });
  expect(editorResponse?.ok(), `GET ${editorPath} must load the disposable Trip`).toBeTruthy();
  await expectMountedWorkspace(page);

  try {
    regionId = await createRegion(page, regionName);
    fromId = await createPlace(page, regionName, fromName, '0', '0');
    toId = await createPlace(page, regionName, toName, '0', '0.1');
    let current = await state(page);

    const created = await mutate(page, 'post', `${editorApiPath}/segments`, automaticRequest(fromId, toId, 'walk', 999, 999));
    segmentId = created.data.id;
    current = await state(page);
    let segment = current.segmentsById[segmentId];
    expect(segment.estimatedDistanceKm).toBeCloseTo(11.119, 3);
    expect(segment.estimatedDurationSource).toBe('Automatic');
    expect(segment.estimatedDurationMinutes).toBeCloseTo(8006 / 60, 8);

    await page.reload({ waitUntil: 'domcontentloaded' });
    await expectMountedWorkspace(page);
    await openSegment(page, segmentId);
    const form = page.locator('#trip-editor-segment-form');
    const distance = form.getByLabel('Estimated distance km');
    await expect(distance).toHaveAttribute('readonly', '');
    await expect(distance).toHaveAttribute('aria-readonly', 'true');
    await expect(form.getByLabel('Use automatic estimate')).toBeChecked();
    await expect(form.getByLabel('Enter manually')).not.toBeChecked();
    const automaticDuration = form.getByLabel('Estimated duration minutes');
    await expect(automaticDuration).toBeVisible();
    await expect(automaticDuration).toBeDisabled();
    await expect(automaticDuration).toHaveAttribute('readonly', '');
    await expect(automaticDuration).toHaveValue(String(8006 / 60));
    await form.getByLabel('Enter manually').check();
    await expect(form.getByLabel('Estimated duration minutes')).toBeEnabled();
    await form.getByLabel('Use automatic estimate').check();
    await expect(form.getByLabel('Estimated duration minutes')).toBeDisabled();

    const missingSource = { ...automaticRequest(fromId, toId, 'walk', 777, 777) } as Record<string, unknown>;
    delete missingSource.estimatedDurationSource;
    const stale = await rawMutation(page, 'put', `${editorApiPath}/segments/${segmentId}`, missingSource);
    expect(stale.status()).toBe(400);
    expect(await stale.text()).toMatch(/reload/i);

    await form.getByLabel('Enter manually').check();
    const manualInput = form.getByLabel('Estimated duration minutes');
    await expect(manualInput).toBeEnabled();
    await manualInput.fill('1.51');
    const manualSave = page.waitForResponse(response =>
      response.request().method() === 'PUT' && response.url().endsWith(`/segments/${segmentId}`));
    await page.getByRole('button', { name: 'Save Segment' }).click();
    expect((await manualSave).ok()).toBeTruthy();
    await expectSaved(page);
    segment = (await state(page)).segmentsById[segmentId];
    expect(segment.estimatedDurationSource).toBe('Manual');
    expect(segment.estimatedDurationMinutes).toBeCloseTo(91 / 60, 10);

    await page.reload({ waitUntil: 'domcontentloaded' });
    await expectMountedWorkspace(page);
    await openSegment(page, segmentId);
    await expect(page.locator('#trip-editor-segment-form').getByLabel('Enter manually')).toBeChecked();
    await expect(page.locator('#trip-editor-segment-form').getByLabel('Estimated duration minutes')).toHaveValue(String(91 / 60));
    await page.locator('#trip-editor-segment-form').getByLabel('Use automatic estimate').check();
    await expect(page.locator('#trip-editor-segment-form').getByLabel('Estimated duration minutes')).toBeDisabled();
    await page.locator('#trip-editor-segment-form').getByLabel('Enter manually').check();
    await expect(page.locator('#trip-editor-segment-form').getByLabel('Estimated duration minutes')).toBeEnabled();

    await mutate(page, 'put', `${editorApiPath}/segments/${segmentId}`, manualRequest(toId, fromId, 'bicycle', 12345, 91 / 60));
    segment = (await state(page)).segmentsById[segmentId];
    expect(segment.estimatedDurationSource).toBe('Manual');
    expect(segment.estimatedDurationMinutes).toBeCloseTo(91 / 60, 10);
    expect(segment.estimatedDistanceKm).toBeCloseTo(11.119, 3);

    await mutate(page, 'put', `${editorApiPath}/segments/${segmentId}`, automaticRequest(toId, fromId, 'bicycle', 54321, 54321));
    segment = (await state(page)).segmentsById[segmentId];
    expect(segment.estimatedDurationSource).toBe('Automatic');
    expect(segment.estimatedDurationMinutes).toBeCloseTo(2669 / 60, 8);
    expect(segment.estimatedDistanceKm).toBeCloseTo(11.119, 3);

    const manualOnly = await rawMutation(page, 'put', `${editorApiPath}/segments/${segmentId}`,
      automaticRequest(fromId, toId, 'issue-405-manual', 1, 1));
    expect(manualOnly.status()).toBe(400);
    expect(await manualOnly.text()).toContain('estimatedDurationSource');

    await mutate(page, 'put', `${editorApiPath}/segments/${segmentId}`, automaticRequest(fromId, fromId, 'walk', 9, 9));
    segment = (await state(page)).segmentsById[segmentId];
    expect(segment.estimatedDistanceKm).toBe(0);
    expect(segment.estimatedDurationMinutes).toBe(0);

    await mutate(page, 'put', `${editorApiPath}/segments/${segmentId}`, automaticRequest(null, null, 'walk', 9, 9));
    segment = (await state(page)).segmentsById[segmentId];
    expect(segment.estimatedDistanceKm).toBeNull();
    expect(segment.estimatedDurationMinutes).toBeNull();
    expect(segment.estimatedDurationSource).toBe('Automatic');
    await page.reload({ waitUntil: 'domcontentloaded' });
    await expectMountedWorkspace(page);
    await openSegment(page, segmentId);
    const unavailable = page.locator('#trip-editor-segment-form').getByLabel('Estimated duration minutes');
    await expect(unavailable).toBeDisabled();
    await expect(unavailable).toHaveAttribute('placeholder', 'Unavailable until route and speed are available');
  } finally {
    if (process.env.WAYFARER_E2E_KEEP_MEASUREMENT_FIXTURE !== '1') {
      if (segmentId) await deleteIfPresent(page, `${editorApiPath}/segments/${segmentId}`);
      if (fromId) await deleteIfPresent(page, `${editorApiPath}/places/${fromId}`);
      if (toId) await deleteIfPresent(page, `${editorApiPath}/places/${toId}`);
      if (regionId) await deleteIfPresent(page, `${editorApiPath}/regions/${regionId}`);
      const remaining = await state(page);
      expect(remaining.segmentsById[segmentId]).toBeUndefined();
      expect(remaining.placesById[fromId]).toBeUndefined();
      expect(remaining.placesById[toId]).toBeUndefined();
      expect(remaining.regionsById[regionId]).toBeUndefined();
    }
  }
});

function automaticRequest(fromPlaceId: string | null, toPlaceId: string | null, mode: string,
  estimatedDistanceKm: number, estimatedDurationMinutes: number): Record<string, unknown> {
  return { fromPlaceId, toPlaceId, mode, estimatedDistanceKm, estimatedDurationMinutes,
    estimatedDurationSource: 'Automatic', notesHtml: '', route: null };
}

function manualRequest(fromPlaceId: string, toPlaceId: string, mode: string,
  estimatedDistanceKm: number, estimatedDurationMinutes: number): Record<string, unknown> {
  return { fromPlaceId, toPlaceId, mode, estimatedDistanceKm, estimatedDurationMinutes,
    estimatedDurationSource: 'Manual', notesHtml: '', route: null };
}

async function createRegion(page: Page, name: string): Promise<string> {
  await page.getByRole('button', { name: 'Add Region' }).click();
  await page.locator('#trip-editor-region-form').getByLabel('Name').fill(name);
  const saved = page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith('/editor/regions'));
  await page.getByRole('button', { name: 'Save Region' }).click();
  const body = await (await saved).json();
  await expectSaved(page);
  await closeForm(page, '#trip-editor-region-form');
  return body.data.id;
}

async function createPlace(page: Page, regionName: string, name: string, latitude: string, longitude: string): Promise<string> {
  await regionCard(page, regionName).getByRole('button', { name: 'Add Place' }).click();
  const form = page.locator('#trip-editor-place-form');
  await form.getByLabel('Name').fill(name);
  await form.getByLabel('Address').fill(`${name} address`);
  await form.getByLabel('Latitude').fill(latitude);
  await form.getByLabel('Longitude').fill(longitude);
  await form.getByLabel('Reverse geocode this location on save').uncheck();
  const saved = page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith('/editor/places'));
  await page.getByRole('button', { name: 'Save Place', exact: true }).click();
  const body = await (await saved).json();
  await expectSaved(page);
  await closeForm(page, '#trip-editor-place-form');
  return body.data.id;
}

async function openSegment(page: Page, id: string): Promise<void> {
  await page.locator(`[data-segment-id="${id}"] .trip-editor-list-button`).click();
  await expect(page.locator('#trip-editor-segment-form')).toBeVisible();
}

async function closeForm(page: Page, selector: string): Promise<void> {
  const surface = page.locator('.trip-editor-surface--docked').filter({ has: page.locator(selector) });
  if (await surface.count()) await surface.getByRole('button', { name: 'Cancel' }).click();
}

async function expectSaved(page: Page): Promise<void> {
  await expect(page.locator('.trip-editor-save-state').filter({ hasText: /saved/i }).first()).toBeVisible();
}

async function state(page: Page): Promise<EditorState> {
  return await loadEditorStateFixture(page) as EditorState;
}

async function mutate(page: Page, method: 'post' | 'put', path: string, data: Record<string, unknown>): Promise<any> {
  const response = await rawMutation(page, method, path, data);
  expect(response.ok(), `${method.toUpperCase()} ${path} returned ${response.status()}: ${await response.text()}`).toBeTruthy();
  return await response.json();
}

async function rawMutation(page: Page, method: 'post' | 'put', path: string, data: Record<string, unknown>) {
  const token = await page.locator('#trip-editor-antiforgery input[name="__RequestVerificationToken"]').inputValue();
  return await page.request[method](absoluteUrl(path), { data, headers: { RequestVerificationToken: token } });
}

async function deleteIfPresent(page: Page, path: string): Promise<void> {
  const token = await page.locator('#trip-editor-antiforgery input[name="__RequestVerificationToken"]').inputValue();
  const response = await page.request.delete(absoluteUrl(path), { headers: { RequestVerificationToken: token } });
  expect([200, 404]).toContain(response.status());
}
