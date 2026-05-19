import { expect, test, type Locator, type Page, type Route } from '@playwright/test';
import {
  absoluteUrl,
  activeEditorAlert,
  activeEditorCancelButton,
  editorApiPath,
  editorPath,
  expectMountedWorkspace,
  loadEditorStateFixture,
  signIn,
  uniqueName
} from './tripEditorTestUtils';

type EditorState = Record<string, any>;

test.describe.serial('Trip Editor Batch 3 error state contracts', () => {
  test('mocked stale place save failure preserves draft and persisted state', async ({ page }) => {
    await openEditor(page);
    const initial = await loadEditorStateFixture(page) as EditorState;
    const fixture = editablePlaceFixture(initial);
    test.skip(!fixture, 'Configured Trip Editor fixture has no editable place for stale-save feedback coverage.');
    if (!fixture) {
      return;
    }

    await editPlace(page, fixture.place.id);
    const form = page.locator('#trip-editor-place-form');
    const draftName = uniqueName('PW failed place save draft');
    await form.getByLabel('Name').fill(draftName);
    await form.getByLabel('Address').fill('PW failed place save address');

    const staleSave = await mockMutationFailure(page, 'PUT', `${editorApiPath}/places/${fixture.place.id}`, 404);
    await page.getByRole('button', { name: 'Save Place' }).click();

    await expect.poll(staleSave.requests).toBe(1);
    await expect(activeEditorAlert(page)).toContainText('Trip Editor place update returned 404');
    await expectFailedStatus(page);
    await expect(form.getByLabel('Name')).toHaveValue(draftName);
    await expect(form.getByLabel('Address')).toHaveValue('PW failed place save address');
    await expect(placeRow(page, fixture.place.id).locator('.trip-editor-place-row__name')).toHaveText(fixture.place.name);
    await expectPersistedPlace(page, fixture.place.id, fixture.place.name, fixture.place.address);

    await staleSave.unroute();
    await activeEditorCancelButton(page).click();
    await page.getByRole('dialog', { name: 'Discard changes?' }).getByRole('button', { name: 'Discard' }).click();
    await expect(form).toHaveCount(0);
    await expect(placeRow(page, fixture.place.id).locator('.trip-editor-place-row__name')).toHaveText(fixture.place.name);
    await expectPersistedPlace(page, fixture.place.id, fixture.place.name, fixture.place.address);
  });

  test('mocked segment save failure preserves the separate segment draft path', async ({ page }) => {
    await openEditor(page);
    const initial = await loadEditorStateFixture(page) as EditorState;
    const segment = editableSegment(initial);
    test.skip(!segment, 'Configured Trip Editor fixture has no editable segment for failure feedback coverage.');
    if (!segment) {
      return;
    }

    await editSegment(page, segment.id);
    const form = page.locator('#trip-editor-segment-form');
    await form.getByLabel('Estimated distance km').fill('123.45');

    const failure = await mockMutationFailure(page, 'PUT', `${editorApiPath}/segments/${segment.id}`, 500);
    await page.getByRole('button', { name: 'Save Segment' }).click();

    await expect.poll(failure.requests).toBe(1);
    await expect(activeEditorAlert(page)).toContainText('Trip Editor segment update returned 500');
    await expectFailedStatus(page);
    await expect(form.getByLabel('Estimated distance km')).toHaveValue('123.45');
    await expectPersistedSegmentDistance(page, segment.id, segment.estimatedDistanceKm);

    await failure.unroute();
    await activeEditorCancelButton(page).click();
    await page.getByRole('dialog', { name: 'Discard changes?' }).getByRole('button', { name: 'Discard' }).click();
    await expect(form).toHaveCount(0);
  });

  test('delete confirmation appears first and mocked delete failure keeps row and editor state', async ({ page }) => {
    await openEditor(page);
    const initial = await loadEditorStateFixture(page) as EditorState;
    const fixture = editablePlaceFixture(initial);
    test.skip(!fixture, 'Configured Trip Editor fixture has no deletable place for delete failure coverage.');
    if (!fixture) {
      return;
    }

    await editPlace(page, fixture.place.id);
    const form = page.locator('#trip-editor-place-form');
    const failure = await mockMutationFailure(page, 'DELETE', `${editorApiPath}/places/${fixture.place.id}`, 500);

    await page.getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(page.getByRole('dialog', { name: 'Delete place?' })).toBeVisible();
    await expect.poll(failure.requests).toBe(0);
    await page.getByRole('dialog', { name: 'Delete place?' }).getByRole('button', { name: 'Keep place' }).click();
    await expect(page.getByRole('dialog', { name: 'Delete place?' })).toHaveCount(0);
    await expect(form).toBeVisible();
    await expect(placeRow(page, fixture.place.id)).toBeVisible();

    await page.getByRole('button', { name: 'Delete', exact: true }).click();
    await page.getByRole('dialog', { name: 'Delete place?' }).getByRole('button', { name: 'Delete' }).click();

    await expect.poll(failure.requests).toBe(1);
    await expect(activeEditorAlert(page)).toContainText('Trip Editor place delete returned 500');
    await expectFailedStatus(page);
    await expect(form).toBeVisible();
    await expect(placeRow(page, fixture.place.id)).toBeVisible();
    await expectPersistedPlace(page, fixture.place.id, fixture.place.name, fixture.place.address);

    await failure.unroute();
  });

  test('clean and dirty cancel paths leave persisted place state unchanged', async ({ page }) => {
    await openEditor(page);
    const initial = await loadEditorStateFixture(page) as EditorState;
    const fixture = editablePlaceFixture(initial);
    test.skip(!fixture, 'Configured Trip Editor fixture has no editable place for cancel-state coverage.');
    if (!fixture) {
      return;
    }

    await editPlace(page, fixture.place.id);
    await activeEditorCancelButton(page).click();
    await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toHaveCount(0);
    await expect(page.locator('#trip-editor-place-form')).toHaveCount(0);
    await expectPersistedPlace(page, fixture.place.id, fixture.place.name, fixture.place.address);

    await editPlace(page, fixture.place.id);
    const form = page.locator('#trip-editor-place-form');
    await form.getByLabel('Address').fill('PW dirty cancel draft address');
    await activeEditorCancelButton(page).click();
    const discard = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(discard).toBeVisible();
    await discard.getByRole('button', { name: 'Keep editing' }).click();
    await expect(form.getByLabel('Address')).toHaveValue('PW dirty cancel draft address');
    await expectPersistedPlace(page, fixture.place.id, fixture.place.name, fixture.place.address);

    await activeEditorCancelButton(page).click();
    await page.getByRole('dialog', { name: 'Discard changes?' }).getByRole('button', { name: 'Discard' }).click();
    await expect(form).toHaveCount(0);
    await expectPersistedPlace(page, fixture.place.id, fixture.place.name, fixture.place.address);
  });
});

async function openEditor(page: Page): Promise<void> {
  await signIn(page);
  await page.goto(absoluteUrl(editorPath));
  await expectMountedWorkspace(page);
}

async function editPlace(page: Page, placeId: string): Promise<void> {
  await placeRow(page, placeId).getByRole('button', { name: 'Edit', exact: true }).click();
  await expect(page.locator('#trip-editor-place-form')).toBeVisible();
}

async function editSegment(page: Page, segmentId: string): Promise<void> {
  await segmentRow(page, segmentId).locator('.trip-editor-list-button').click();
  await expect(page.locator('#trip-editor-segment-form')).toBeVisible();
}

async function mockMutationFailure(page: Page, method: string, path: string, status: number): Promise<{ requests: () => number; unroute: () => Promise<void> }> {
  let count = 0;
  const matcher = pathMatcher(path);
  const handler = async (route: Route): Promise<void> => {
    if (route.request().method() !== method) {
      await route.fallback();
      return;
    }

    count += 1;
    await route.fulfill({
      status,
      contentType: 'application/problem+json',
      body: JSON.stringify({ title: 'Injected Trip Editor failure', status })
    });
  };
  await page.route(matcher, handler);
  return {
    requests: () => count,
    unroute: async () => {
      await page.unroute(matcher, handler);
    }
  };
}

async function expectFailedStatus(page: Page): Promise<void> {
  const failedStatus = page.locator('.trip-editor-save-state').filter({ hasText: 'Save failed' }).first();
  await expect(failedStatus).toBeVisible();
  await expect(failedStatus).toHaveClass(/text-bg-danger.*trip-editor-save-state--danger/);
}

async function expectPersistedPlace(page: Page, placeId: string, name: string, address: string): Promise<void> {
  const state = await loadEditorStateFixture(page) as EditorState;
  expect(state.placesById[placeId].name).toBe(name);
  expect(state.placesById[placeId].address).toBe(address);
}

async function expectPersistedSegmentDistance(page: Page, segmentId: string, distance: number | null): Promise<void> {
  const state = await loadEditorStateFixture(page) as EditorState;
  expect(state.segmentsById[segmentId].estimatedDistanceKm).toBe(distance);
}

function editablePlaceFixture(state: EditorState): { region: Record<string, any>; place: Record<string, any> } | null {
  for (const regionId of state.regionOrder as string[]) {
    const region = state.regionsById[regionId];
    if (!region || region.isShadow) {
      continue;
    }

    const placeId = (state.placeOrderByRegionId[regionId] as string[] | undefined)?.find(id => {
      const place = state.placesById[id];
      return place?.capabilities?.canEdit && place?.capabilities?.canDelete;
    });
    if (placeId) {
      return { region, place: state.placesById[placeId] };
    }
  }

  return null;
}

function editableSegment(state: EditorState): Record<string, any> | null {
  const segmentId = (state.segmentOrder as string[]).find(id => state.segmentsById[id]?.capabilities?.canEdit && state.segmentsById[id]?.capabilities?.canDelete);
  return segmentId ? state.segmentsById[segmentId] : null;
}

function placeRow(page: Page, placeId: string): Locator {
  return page.locator(`[data-place-id="${placeId}"]`);
}

function segmentRow(page: Page, segmentId: string): Locator {
  return page.locator(`[data-segment-id="${segmentId}"]`);
}

function pathMatcher(path: string): RegExp {
  return new RegExp(`${path.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}/?$`, 'i');
}
