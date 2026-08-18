import { expect, test, type Page, type Route } from '@playwright/test';
import { absoluteUrl, config, editorApiPath, editorPath, expectMountedWorkspace, pathRegex, signIn } from './tripEditorTestUtils';

test.describe.serial('bounded external routing workflow', () => {
  test('mounted two-Segment workflow preserves independent drafts through proposal lifecycle', async ({ page }) => {
    await signIn(page);
    const fixtureResponse = await page.request.get(absoluteUrl(editorApiPath), { headers: { Accept: 'application/json' } });
    test.skip(!fixtureResponse.ok(), `The configured mounted fixture is unavailable (${fixtureResponse.status()}).`);
    const original = await fixtureResponse.json() as Record<string, any>;
    test.skip(original.segmentOrder.length < 2, 'The configured mounted fixture has fewer than two Segments.');
    const [fallbackId, customId] = original.segmentOrder.slice(0, 2) as string[];
    const state = structuredClone(original);
    state.options.transportModes = distinctModes(state.options.transportModes);
    for (const id of [fallbackId, customId]) state.segmentsById[id].externalRouting = capability();
    state.segmentsById[fallbackId].route = null;
    state.segmentsById[fallbackId].hasCustomRoute = false;
    state.segmentsById[customId].route = state.segmentsById[customId].effectiveRoute;
    state.segmentsById[customId].hasCustomRoute = true;

    await page.route(pathRegex(editorApiPath), async route => {
      if (route.request().method() === 'GET') await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
      else await route.fallback();
    });
    let generationRequests = 0;
    let failNext = false;
    let pendingRelease: (() => void) | null = null;
    await page.route(/\/api\/trip-editor\/[^/]+\/segments\/[^/]+\/route-proposals$/, async route => {
      generationRequests++;
      if (failNext) {
        failNext = false;
        await route.fulfill({ status: 422, contentType: 'application/json', body: JSON.stringify({ code: 'provider-unavailable' }) });
        return;
      }
      if (pendingRelease) await new Promise<void>(resolve => { const prior = pendingRelease; pendingRelease = () => { prior?.(); resolve(); }; });
      const segmentId = route.request().url().split('/segments/')[1].split('/')[0];
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(proposal(state, segmentId)) }).catch(() => {});
    });
    await page.route(/\/route-proposals\/[^/]+\/accept$/, async route => {
      const body = route.request().postDataJSON();
      const segmentId = route.request().url().split('/segments/')[1].split('/')[0];
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
        proposalId: route.request().url().split('/route-proposals/')[1].split('/')[0], segmentId,
        geometry: body.geometry, waypointIndices: body.waypointIndices
      }) });
    });
    await page.route(new RegExp(`${escape(fixedSegmentEndpoint(customId))}$`), route => fulfillSegmentSave(route, state, customId));
    await page.route(new RegExp(`${escape(fixedSegmentEndpoint(fallbackId))}$`), route => fulfillSegmentSave(route, state, fallbackId));

    await page.goto(absoluteUrl(editorPath), { waitUntil: 'domcontentloaded' });
    await expect(page).toHaveURL(pathRegex(editorPath));
    await expectMountedWorkspace(page);

    await openSegment(page, fallbackId);
    await expectDisclosure(page);
    await page.getByRole('button', { name: 'Generate routed path' }).click();
    await expect(page.getByText('Proposal ready for preview.')).toBeVisible();
    await page.getByRole('button', { name: 'Discard proposal' }).click();
    await expect(page.getByText('Proposal ready for preview.')).toHaveCount(0);

    await page.getByRole('button', { name: 'Generate routed path' }).click();
    await page.getByRole('button', { name: 'Accept proposal' }).click();
    await expect(page.getByRole('button', { name: 'Clear Route' })).toBeEnabled();
    await expect(page.getByRole('button', { name: 'Draw/Edit Route' })).toBeEnabled();
    await page.getByRole('button', { name: 'Cancel' }).click();

    await openSegment(page, customId);
    await page.getByRole('button', { name: 'Replace with routed path' }).click();
    await page.getByRole('button', { name: 'Generate replacement' }).click();
    await expect(page.getByText('Proposal ready for preview.')).toBeVisible();
    await page.getByRole('button', { name: 'Accept proposal' }).click();
    await page.getByRole('button', { name: 'Save Segment' }).click();
    await expect(page.getByRole('button', { name: 'Reset' })).toBeDisabled();
    await page.getByRole('button', { name: 'Clear Route' }).click();
    await expect(page.getByRole('button', { name: 'Reset' })).toBeEnabled();
    await page.getByRole('button', { name: 'Reset' }).click();
    await expect(page.getByRole('button', { name: 'Clear Route' })).toBeEnabled();

    await page.getByRole('button', { name: 'Replace with routed path' }).click();
    await page.getByRole('button', { name: 'Generate replacement' }).click();
    await expect(page.getByText('Proposal ready for preview.')).toBeVisible();
    const beforeProfileChange = generationRequests;
    const modeSelect = page.getByText('Transport mode').locator('..').locator('select');
    await modeSelect.selectOption({ index: 2 });
    await expect(page.getByText('Proposal ready for preview.')).toHaveCount(0);
    await expect(page.getByText('Save the transport-profile change before generating')).toBeVisible();
    expect(generationRequests).toBe(beforeProfileChange);
    await page.getByRole('button', { name: 'Save Segment' }).click();
    await page.getByRole('button', { name: 'Replace with routed path' }).click();
    await page.getByRole('button', { name: 'Generate replacement' }).click();
    await expect(page.getByText('Proposal ready for preview.')).toBeVisible();
    await page.getByRole('button', { name: 'Discard proposal' }).click();

    failNext = true;
    await page.getByRole('button', { name: 'Replace with routed path' }).click();
    await page.getByRole('button', { name: 'Generate replacement' }).click();
    await expect(page.getByRole('alert')).toContainText('draft is unchanged');
    await expect(page.getByRole('button', { name: 'Clear Route' })).toBeEnabled();

    pendingRelease = () => {};
    await page.getByRole('button', { name: 'Replace with routed path' }).click();
    await page.getByRole('button', { name: 'Generate replacement' }).click();
    await expect(page.getByRole('button', { name: 'Cancel generation' })).toBeVisible();
    await page.getByRole('button', { name: 'Cancel generation' }).click();
    pendingRelease?.();
    pendingRelease = null;
    await expect(page.getByRole('button', { name: 'Clear Route' })).toBeEnabled();
  });
});

const capability = () => ({ available: true, unavailableReason: null, providerDisplayName: 'Controlled OSRM',
  mappedProfileLabel: 'Controlled profile', disclosure: 'Ordered anchor coordinates are sent to Controlled OSRM.',
  attribution: 'Controlled routing data' });

function proposal(state: Record<string, any>, segmentId: string): Record<string, any> {
  const segment = state.segmentsById[segmentId];
  const placeIds = [segment.fromPlaceId, ...segment.waypointPlaceIds, segment.toPlaceId];
  const geometry = placeIds.map((id: string) => state.placesById[id].location);
  return { proposalId: crypto.randomUUID(), segmentId, geometry,
    waypointIndices: geometry.map((_: unknown, index: number) => index), protectedContext: 'controlled-context',
    expiresAt: new Date(Date.now() + 600000).toISOString() };
}

async function fulfillSegmentSave(route: Route, state: Record<string, any>, segmentId: string): Promise<void> {
  if (route.request().method() !== 'PUT') { await route.fallback(); return; }
  const request = route.request().postDataJSON();
  const current = state.segmentsById[segmentId];
  const saved = { ...current, ...request, aggregateConcurrencyToken: `${current.aggregateConcurrencyToken}-saved`,
    hasCustomRoute: request.route !== null, externalRouting: current.externalRouting };
  state.segmentsById[segmentId] = saved;
  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ success: true, data: saved,
    affected: { metadata: null, regions: [], regionOrder: null, places: [], placeOrdersByRegionId: {}, areas: [],
      areaOrdersByRegionId: {}, segments: [saved], segmentOrder: null, tags: [], tagOrder: null,
      visitProgress: null, options: null },
    deletedIds: { regions: [], places: [], areas: [], segments: [], tags: [] }, warnings: [] }) });
}

async function openSegment(page: Page, id: string): Promise<void> {
  const form = page.locator('#trip-editor-segment-form');
  if (await form.isVisible().catch(() => false)) await page.getByRole('button', { name: 'Cancel' }).click();
  await page.locator(`[data-segment-id="${id}"] .trip-editor-list-button`).click();
  await expect(form).toBeVisible();
}

async function expectDisclosure(page: Page): Promise<void> {
  await expect(page.getByText('Controlled OSRM')).toBeVisible();
  await expect(page.getByText('Ordered anchor coordinates are sent to Controlled OSRM.')).toBeVisible();
}

const fixedSegmentEndpoint = (id: string): string => `${editorApiPath}/segments/${id}`;
const escape = (value: string): string => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const distinctModes = (modes: Array<Record<string, any>>): Array<Record<string, any>> => modes.length > 1 ? modes :
  [...modes, { value: 'controlled-alternate', label: 'Controlled alternate', speedKmh: 10 }];
