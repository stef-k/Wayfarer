import { execFile } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { promisify } from 'node:util';
import { expect, test, type Locator, type Page, type Route } from '@playwright/test';
import {
  absoluteUrl,
  activeEditorAlert,
  editorApiPath,
  editorPath,
  expectMountedWorkspace,
  loadEditorStateFixture,
  pathRegex,
  signIn
} from './tripEditorTestUtils';

const execute = promisify(execFile);

type Fixture = {
  profileId: string;
  mode: string;
  waypointSegmentId: string;
  zeroSegmentId: string;
  staleSegmentId: string;
  fromId: string;
  waypointId: string;
  staleWaypointId: string;
  alternateId: string;
  toId: string;
  estimatedDistanceKm: number;
  estimatedDurationMinutes: number;
  estimatedDurationSource: string;
  routeCoordinates: number[][];
};

test.describe.serial('#407/#408 persisted waypoint aggregate and accessible editor', () => {
  test('mounted editor authors waypoints and preserves the complete aggregate through failures and confirmation', async ({ page }) => {
    test.setTimeout(180_000);
    const fixture = await loadFixture();
    await signIn(page);
    const response = await page.goto(absoluteUrl(editorPath), { waitUntil: 'domcontentloaded' });
    expect(response?.ok()).toBeTruthy();
    await expectMountedWorkspace(page);
    expect(await page.evaluate(() => typeof window.Sortable)).toBe('function');

    const initial = await editorState(page);
    const original = structuredClone(initial.segmentsById[fixture.waypointSegmentId]);
    expect(original.waypointPlaceIds).toEqual([fixture.waypointId]);
    expect(original.waypointRouteVertexIndices).toEqual([2]);
    expect(original.estimatedDistanceKm).toBe(8.303);
    expect(original.estimatedDurationMinutes).toBe(fixture.estimatedDurationMinutes);
    expect(original.estimatedDurationSource).toBe(fixture.estimatedDurationSource);
    expect(original.mode).toBe(fixture.mode);
    expect(original.transportProfileId).toBe(fixture.profileId);
    expect(original.route?.coordinates).toEqual(fixture.routeCoordinates);

    await openSegment(page, fixture.waypointSegmentId);
    const form = page.locator('#trip-editor-segment-form');
    await expect(page.locator(`[data-segment-id="${fixture.waypointSegmentId}"][data-route-owner="draft"]`)).toHaveCount(0);
    await expect(page.locator(`[data-segment-id="${fixture.waypointSegmentId}"][data-route-owner="saved"]`)).toHaveCount(1);
    const drawRoute = page.getByRole('button', { name: 'Draw/Edit Route' });
    const clearRoute = page.getByRole('button', { name: 'Clear Route' });
    const routeExplanation = page.getByText('Route editing for segments with intermediate places will be available in a later update.');
    await expect(drawRoute).toBeDisabled();
    await expect(clearRoute).toBeDisabled();
    await expect(routeExplanation).toBeVisible();
    const explanationId = await routeExplanation.getAttribute('id');
    expect(explanationId).toBeTruthy();
    await expect(drawRoute).toHaveAttribute('aria-describedby', explanationId!);
    await expect(clearRoute).toHaveAttribute('aria-describedby', explanationId!);
    await expect(form.getByLabel('From place')).toBeEnabled();
    await expect(notesEditor(form)).toBeEditable();
    const forbiddenRequests: string[] = [];
    page.on('request', request => {
      if (request.method() !== 'GET' && request.url().includes('/segments')) forbiddenRequests.push(request.url());
    });
    await expect(drawRoute.click({ timeout: 500 })).rejects.toThrow();
    await expect(clearRoute.click({ timeout: 500 })).rejects.toThrow();
    await drawRoute.focus();
    await page.keyboard.press('Enter');
    await page.keyboard.press('Space');
    await expect(page.getByRole('region', { name: 'Map work' })).toHaveCount(0);
    expect(forbiddenRequests).toEqual([]);
    await notesEditor(form).fill('Browser ordinary visible edit');
    const ordinaryRequest = captureNextPut(page, fixture.waypointSegmentId);
    const ordinaryResponse = waitForPut(page, fixture.waypointSegmentId);
    await page.getByRole('button', { name: 'Save Segment' }).click();
    const submitted = await ordinaryRequest;
    expect((await ordinaryResponse).ok()).toBeTruthy();
    expect(submitted.waypointPlaceIds).toEqual([fixture.waypointId]);
    expect(submitted.waypointRouteVertexIndices).toEqual([2]);
    expect(submitted.route).toEqual(original.route);
    expect(submitted.transportProfileId ?? original.transportProfileId).toBe(original.transportProfileId);
    const ordinaryState = await editorState(page);
    const saved = ordinaryState.segmentsById[fixture.waypointSegmentId];
    expectHiddenAggregate(saved, original);
    expect(saved.notesHtml).toContain('Browser ordinary visible edit');
    expect(saved.aggregateConcurrencyToken).not.toBe(original.aggregateConcurrencyToken);
    await fixtureControl('verify-preserved');

    await openSegment(page, fixture.waypointSegmentId);
    await notesEditor(form).fill('Draft survives stale aggregate');
    await fixtureControl('drift');
    const staleRequest = captureNextPut(page, fixture.waypointSegmentId);
    await page.getByRole('button', { name: 'Save Segment' }).click();
    expect((await staleRequest).waypointPlaceIds).toEqual([fixture.waypointId]);
    await expect(activeEditorAlert(page)).toContainText(/segment-aggregate-stale|changed|reload/i);
    await expect(notesEditor(form)).toContainText('Draft survives stale aggregate');
    await expect(page.getByRole('heading', { name: 'Current saved Segment' })).toBeFocused();
    await page.getByRole('button', { name: 'Reload current saved Segment' }).click();
    const reloadConfirmation = page.getByRole('dialog', { name: 'Reload current saved Segment?' });
    await reloadConfirmation.getByRole('button', { name: 'Reload saved Segment' }).click();
    await expect(notesEditor(form)).toContainText('Externally drifted');
    const staleCanonical = (await editorState(page)).segmentsById[fixture.waypointSegmentId];
    expect(staleCanonical.notesHtml).toContain('Externally drifted');
    expectHiddenAggregate(staleCanonical, original);

    await page.reload({ waitUntil: 'domcontentloaded' });
    await expectMountedWorkspace(page);
    await openSegment(page, fixture.waypointSegmentId);
    await form.getByLabel('From place').selectOption(fixture.alternateId);
    const confirmationPath = pathRegex(`${editorApiPath}/segments/${fixture.waypointSegmentId}`);
    let replacedConfirmation = false;
    const replaceConfirmation = async (route: Route): Promise<void> => {
      if (route.request().method() !== 'PUT' || replacedConfirmation) return route.fallback();
      const upstream = await route.fetch();
      const body = await upstream.text();
      if (upstream.status() !== 409 || !body.includes('segment-route-clear-confirmation-required'))
        return route.fulfill({ response: upstream, body });
      replacedConfirmation = true;
      await route.fulfill({ response: upstream, body, headers: {
        ...upstream.headers(), 'x-wayfarer-clear-route-confirmation': 'invalid-browser-confirmation'
      } });
    };
    await page.route(confirmationPath, replaceConfirmation);
    await page.getByRole('button', { name: 'Save Segment' }).click();
    const confirmation = page.getByRole('dialog', { name: 'Clear custom route?' });
    await expect(confirmation).toBeVisible();
    await expect.poll(() => replacedConfirmation).toBe(true);
    const staleConfirmationResponse = page.waitForResponse(candidate =>
      candidate.status() === 409 && candidate.url().endsWith(`/segments/${fixture.waypointSegmentId}`));
    await confirmation.getByRole('button', { name: 'Clear route and save' }).click();
    const refreshed = await staleConfirmationResponse;
    expect((await refreshed.json()).code).toBe('segment-route-clear-confirmation-stale');
    expect(refreshed.headers()['x-wayfarer-clear-route-confirmation']).toBeTruthy();
    await expect(activeEditorAlert(page)).toContainText(/segment-route-clear-confirmation-stale|changed|reload|confirm/i);
    await expect(form.getByLabel('From place')).toHaveValue(fixture.alternateId);
    const afterStaleConfirmation = (await editorState(page)).segmentsById[fixture.waypointSegmentId];
    expect(afterStaleConfirmation.fromPlaceId).toBe(fixture.fromId);
    expectHiddenAggregate(afterStaleConfirmation, original);
    await page.unroute(confirmationPath, replaceConfirmation);

    await page.reload({ waitUntil: 'domcontentloaded' });
    await expectMountedWorkspace(page);
    await openSegment(page, fixture.waypointSegmentId);
    await form.getByLabel('From place').selectOption(fixture.alternateId);
    await page.getByRole('button', { name: 'Save Segment' }).click();
    const successfulConfirmation = page.getByRole('dialog', { name: 'Clear custom route?' });
    await expect(successfulConfirmation).toBeVisible();
    const confirmedSave = page.waitForResponse(candidate =>
      candidate.request().method() === 'PUT' && candidate.status() === 200
        && candidate.url().endsWith(`/segments/${fixture.waypointSegmentId}`));
    await successfulConfirmation.getByRole('button', { name: 'Clear route and save' }).click();
    expect((await confirmedSave).ok()).toBeTruthy();
    await expect(successfulConfirmation).toHaveCount(0);
    const cleared = (await editorState(page)).segmentsById[fixture.waypointSegmentId];
    expect(cleared.fromPlaceId).toBe(fixture.alternateId);
    expect(cleared.route).toBeNull();
    expect(cleared.waypointRouteVertexIndices).toEqual([null]);
    await expect(page.locator(`[data-segment-id="${fixture.waypointSegmentId}"][data-route-owner="draft"]`)).toHaveCount(0);
    await expect(page.locator(`[data-segment-id="${fixture.waypointSegmentId}"][data-route-owner="saved"][data-route-kind="fallback"]`)).toHaveCount(1);

    await openSegment(page, fixture.staleSegmentId);
    const staleSelector = form.getByLabel(/Intermediate place 1:/);
    await expect(staleSelector).toHaveValue(fixture.staleWaypointId);
    await expect(staleSelector.locator(`option[value="${fixture.staleWaypointId}"]`)).toHaveAttribute('disabled', '');
    await expect(staleSelector.locator(`option[value="${fixture.staleWaypointId}"]`)).toContainText(/Shadow waypoint .*unavailable/i);
    const staleState = (await editorState(page)).segmentsById[fixture.staleSegmentId];
    expect(staleState.effectiveRoute.coordinates).toHaveLength(3);
    expect(staleState.effectiveRoute.coordinates[1]).toEqual([23.73, 38.05]);
    const fallbackPath = page.locator(`[data-segment-id="${fixture.staleSegmentId}"][data-route-owner="saved"][data-route-kind="fallback"]`);
    await expect(fallbackPath).toHaveCount(1);
    expect((await fallbackPath.getAttribute('d'))?.match(/[ML]/g)?.length).toBeGreaterThanOrEqual(3);

    await openSegment(page, fixture.waypointSegmentId);
    await notesEditor(form).fill('Failed save keeps this draft');
    let failedBody: Record<string, unknown> | null = null;
    const matcher = pathRegex(`${editorApiPath}/segments/${fixture.waypointSegmentId}`);
    const fail = async (route: Route): Promise<void> => {
      if (route.request().method() !== 'PUT') return route.fallback();
      failedBody = route.request().postDataJSON();
      await route.fulfill({ status: 500, contentType: 'application/problem+json', body: '{}' });
    };
    await page.route(matcher, fail);
    await page.getByRole('button', { name: 'Save Segment' }).click();
    await expect(activeEditorAlert(page)).toContainText(/500|failed/i);
    await expect(notesEditor(form)).toContainText('Failed save keeps this draft');
    expect(failedBody).toMatchObject({ waypointPlaceIds: [fixture.waypointId], waypointRouteVertexIndices: [null] });
    await page.unroute(matcher, fail);

    await page.reload({ waitUntil: 'domcontentloaded' });
    await expectMountedWorkspace(page);
    await openSegment(page, fixture.zeroSegmentId);
    await expect(page.getByRole('button', { name: 'Draw/Edit Route' })).toBeEnabled();
    const zeroBefore = (await editorState(page)).segmentsById[fixture.zeroSegmentId];
    expect(zeroBefore.waypointPlaceIds).toEqual([]);
    await expect(form.getByRole('group', { name: 'Intermediate places' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Clear Route' })).toBeDisabled();
    const placeToAdd = form.getByLabel('Place to add');
    await placeToAdd.selectOption(fixture.waypointId);
    await form.getByRole('button', { name: 'Add intermediate place' }).press('Space');
    await expect(form.getByLabel(/Intermediate place 1:/)).toBeFocused();
    await form.getByLabel(/Intermediate place 1:/).selectOption(fixture.alternateId);
    await expect(form.locator('p', { hasText: 'Journey order:' })).toContainText(/From .* → Alternate .* → To /);
    await form.getByLabel(/Intermediate place 1:/).selectOption(fixture.waypointId);
    await placeToAdd.selectOption(fixture.alternateId);
    await form.getByRole('button', { name: 'Add intermediate place' }).press('Enter');
    await expect(form.locator('p', { hasText: 'Journey order:' })).toContainText(/From .* → Waypoint .* → Alternate .* → To /);
    await form.getByRole('button', { name: /Move Alternate .* up/ }).press('Enter');
    await expect(form.locator('p', { hasText: 'Journey order:' })).toContainText(/From .* → Alternate .* → Waypoint .* → To /);
    await expect(page.getByRole('button', { name: 'Draw/Edit Route' })).toBeDisabled();
    await expect(form.getByText(/map keeps the saved route.*updates after Save/i)).toBeVisible();
    await notesEditor(form).fill('Waypoint UI save');
    const zeroResponse = waitForPut(page, fixture.zeroSegmentId);
    await page.getByRole('button', { name: 'Save Segment' }).click();
    expect((await zeroResponse).ok()).toBeTruthy();
    const zeroAfter = (await editorState(page)).segmentsById[fixture.zeroSegmentId];
    expect(zeroAfter.waypointPlaceIds).toEqual([fixture.alternateId, fixture.waypointId]);
    expect(zeroAfter.waypointRouteVertexIndices).toEqual([null, null]);

    await page.reload({ waitUntil: 'domcontentloaded' });
    await expectMountedWorkspace(page);
    await openSegment(page, fixture.zeroSegmentId);
    await expect(form.locator('p', { hasText: 'Journey order:' })).toContainText(/Alternate .* → Waypoint /);
    await form.getByRole('button', { name: /Remove Waypoint / }).click();
    const removalResponse = waitForPut(page, fixture.zeroSegmentId);
    await page.getByRole('button', { name: 'Save Segment' }).click();
    expect((await removalResponse).ok()).toBeTruthy();

    await placeToAdd.selectOption(fixture.waypointId);
    await form.getByRole('button', { name: 'Add intermediate place' }).click();
    await page.getByRole('button', { name: 'Reset' }).click();
    await expect(form.getByLabel(/Intermediate place 2:/)).toHaveCount(0);
    await placeToAdd.selectOption(fixture.waypointId);
    await form.getByRole('button', { name: 'Add intermediate place' }).click();
    await page.getByRole('button', { name: 'Cancel' }).click();
    const discard = page.getByRole('dialog', { name: 'Discard changes?' });
    await discard.getByRole('button', { name: 'Discard' }).click();
    await openSegment(page, fixture.zeroSegmentId);
    await expect(form.getByLabel(/Intermediate place 1:/)).toHaveValue(fixture.alternateId);
    await expect(form.getByLabel(/Intermediate place 2:/)).toHaveCount(0);

    await placeToAdd.selectOption(fixture.waypointId);
    await form.getByRole('button', { name: 'Add intermediate place' }).click();
    await form.getByLabel('From place').selectOption(fixture.alternateId);
    const invalidResponse = page.waitForResponse(candidate => candidate.request().method() === 'PUT' && candidate.status() === 400 && candidate.url().endsWith(`/segments/${fixture.zeroSegmentId}`));
    await page.getByRole('button', { name: 'Save Segment' }).click();
    expect((await invalidResponse).ok()).toBeFalsy();
    const invalidWaypoint = form.getByLabel(/Intermediate place 1: Alternate/);
    await expect(invalidWaypoint).toBeFocused();
    await expect(invalidWaypoint).toHaveAttribute('aria-invalid', 'true');
    const rowErrorId = await invalidWaypoint.getAttribute('aria-errormessage');
    expect(rowErrorId).toBeTruthy();
    const rowError = form.locator(`#${rowErrorId}`);
    await expect(rowError).toBeVisible();
    await expect(rowError).toContainText('A waypoint cannot duplicate an endpoint.');
    await expect(form.locator('.trip-editor-form-error')).toHaveCount(0);
    await form.getByRole('button', { name: /Move Alternate .* down/ }).click();
    const reorderedInvalidWaypoint = form.getByLabel(/Intermediate place 2: Alternate/);
    await expect(reorderedInvalidWaypoint).toHaveAttribute('aria-errormessage', rowErrorId!);
    await expect(rowError).toContainText('A waypoint cannot duplicate an endpoint.');
    await form.getByRole('button', { name: /Remove Waypoint / }).click();
    const retainedInvalidWaypoint = form.locator(`select[aria-errormessage="${rowErrorId}"]`);
    const retainedClientId = await retainedInvalidWaypoint.getAttribute('data-waypoint-client-id');
    expect(retainedClientId).toBeTruthy();
    const correctedWaypoint = form.locator(`[data-waypoint-client-id="${retainedClientId}"]`);
    await correctedWaypoint.selectOption(fixture.waypointId);
    await expect(correctedWaypoint).not.toHaveAttribute('aria-invalid', 'true');
    await expect(correctedWaypoint).not.toHaveAttribute('aria-errormessage', /.+/);
    await expect(rowError).toHaveCount(0);
    await page.getByRole('button', { name: 'Reset' }).click();

    await form.getByLabel('From place').selectOption(fixture.toId);
    await page.getByRole('button', { name: 'Reset' }).click();
    await expect(form.getByLabel('From place')).toBeFocused();

    const aggregateValidation = async (route: Route): Promise<void> => {
      if (route.request().method() !== 'PUT') return route.fallback();
      await route.fulfill({ status: 400, contentType: 'application/problem+json', body: JSON.stringify({
        title: 'Validation failed', errors: { waypointRouteVertexIndices: ['Waypoint route mapping is invalid.'] }
      }) });
    };
    await page.route(pathRegex(`${editorApiPath}/segments/${fixture.zeroSegmentId}`), aggregateValidation);
    await notesEditor(form).fill('Aggregate validation focus');
    await page.getByRole('button', { name: 'Save Segment' }).click();
    await expect(form.getByRole('group', { name: 'Intermediate places' })).toBeFocused();
    await page.unroute(pathRegex(`${editorApiPath}/segments/${fixture.zeroSegmentId}`), aggregateValidation);
    await page.getByRole('button', { name: 'Reset' }).click();

    await notesEditor(form).fill('Notes-only Reset focus');
    await page.getByRole('button', { name: 'Reset' }).click();
    await expect(notesEditor(form)).toBeFocused();
    await expect(notesEditor(form)).toHaveAttribute('contenteditable', 'true');
    const notesLabelId = await notesEditor(form).getAttribute('aria-labelledby');
    expect(notesLabelId).toBeTruthy();
    await expect(form.locator(`#${notesLabelId}`)).toHaveText('Notes');

    for (const viewport of [{ width: 1280, height: 900 }, { width: 760, height: 900 }, { width: 390, height: 844 }, { width: 430, height: 932 }]) {
      await page.setViewportSize(viewport);
      await expect(form.getByRole('group', { name: 'Intermediate places' })).toBeVisible();
      expect(await form.evaluate(element => element.scrollWidth <= element.clientWidth)).toBeTruthy();
    }
    await page.setViewportSize({ width: 1280, height: 900 });
    await openSegment(page, fixture.zeroSegmentId);
    await expect(form.getByRole('group', { name: 'Intermediate places' })).toBeVisible();
    const chromium = await page.context().newCDPSession(page);
    await chromium.send('Emulation.setPageScaleFactor', { pageScaleFactor: 2 });
    await expect.poll(() => page.evaluate(() => window.visualViewport?.scale)).toBe(2);
    expect(await form.evaluate(element => element.scrollWidth <= element.clientWidth)).toBeTruthy();
    await chromium.send('Emulation.setPageScaleFactor', { pageScaleFactor: 1 });
    await chromium.detach();
    await fixtureControl('verify-ui');

    await page.getByRole('button', { name: 'Add Segment' }).click();
    await form.getByLabel('From place').selectOption(fixture.fromId);
    await form.getByLabel('To place').selectOption(fixture.toId);
    await form.getByLabel('Transport mode').selectOption({ index: 1 });
    const createResponse = page.waitForResponse(candidate =>
      candidate.request().method() === 'POST' && candidate.url().endsWith('/segments'));
    await page.getByRole('button', { name: 'Save Segment' }).click();
    expect((await createResponse).ok()).toBeTruthy();
    const created = Object.values((await editorState(page)).segmentsById as Record<string, any>)
      .find(segment => ![fixture.waypointSegmentId, fixture.zeroSegmentId, fixture.staleSegmentId].includes(segment.id));
    expect(created?.waypointPlaceIds).toEqual([]);
    expect(created?.waypointRouteVertexIndices).toEqual([]);
    expect((await page.request.get(absoluteUrl(`/Public/Trip/${fixture.waypointSegmentId}`))).status()).toBe(404);
  });
});

async function loadFixture(): Promise<Fixture> {
  const path = required('WAYFARER_E2E_WAYPOINT_FIXTURE');
  return JSON.parse(await readFile(path, 'utf8')) as Fixture;
}
async function fixtureControl(command: 'drift' | 'verify-preserved' | 'verify-ui'): Promise<void> {
  await execute('dotnet', [required('WAYFARER_E2E_WAYPOINT_HELPER'), command,
    required('WAYFARER_E2E_WAYPOINT_FIXTURE')], { env: process.env });
}

function required(name: string): string {
  const value = process.env[name];
  if (!value) throw new Error(`${name} is required for #407 browser coverage.`);
  return value;
}

async function editorState(page: Page): Promise<Record<string, any>> {
  return await loadEditorStateFixture(page) as Record<string, any>;
}

async function openSegment(page: Page, id: string): Promise<void> {
  const form = page.locator('#trip-editor-segment-form');
  if (await form.isVisible().catch(() => false)) {
    await page.getByRole('button', { name: 'Cancel' }).filter({ visible: true }).click();
  }
  await page.locator(`[data-segment-id="${id}"] .trip-editor-list-button`).click();
  await expect(form).toBeVisible();
}

function notesEditor(form: Locator): Locator {
  return form.locator('.ql-editor[contenteditable="true"]');
}

async function captureNextPut(page: Page, id: string): Promise<Record<string, any>> {
  const request = await page.waitForRequest(candidate =>
    candidate.method() === 'PUT' && candidate.url().endsWith(`/segments/${id}`));
  return request.postDataJSON() as Record<string, any>;
}

function waitForPut(page: Page, id: string) {
  return page.waitForResponse(candidate =>
    candidate.request().method() === 'PUT' && candidate.url().endsWith(`/segments/${id}`));
}

function expectHiddenAggregate(actual: Record<string, any>, original: Record<string, any>): void {
  expect(actual.waypointPlaceIds).toEqual(original.waypointPlaceIds);
  expect(actual.waypointRouteVertexIndices).toEqual(original.waypointRouteVertexIndices);
  expect(actual.route).toEqual(original.route);
  expect(actual.transportProfileId).toBe(original.transportProfileId);
}
