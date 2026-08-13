import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { expect, test, type Locator, type Page, type Route } from '@playwright/test';
import { absoluteUrl, editorApiPath, editorPath, expectMountedWorkspace, loadEditorStateFixture, pathRegex, signIn } from './tripEditorTestUtils';

type Fixture = {
  cleanupSegmentId: string;
  responsiveSegmentId: string;
  failedSaveSegmentId: string;
  waypointId: string;
};

let fixture: Fixture;

test.describe('#409 final route-work product workflows', () => {
  test.beforeAll(() => {
    fixture = JSON.parse(readFileSync(required('WAYFARER_E2E_WAYPOINT_FIXTURE'), 'utf8')) as Fixture;
  });
  test('switch, drawer close, reopen, and workspace teardown clean exact W ownership', async ({ page }) => {
    test.setTimeout(120_000);
    await openWorkspace(page);
    await openSegment(page, fixture.cleanupSegmentId);
    await makeRouteWorkDirty(page);
    const work = routeWork(page);
    const originalHandles = await workHandles(page).count();

    const otherRow = page.locator(`[data-segment-id="${fixture.responsiveSegmentId}"]`);
    await otherRow.locator('.trip-editor-list-button').click();
    const mapDiscard = page.getByRole('dialog', { name: 'Discard map editing changes?' });
    await mapDiscard.getByRole('button', { name: 'Keep editing' }).click();
    await expect(work).toBeVisible();
    await expect(workHandles(page)).toHaveCount(originalHandles);
    await expect(work.getByRole('listitem').filter({ hasText: /^Via 1 —/ })).toHaveAttribute('data-route-point-index', '3');

    await otherRow.locator('.trip-editor-list-button').click();
    await mapDiscard.getByRole('button', { name: 'Discard' }).click();
    await expect(work).toHaveCount(0);
    await expect(workHandles(page)).toHaveCount(0);
    await expect(page.locator('[data-route-owner="work"]')).toHaveCount(0);
    await expect(page.locator('#trip-editor-segment-form')).toBeVisible();

    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByRole('button', { name: 'Draw/Edit Route' }).press('Enter');
    await makeActiveRouteWorkDirty(page);
    await page.getByRole('button', { name: 'Cancel', exact: true }).last().click();
    await mapDiscard.getByRole('button', { name: 'Keep editing' }).click();
    await expect(work).toBeVisible();
    await page.getByRole('button', { name: 'Cancel', exact: true }).last().click();
    await mapDiscard.getByRole('button', { name: 'Discard' }).click();
    await expect(workHandles(page)).toHaveCount(0);

    const expand = page.getByRole('button', { name: 'Expand', exact: true });
    await expand.click();
    await page.getByRole('button', { name: 'Draw/Edit Route' }).press('Enter');
    await expect(workHandles(page)).toHaveCount(originalHandles - 1);
    await page.getByRole('button', { name: /toggle navigation/i }).click();
    await page.getByRole('link', { name: 'Trips', exact: true }).click();
    await expect(page.locator('.trip-editor-workspace')).toHaveCount(0);
    await expect(workHandles(page)).toHaveCount(0);
    await expect(page.locator('[data-route-owner="work"]')).toHaveCount(0);
  });

  test('active W remains contained and operable at every required layout', async ({ page }) => {
    test.setTimeout(120_000);
    const layouts = [
      { name: 'desktop', width: 1280, height: 900, scale: 1 },
      { name: 'intermediate', width: 760, height: 900, scale: 1 },
      { name: '390x844', width: 390, height: 844, scale: 1 },
      { name: '430x932', width: 430, height: 932, scale: 1 },
      { name: '200-percent', width: 1280, height: 900, scale: 2 }
    ];
    const cdp = await page.context().newCDPSession(page);
    let authenticated = false;
    try {
      for (const layout of layouts) {
        await page.setViewportSize({ width: layout.width, height: layout.height });
        await cdp.send('Emulation.setPageScaleFactor', { pageScaleFactor: layout.scale });
        if (!authenticated) {
          await openWorkspace(page);
          authenticated = true;
        } else {
          expect((await page.goto(absoluteUrl(editorPath), { waitUntil: 'domcontentloaded' }))?.ok()).toBeTruthy();
          await expectMountedWorkspace(page);
        }
        await openSegment(page, fixture.responsiveSegmentId);
        if (layout.width <= 430) await page.getByRole('button', { name: 'Draw/Edit Route' }).press('Enter');
        else await page.getByRole('button', { name: 'Draw/Edit Route' }).click();
        const work = routeWork(page);
        for (const control of [
          work.getByRole('button', { name: 'Done' }), work.getByRole('button', { name: 'Cancel' }),
          work.getByRole('button', { name: 'Clear Route' }), work.getByLabel('Longitude').first(),
          work.getByLabel('Latitude').first(), work.getByRole('button', { name: /Remove Route point/ }).first(),
          work.getByRole('button', { name: /Insert route point after/ }).first()
        ]) await expect(control, layout.name).toBeVisible();
        const overflow = await page.evaluate(() => ({
          x: document.documentElement.scrollWidth - document.documentElement.clientWidth,
          twoDimensional: document.documentElement.scrollHeight > document.documentElement.clientHeight
            && document.documentElement.scrollWidth > document.documentElement.clientWidth
        }));
        expect(overflow, layout.name).toEqual({ x: 0, twoDimensional: false });
        for (const button of await work.getByRole('button').all()) {
          const box = await button.boundingBox();
          expect(box?.height ?? 0, `${layout.name}: ${await button.textContent()}`).toBeGreaterThanOrEqual(38);
        }
        await work.getByRole('button', { name: 'Done' }).click();
        if (layout !== layouts.at(-1)) await page.goto('about:blank');
      }
    } finally {
      await cdp.send('Emulation.setPageScaleFactor', { pageScaleFactor: 1 });
      await cdp.detach();
    }
  });

  test('failed Save retains accepted W draft, prevents duplicates, retries, and rereads provider state', async ({ page }) => {
    test.setTimeout(120_000);
    await openWorkspace(page);
    const initial = (await editorState(page)).segmentsById[fixture.failedSaveSegmentId];
    await openSegment(page, fixture.failedSaveSegmentId);
    await makeRouteWorkDirty(page);
    await routeWork(page).getByRole('button', { name: 'Done' }).click();
    const expected = [[23.70, 37.97], [23.71, 37.975], [23.72, 37.98], [23.74, 37.99], [23.78, 38.01]];
    const save = page.getByRole('button', { name: 'Save Segment' });
    let failedProposal: Record<string, any> | null = null;
    const failurePath = pathRegex(`${editorApiPath}/segments/${fixture.failedSaveSegmentId}`);
    const failOnce = async (route: Route): Promise<void> => {
      if (route.request().method() !== 'PUT') return route.fallback();
      failedProposal = route.request().postDataJSON() as Record<string, any>;
      await expect(page.getByRole('status').filter({ hasText: 'Saving' }).first()).toBeVisible();
      await expect(save).toBeDisabled();
      await route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Deterministic #407 provider failure.' }) });
    };
    await page.route(failurePath, failOnce);
    try {
      const response = page.waitForResponse(candidate => candidate.request().method() === 'PUT' && candidate.url().endsWith(`/segments/${fixture.failedSaveSegmentId}`));
      await save.click();
      expect((await response).status()).toBe(500);
    } finally {
      await page.unroute(failurePath, failOnce);
    }
    await expect(page.getByText('Unsaved route · 5 custom route points')).toBeVisible();
    await expect(routeWork(page)).toHaveCount(0);
    await expect(page.locator(`[data-segment-id="${fixture.failedSaveSegmentId}"][data-route-owner="saved"]`)).toHaveCount(1);
    const afterFailure = (await editorState(page)).segmentsById[fixture.failedSaveSegmentId];
    expect(afterFailure.route.coordinates).toEqual(initial.route.coordinates);
    expect(afterFailure.waypointRouteVertexIndices).toEqual([2]);
    expect(failedProposal?.aggregateConcurrencyToken).toBeTruthy();

    const retryRequest = page.waitForRequest(candidate => candidate.method() === 'PUT' && candidate.url().endsWith(`/segments/${fixture.failedSaveSegmentId}`));
    const success = page.waitForResponse(candidate => candidate.request().method() === 'PUT' && candidate.status() === 200 && candidate.url().endsWith(`/segments/${fixture.failedSaveSegmentId}`));
    await save.click();
    expect((await retryRequest).postDataJSON().aggregateConcurrencyToken).toBe(failedProposal?.aggregateConcurrencyToken);
    const successfulResponse = await success;
    const authoritative = await successfulResponse.json();
    expect(authoritative.data.aggregateConcurrencyToken).not.toBe(failedProposal?.aggregateConcurrencyToken);
    const adopted = (await editorState(page)).segmentsById[fixture.failedSaveSegmentId];
    expect(adopted.route.coordinates).toEqual(expected);
    expect(adopted.waypointRouteVertexIndices).toEqual([3]);
    expect(adopted.aggregateConcurrencyToken).not.toBe(initial.aggregateConcurrencyToken);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await expectMountedWorkspace(page);
    const reread = (await editorState(page)).segmentsById[fixture.failedSaveSegmentId];
    expect(reread.route.coordinates).toEqual(expected);
    expect(reread.waypointRouteVertexIndices).toEqual([3]);
    fixtureControl('verify-failed-save');
  });
});

/** Opens the authenticated persisted fixture workspace. */
async function openWorkspace(page: Page): Promise<void> {
  await signIn(page);
  expect((await page.goto(absoluteUrl(editorPath), { waitUntil: 'domcontentloaded' }))?.ok()).toBeTruthy();
  await expectMountedWorkspace(page);
}

/** Opens one exact Segment through its visible list control. */
async function openSegment(page: Page, id: string): Promise<void> {
  await page.locator(`[data-segment-id="${id}"] .trip-editor-list-button`).click();
  await expect(page.locator('#trip-editor-segment-form')).toBeVisible();
}

/** Creates one deterministic dirty W proposal through accessible route-point controls. */
async function makeRouteWorkDirty(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Draw/Edit Route' }).click();
  await makeActiveRouteWorkDirty(page);
}

/** Makes the already-open route work dirty through its ordered point editor. */
async function makeActiveRouteWorkDirty(page: Page): Promise<void> {
  const work = routeWork(page);
  await work.getByRole('listitem').filter({ hasText: /^Start —/ }).getByRole('button', { name: /Insert route point after/ }).click();
  const point = work.locator('[data-route-point-index="1"]');
  await point.getByLabel('Longitude').fill('23.71');
  await point.getByLabel('Latitude').fill('37.975');
  await page.keyboard.press('Tab');
}

/** Selects the public route-work region instead of private component state. */
const routeWork = (page: Page): Locator => page.getByRole('region', { name: 'Map work' });

/** Selects only visible route-work-owned anonymous handles. */
const workHandles = (page: Page): Locator => page.locator('.segment-route-work-handle');

/** Reads the mounted server-authored editor state for saved-authority comparisons. */
const editorState = async (page: Page): Promise<Record<string, any>> => await loadEditorStateFixture(page) as Record<string, any>;

/** Runs an existing fixture-scoped independent provider verification command. */
function fixtureControl(command: string): void {
  execFileSync('dotnet', [required('WAYFARER_E2E_WAYPOINT_HELPER'), command, required('WAYFARER_E2E_WAYPOINT_FIXTURE')], { env: process.env });
}

/** Returns one required runner-owned value without exposing it. */
function required(name: string): string {
  const value = process.env[name];
  if (!value) throw new Error(`${name} is required for #409 route-work completion coverage.`);
  return value;
}
