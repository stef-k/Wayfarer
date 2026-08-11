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
  waypointSegmentId: string;
  zeroSegmentId: string;
  fromId: string;
  waypointId: string;
  alternateId: string;
  toId: string;
};

test.describe.serial('#407 persisted waypoint aggregate', () => {
  test('mounted editor preserves hidden aggregate through success, contention, confirmation, failure, and zero-waypoint work', async ({ page }) => {
    test.setTimeout(120_000);
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
    expect(original.route?.coordinates).toHaveLength(4);
    await expectNoWaypointAuthoring(page);

    await openSegment(page, fixture.waypointSegmentId);
    const form = page.locator('#trip-editor-segment-form');
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

    await openSegment(page, fixture.waypointSegmentId);
    await notesEditor(form).fill('Draft survives stale aggregate');
    await fixtureControl('drift');
    const staleRequest = captureNextPut(page, fixture.waypointSegmentId);
    await page.getByRole('button', { name: 'Save Segment' }).click();
    expect((await staleRequest).waypointPlaceIds).toEqual([fixture.waypointId]);
    await expect(activeEditorAlert(page)).toContainText(/segment-aggregate-stale|changed|reload/i);
    await expect(notesEditor(form)).toContainText('Draft survives stale aggregate');
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
    const zeroBefore = (await editorState(page)).segmentsById[fixture.zeroSegmentId];
    expect(zeroBefore.waypointPlaceIds).toEqual([]);
    await notesEditor(form).fill('Zero waypoint update remains functional');
    const zeroResponse = waitForPut(page, fixture.zeroSegmentId);
    await page.getByRole('button', { name: 'Save Segment' }).click();
    expect((await zeroResponse).ok()).toBeTruthy();
    const zeroAfter = (await editorState(page)).segmentsById[fixture.zeroSegmentId];
    expect(zeroAfter.waypointPlaceIds).toEqual([]);
    expect(zeroAfter.waypointRouteVertexIndices).toEqual([]);

    await page.getByRole('button', { name: 'Add Segment' }).click();
    await form.getByLabel('From place').selectOption(fixture.fromId);
    await form.getByLabel('To place').selectOption(fixture.toId);
    await form.getByLabel('Transport mode').selectOption({ index: 1 });
    const createResponse = page.waitForResponse(candidate =>
      candidate.request().method() === 'POST' && candidate.url().endsWith('/segments'));
    await page.getByRole('button', { name: 'Save Segment' }).click();
    expect((await createResponse).ok()).toBeTruthy();
    const created = Object.values((await editorState(page)).segmentsById as Record<string, any>)
      .find(segment => segment.id !== fixture.waypointSegmentId && segment.id !== fixture.zeroSegmentId);
    expect(created?.waypointPlaceIds).toEqual([]);
    expect(created?.waypointRouteVertexIndices).toEqual([]);
    await expectNoWaypointAuthoring(page);
    expect((await page.request.get(absoluteUrl(`/Public/Trip/${fixture.waypointSegmentId}`))).status()).toBe(404);
  });
});

async function loadFixture(): Promise<Fixture> {
  const path = required('WAYFARER_E2E_WAYPOINT_FIXTURE');
  return JSON.parse(await readFile(path, 'utf8')) as Fixture;
}

async function fixtureControl(command: 'drift'): Promise<void> {
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

async function expectNoWaypointAuthoring(page: Page): Promise<void> {
  await expect(page.getByRole('button', { name: /add waypoint|reorder waypoint|pick waypoint|waypoint picker|reorder anchor|pick anchor/i })).toHaveCount(0);
  await expect(page.locator('label').filter({ hasText: /waypoint|route vertex index/i })).toHaveCount(0);
}
