import fs from 'node:fs';
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
};

const fixture = loadLifecycleFixture();

test.describe.serial('Trip Editor #406 real lifecycle contracts', () => {
  test('waypoint-only Place warning is server-backed, cancellable, and accessible', async ({ page }) => {
    await openFixture(page);
    const editButton = placeRow(page, fixture.waypointOnlyPlace.id).getByRole('button', { name: 'Edit' });
    await editButton.click();
    await page.getByRole('button', { name: 'Delete', exact: true }).click();

    const dialog = page.getByRole('dialog', { name: 'Delete place?' });
    await expect(dialog).toBeVisible();
    await expect(dialog).toHaveAccessibleDescription(
      `This deletes ${fixture.waypointOnlyPlace.endpointSegments} connected segment(s) and updates ${fixture.waypointOnlyPlace.waypointOnlySegments} waypoint route(s).`
    );
    await expect(dialog.getByRole('button', { name: 'Keep place' })).toBeFocused();
    await dialog.getByRole('button', { name: 'Keep place' }).click();

    await expect(dialog).toHaveCount(0);
    await expect(placeRow(page, fixture.waypointOnlyPlace.id)).toBeVisible();
    await expect(editButton).toBeFocused();
    await expectPersistedPlace(page, fixture.waypointOnlyPlace.id);
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

/** Confirms cancellation left the exact Place persisted on the authenticated editor endpoint. */
async function expectPersistedPlace(page: Page, placeId: string): Promise<void> {
  const response = await page.request.get(absoluteUrl(editorApiPath), { headers: { Accept: 'application/json' } });
  expect(response.ok()).toBeTruthy();
  const state = await response.json() as { placesById: Record<string, unknown> };
  expect(state.placesById[placeId]).toBeTruthy();
}
