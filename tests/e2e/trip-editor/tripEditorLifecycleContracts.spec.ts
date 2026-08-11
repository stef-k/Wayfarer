import fs from 'node:fs';
import net from 'node:net';
import { execFileSync } from 'node:child_process';
import { expect, test, type Locator, type Page } from '@playwright/test';
import {
  absoluteUrl,
  editorApiPath,
  editorPath,
  expectMountedWorkspace,
  signIn
} from './tripEditorTestUtils';

type LifecycleFixture = {
  tripId: string;
  waypointOnlyPlace: { id: string; name: string; endpointSegments: number; waypointOnlySegments: number };
  mixedPlace: { id: string; name: string; endpointSegments: number; waypointOnlySegments: number };
  deletedRegion: { id: string; name: string; deletedPlaces: number; deletedAreas: number; endpointSegments: number; waypointOnlySegments: number };
  stalePlace: { id: string; name: string; endpointSegments: number; waypointOnlySegments: number };
  failurePlace: { id: string; name: string; endpointSegments: number; waypointOnlySegments: number };
  phoneStalePlace: { id: string; name: string; endpointSegments: number; waypointOnlySegments: number };
  phoneFailurePlace: { id: string; name: string; endpointSegments: number; waypointOnlySegments: number };
  phoneRegion: { id: string; name: string; deletedPlaces: number; deletedAreas: number; endpointSegments: number; waypointOnlySegments: number };
  staleDriftSegmentId: string;
  segmentIds: string[];
};

const fixture = loadLifecycleFixture();

test.describe.serial('Trip Editor #406 real lifecycle contracts', () => {
  test('waypoint-only Place warning is server-backed, cancellable, and accessible', async ({ page }) => {
    await openFixture(page);
    const editButton = placeRow(page, fixture.waypointOnlyPlace.id).getByRole('button', { name: 'Edit' });
    await editButton.click();
    const deleteButton = page.getByRole('button', { name: 'Delete', exact: true });
    await deleteButton.click();

    const dialog = page.getByRole('dialog', { name: 'Delete place?' });
    await expect(dialog).toBeVisible();
    await expect(dialog).toHaveAccessibleDescription(
      `This deletes ${fixture.waypointOnlyPlace.endpointSegments} connected segment(s) and updates ${fixture.waypointOnlyPlace.waypointOnlySegments} waypoint route(s).`
    );
    await expect(dialog.getByRole('button', { name: 'Keep place' })).toBeFocused();
    await dialog.getByRole('button', { name: 'Keep place' }).click();

    await expect(dialog).toHaveCount(0);
    await expect(placeRow(page, fixture.waypointOnlyPlace.id)).toBeVisible();
    await expect(page.locator('.trip-editor-workspace')).toBeFocused();
    await expectPersistedPlace(page, fixture.waypointOnlyPlace.id);

    const survivingSegment = fixture.segmentIds[0];
    const requests = collectDeleteRequests(page, fixture.waypointOnlyPlace.id);
    await deleteButton.click();
    await dialog.getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(placeRow(page, fixture.waypointOnlyPlace.id)).toHaveCount(0);
    await expect(segmentRow(page, survivingSegment)).toBeVisible();
    expect(requests()).toHaveLength(2);
    expect(requests()[0].confirmation).toBeFalsy();
    expect(requests()[1].confirmation).toBeTruthy();
    await expectDeletedPlace(page, fixture.waypointOnlyPlace.id);
    await expectMeaningfulFocus(page);
  });

  test('mixed Place deletion removes endpoint Segment and reconciles its survivor without reload', async ({ page }) => {
    await openFixture(page);
    const [endpointSegment, survivingSegment] = fixture.segmentIds.slice(1, 3);
    await deletePlace(page, fixture.mixedPlace);
    await expect(placeRow(page, fixture.mixedPlace.id)).toHaveCount(0);
    await expect(segmentRow(page, endpointSegment)).toHaveCount(0);
    await expect(segmentRow(page, survivingSegment)).toBeVisible();
    await expectDeletedPlace(page, fixture.mixedPlace.id);
    await expectMeaningfulFocus(page);
  });

  test('Region deletion applies exact child, dependency, and order state without reload', async ({ page }) => {
    await openFixture(page);
    const target = fixture.deletedRegion;
    const card = regionCard(page, target.id);
    await card.locator('.trip-editor-region-card__header').getByRole('button', { name: 'Edit' }).click();
    await page.getByRole('button', { name: 'Delete', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Delete region?' });
    await expect(dialog).toHaveAccessibleDescription(
      `This deletes ${target.deletedPlaces} place(s), ${target.deletedAreas} area(s), ${target.endpointSegments} connected segment(s), and updates ${target.waypointOnlySegments} waypoint route(s).`
    );
    await dialog.getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(card).toHaveCount(0);
    await expect(segmentRow(page, fixture.segmentIds[3])).toHaveCount(0);
    await expect(segmentRow(page, fixture.segmentIds[4])).toBeVisible();
    await expectMeaningfulFocus(page);
  });

  test('stale confirmation refreshes bounded dependencies before the second confirmation', async ({ page }) => {
    await openFixture(page);
    const requests = collectDeleteRequests(page, fixture.stalePlace.id);
    await placeRow(page, fixture.stalePlace.id).getByRole('button', { name: 'Edit' }).click();
    await page.getByRole('button', { name: 'Delete', exact: true }).click();
    const first = page.getByRole('dialog', { name: 'Delete place?' });
    await expect(first).toContainText('updates 1 waypoint route(s)');
    applyFixtureDrift();
    await first.getByRole('button', { name: 'Delete', exact: true }).click();

    const refreshed = page.getByRole('dialog', { name: 'Dependencies changed' });
    await expect(refreshed).toContainText('updates 2 waypoint route(s)');
    await expect(refreshed.getByRole('button', { name: 'Keep place' })).toBeFocused();
    await refreshed.getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(placeRow(page, fixture.stalePlace.id)).toHaveCount(0);
    expect(requests()).toHaveLength(3);
    expect(requests()[1].confirmation).toBeTruthy();
    expect(requests()[2].confirmation).toBeTruthy();
    await expectMeaningfulFocus(page);
  });

  test('provider failure preserves visible state, prevents duplicate confirmation, and permits retry', async ({ page }, testInfo) => {
    test.setTimeout(120_000);
    const timing = (event: string): void => {
      const description = `${new Date().toISOString()} ${event}`;
      testInfo.annotations.push({ type: 'provider-outage-timing', description });
      console.info(`[provider-outage] ${description}`);
    };
    await openFixture(page);
    const target = fixture.failurePlace;
    const requests = collectDeleteRequests(page, target.id);
    await placeRow(page, target.id).getByRole('button', { name: 'Edit' }).click();
    const deleteButton = page.getByRole('button', { name: 'Delete', exact: true });
    await deleteButton.click();
    const dialog = page.getByRole('dialog', { name: 'Delete place?' });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'Delete', exact: true })).toBeEnabled();
    timing('Delete button actionable before stop');
    timing('warning dialog visible');
    timing('PostgreSQL stop command start');
    stopPostgres();
    timing('PostgreSQL stop command end');
    await expect.poll(postgresPortIsOpen, { timeout: 15_000 }).toBe(false);
    timing('port closure confirmed');
    try {
      const confirmDelete = dialog.getByRole('button', { name: 'Delete', exact: true });
      await expect(dialog).toBeVisible();
      await expect(confirmDelete).toBeVisible();
      await expect(confirmDelete).toBeEnabled();
      timing('Delete button actionable after stop');
      const response = page.waitForResponse(candidate => candidate.request().method() === 'DELETE' && candidate.url().endsWith(`/places/${target.id}`));
      const dispatched = page.waitForRequest(candidate => candidate.method() === 'DELETE' && candidate.url().endsWith(`/places/${target.id}`));
      timing('confirmation click');
      await confirmDelete.click();
      await dispatched;
      timing('DELETE request dispatched');
      timing('confirmation click completed');
      await expect(dialog).toHaveCount(0);
      await expect(deleteButton).toBeDisabled();
      await expect(page.getByRole('status').filter({ hasText: 'Saving' }).first()).toBeVisible();
      expect((await response).status()).toBe(500);
      timing('HTTP 500 response');
      expect(requests()).toHaveLength(2);
    } finally {
      timing('PostgreSQL restart start');
      startPostgres();
      await expect.poll(postgresPortIsOpen, { timeout: 30_000 }).toBe(true);
      timing('PostgreSQL restart end');
    }

    await expect.poll(async () => (await page.request.get(absoluteUrl(editorApiPath))).status(), { timeout: 30_000 }).toBe(200);
    timing('PostgreSQL readiness verified');

    await expect(placeRow(page, target.id)).toBeVisible();
    await expect(segmentRow(page, fixture.segmentIds[7])).toBeVisible();
    await expect(page.locator('.trip-editor-form-error[role="alert"]')).toContainText('place delete returned 500');
    timing('retry request start');
    await deleteButton.click();
    await page.getByRole('dialog', { name: 'Delete place?' }).getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(placeRow(page, target.id)).toHaveCount(0);
    timing('retry success response');
    expect(requests()).toHaveLength(4);
    await expectMeaningfulFocus(page);
  });

  for (const viewport of [{ width: 390, height: 844 }, { width: 430, height: 932 }]) {
    test(`phone ${viewport.width}x${viewport.height} keeps lifecycle warnings usable and state coherent`, async ({ page }) => {
      await page.setViewportSize(viewport);
      await openFixture(page);
      await assertPhonePlaceWarning(page, fixture.phoneFailurePlace, viewport);
      await assertPhoneRegionWarning(page, fixture.phoneRegion, viewport);
      await assertPhoneStaleRefresh(page, viewport);
      await assertPhoneFailureRollback(page, viewport);
      await expectNoHorizontalOverflow(page);
    });
  }
});

/** Loads the exact IDs emitted by the run-owned PostgreSQL fixture provisioner. */
function loadLifecycleFixture(): LifecycleFixture {
  const path = process.env.WAYFARER_E2E_LIFECYCLE_FIXTURE;
  if (!path) {
    throw new Error('WAYFARER_E2E_LIFECYCLE_FIXTURE must name the run-owned #406 fixture manifest.');
  }

  return JSON.parse(fs.readFileSync(path, 'utf8')) as LifecycleFixture;
}

/** Authenticates through Identity and proves the exact owned Trip mounts before lifecycle interaction. */
async function openFixture(page: Page): Promise<void> {
  expect(fixture.tripId).toBe(editorPath.split('/').pop());
  await signIn(page);
  await page.goto(absoluteUrl(editorPath));
  await expect(page).toHaveURL(new RegExp(`${fixture.tripId}/?$`, 'i'));
  await expectMountedWorkspace(page);
  if (await page.evaluate(() => window.matchMedia('(max-width: 640px)').matches)) {
    await page.getByRole('navigation', { name: 'Trip editor sections' }).getByRole('button', { name: 'Regions' }).click();
    await page.getByRole('button', { name: 'Expand' }).click();
  }
}

/** Locates one fixture Place by its exact captured identity. */
function placeRow(page: Page, placeId: string) {
  return page.locator(`[data-place-id="${placeId}"]`);
}

/** Locates one rendered Segment by its captured identity. */
function segmentRow(page: Page, segmentId: string) {
  return page.locator(`.trip-editor-segment-row[data-segment-id="${segmentId}"]`);
}

/** Locates one rendered Region by its captured identity. */
function regionCard(page: Page, regionId: string) {
  return page.locator(`[data-region-id="${regionId}"]`);
}

/** Confirms cancellation left the exact Place persisted on the authenticated editor endpoint. */
async function expectPersistedPlace(page: Page, placeId: string): Promise<void> {
  const response = await page.request.get(absoluteUrl(editorApiPath), { headers: { Accept: 'application/json' } });
  expect(response.ok()).toBeTruthy();
  const state = await response.json() as { placesById: Record<string, unknown> };
  expect(state.placesById[placeId]).toBeTruthy();
}

/** Confirms one exact Place disappeared from canonical state. */
async function expectDeletedPlace(page: Page, placeId: string): Promise<void> {
  const response = await page.request.get(absoluteUrl(editorApiPath), { headers: { Accept: 'application/json' } });
  expect(response.ok()).toBeTruthy();
  const state = await response.json() as { placesById: Record<string, unknown> };
  expect(state.placesById[placeId]).toBeUndefined();
}

/** Deletes through the visible editor and validates the warning counts. */
async function deletePlace(page: Page, target: LifecycleFixture['mixedPlace']): Promise<void> {
  await placeRow(page, target.id).getByRole('button', { name: 'Edit' }).click();
  await page.getByRole('button', { name: 'Delete', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Delete place?' });
  await expect(dialog).toHaveAccessibleDescription(
    `This deletes ${target.endpointSegments} connected segment(s) and updates ${target.waypointOnlySegments} waypoint route(s).`
  );
  await dialog.getByRole('button', { name: 'Delete', exact: true }).click();
}

/** Records exact lifecycle DELETE requests without replacing application traffic. */
function collectDeleteRequests(page: Page, targetId: string): () => Array<{ confirmation?: string }> {
  const requests: Array<{ confirmation?: string }> = [];
  page.on('request', request => {
    if (request.method() === 'DELETE' && request.url().endsWith(`/places/${targetId}`)) {
      requests.push({ confirmation: request.headers()['x-wayfarer-dependency-confirmation'] });
    }
  });
  return () => requests;
}

/** Mutates only the captured fixture association after the first warning. */
function applyFixtureDrift(): void {
  const manifestPath = process.env.WAYFARER_E2E_LIFECYCLE_FIXTURE!;
  const helper = process.env.WAYFARER_E2E_LIFECYCLE_HELPER!;
  if (!helper) throw new Error('WAYFARER_E2E_LIFECYCLE_HELPER is required for stale-confirmation coverage.');
  execFileSync('dotnet', [helper, 'drift', manifestPath], { stdio: 'pipe', env: process.env });
}

/** Invokes one exact fixture helper mutation without exposing a production endpoint. */
function runFixtureHelper(command: string): void {
  const manifestPath = process.env.WAYFARER_E2E_LIFECYCLE_FIXTURE!;
  const helper = process.env.WAYFARER_E2E_LIFECYCLE_HELPER!;
  if (!helper) throw new Error('WAYFARER_E2E_LIFECYCLE_HELPER is required for lifecycle fixture control.');
  execFileSync('dotnet', [helper, command, manifestPath], { stdio: 'pipe', env: process.env });
}

/** Stops only the run-owned PostgreSQL cluster used by the current browser fixture. */
function stopPostgres(): void {
  const control = requiredEnvironment('WAYFARER_E2E_PG_CTL');
  const data = requiredEnvironment('WAYFARER_E2E_POSTGRES_DATA');
  execFileSync(control, ['-D', data, '-m', 'fast', 'stop'], { stdio: 'pipe', timeout: 30_000 });
}

/** Restarts only the run-owned PostgreSQL cluster after deterministic failure evidence. */
function startPostgres(): void {
  const control = requiredEnvironment('WAYFARER_E2E_PG_CTL');
  const data = requiredEnvironment('WAYFARER_E2E_POSTGRES_DATA');
  const log = requiredEnvironment('WAYFARER_E2E_POSTGRES_LOG');
  const port = requiredEnvironment('WAYFARER_E2E_POSTGRES_PORT');
  execFileSync(control, ['-D', data, '-l', log, '-o', `-p ${port} -h 127.0.0.1`, 'start'], { stdio: 'ignore', timeout: 30_000 });
}

/** Probes only the run-owned PostgreSQL TCP port with a bounded socket attempt. */
async function postgresPortIsOpen(): Promise<boolean> {
  const port = Number(requiredEnvironment('WAYFARER_E2E_POSTGRES_PORT'));
  return await new Promise(resolve => {
    const socket = net.createConnection({ host: '127.0.0.1', port });
    const finish = (open: boolean): void => { socket.destroy(); resolve(open); };
    socket.setTimeout(1_000);
    socket.once('connect', () => finish(true));
    socket.once('error', () => finish(false));
    socket.once('timeout', () => finish(false));
  });
}

/** Returns a required run-owned orchestration value without printing it. */
function requiredEnvironment(name: string): string {
  const value = process.env[name];
  if (!value) throw new Error(`${name} is required for lifecycle provider-failure coverage.`);
  return value;
}

/** Exercises a contained Place warning and cancel focus at the active phone viewport. */
async function assertPhonePlaceWarning(page: Page, target: LifecycleFixture['phoneFailurePlace'], viewport: { width: number; height: number }): Promise<void> {
  await placeRow(page, target.id).getByRole('button', { name: 'Edit' }).click();
  await page.getByRole('button', { name: 'Delete', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Delete place?' });
  await assertContainedDialog(dialog, viewport);
  await expect(dialog).toContainText('connected segment(s)');
  await dialog.getByRole('button', { name: 'Keep place' }).click();
  await expect(page.locator('.trip-editor-workspace')).toBeFocused();
}

/** Exercises Region counts and controls without consuming the reusable phone fixture. */
async function assertPhoneRegionWarning(page: Page, target: LifecycleFixture['phoneRegion'], viewport: { width: number; height: number }): Promise<void> {
  const card = regionCard(page, target.id);
  await card.locator('.trip-editor-region-card__header').getByRole('button', { name: 'Edit' }).click();
  await page.getByRole('button', { name: 'Delete', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Delete region?' });
  await assertContainedDialog(dialog, viewport);
  await expect(dialog).toContainText(`${target.deletedPlaces} place(s), ${target.deletedAreas} area(s)`);
  await dialog.getByRole('button', { name: 'Keep region' }).click();
  await expect(page.locator('.trip-editor-workspace')).toBeFocused();
}

/** Proves stale-warning refresh at phone width while retaining the reusable target. */
async function assertPhoneStaleRefresh(page: Page, viewport: { width: number; height: number }): Promise<void> {
  runFixtureHelper('reset-drift');
  const target = fixture.phoneStalePlace;
  await placeRow(page, target.id).getByRole('button', { name: 'Edit' }).click();
  await page.getByRole('button', { name: 'Delete', exact: true }).click();
  const first = page.getByRole('dialog', { name: 'Delete place?' });
  await expect(first).toContainText('updates 1 waypoint route(s)');
  runFixtureHelper('phone-drift');
  await first.getByRole('button', { name: 'Delete', exact: true }).click();
  const refreshed = page.getByRole('dialog', { name: 'Dependencies changed' });
  await assertContainedDialog(refreshed, viewport);
  await expect(refreshed).toContainText('updates 2 waypoint route(s)');
  await refreshed.getByRole('button', { name: 'Keep place' }).click();
  await expect(placeRow(page, target.id)).toBeVisible();
  await expect(page.locator('.trip-editor-workspace')).toBeFocused();
}

/** Proves provider rollback and retry availability at phone width without deleting the target. */
async function assertPhoneFailureRollback(page: Page, viewport: { width: number; height: number }): Promise<void> {
  const target = fixture.phoneFailurePlace;
  await placeRow(page, target.id).getByRole('button', { name: 'Edit' }).click();
  const deleteButton = page.getByRole('button', { name: 'Delete', exact: true });
  await deleteButton.click();
  const dialog = page.getByRole('dialog', { name: 'Delete place?' });
  await assertContainedDialog(dialog, viewport);
  stopPostgres();
  try {
    const response = page.waitForResponse(candidate => candidate.request().method() === 'DELETE' && candidate.url().endsWith(`/places/${target.id}`));
    await dialog.getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(deleteButton).toBeDisabled();
    expect((await response).status()).toBe(500);
  } finally {
    startPostgres();
  }
  await expect(placeRow(page, target.id)).toBeVisible();
  await deleteButton.click();
  const retry = page.getByRole('dialog', { name: 'Delete place?' });
  await assertContainedDialog(retry, viewport);
  await retry.getByRole('button', { name: 'Keep place' }).click();
}

/** Asserts required content and touch controls remain inside the visual viewport. */
async function assertContainedDialog(dialog: Locator, viewport: { width: number; height: number }): Promise<void> {
  await expect(dialog).toBeVisible();
  const box = await dialog.boundingBox();
  expect(box).not.toBeNull();
  expect(box!.x).toBeGreaterThanOrEqual(0);
  expect(box!.x + box!.width).toBeLessThanOrEqual(viewport.width);
  expect(box!.y).toBeGreaterThanOrEqual(0);
  expect(box!.y + box!.height).toBeLessThanOrEqual(viewport.height);
  for (const button of [dialog.getByRole('button').first(), dialog.getByRole('button').last()]) {
    await expect(button).toBeVisible();
    const buttonBox = await button.boundingBox();
    expect(buttonBox!.height).toBeGreaterThanOrEqual(38);
  }
}

/** Proves neither the document nor the mounted editor introduces phone-width overflow. */
async function expectNoHorizontalOverflow(page: Page): Promise<void> {
  expect(await page.evaluate(() => ({ document: document.documentElement.scrollWidth - document.documentElement.clientWidth, editor: document.querySelector('.trip-editor-workspace')!.scrollWidth - document.querySelector('.trip-editor-workspace')!.clientWidth })))
    .toEqual({ document: 0, editor: 0 });
}

/** Proves focus remains inside a visible, useful editor control after a destructive outcome. */
async function expectMeaningfulFocus(page: Page): Promise<void> {
  await expect.poll(() => page.evaluate(() => {
    const active = document.activeElement as HTMLElement | null;
    return Boolean(active && active !== document.body && active.getClientRects().length > 0);
  })).toBeTruthy();
}
