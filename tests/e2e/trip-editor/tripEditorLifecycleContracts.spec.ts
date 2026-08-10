import fs from 'node:fs';
import { execFileSync } from 'node:child_process';
import { expect, test, type Page } from '@playwright/test';
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
    await expect(deleteButton).toBeFocused();
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
    await card.getByRole('button', { name: 'Edit' }).click();
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
}

/** Locates one fixture Place by its exact captured identity. */
function placeRow(page: Page, placeId: string) {
  return page.locator(`[data-place-id="${placeId}"]`);
}

/** Locates one rendered Segment by its captured identity. */
function segmentRow(page: Page, segmentId: string) {
  return page.locator(`[data-segment-id="${segmentId}"]`);
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

/** Proves focus remains inside a visible, useful editor control after a destructive outcome. */
async function expectMeaningfulFocus(page: Page): Promise<void> {
  await expect.poll(() => page.evaluate(() => {
    const active = document.activeElement as HTMLElement | null;
    return Boolean(active && active !== document.body && active.getClientRects().length > 0);
  })).toBeTruthy();
}
