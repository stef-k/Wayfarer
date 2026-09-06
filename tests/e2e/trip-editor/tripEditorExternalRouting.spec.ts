import { expect, test, type Page, type Route } from '@playwright/test';
import { absoluteUrl, editorApiPath, editorPath, expectMountedWorkspace, pathRegex, signIn } from './tripEditorTestUtils';

// Controlled proposal and Save responses isolate the mounted presentation contract from provider/persistence tests.
test('pending geometry and estimates survive theme changes, Discard and canonical Save', async ({ page }, testInfo) => {
  await page.route(/\/tiles?\//i, route => route.abort());
  await page.route(/https?:\/\/(?!localhost[:/]|127\.0\.0\.1[:/])/, route => route.abort());
  await signIn(page);
  const fixtureResponse = await page.request.get(absoluteUrl(editorApiPath), { headers: { Accept: 'application/json' } });
  expect(fixtureResponse.ok()).toBeTruthy();
  const state = await fixtureResponse.json() as Record<string, any>;
  const [segmentId] = state.segmentOrder as string[];
  expect(segmentId).toBeTruthy();
  const segment = state.segmentsById[segmentId];
  segment.externalRouting = capability();
  segment.route = segment.effectiveRoute;
  segment.hasCustomRoute = true;
  await page.route(pathRegex(editorApiPath), route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) }));
  await page.route(/\/api\/trip-editor\/[^/]+\/segments\/[^/]+\/route-proposals$/, route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(proposal(state, segmentId)) }));
  await page.route(new RegExp(`${escape(fixedSegmentEndpoint(segmentId))}$`), route => fulfillSegmentSave(route, state, segmentId));
  await page.goto(absoluteUrl(editorPath), { waitUntil: 'domcontentloaded' });
  await expectMountedWorkspace(page);
  await openSegment(page, segmentId);
  await expectDisclosure(page);
  const form = page.locator('#trip-editor-segment-form');
  const distance = form.getByLabel('Estimated distance km', { exact: true });
  const duration = form.getByLabel('Estimated duration minutes', { exact: true });
  const before = [await distance.inputValue(), await duration.inputValue()];
  const current = page.locator(`path[data-segment-id="${segmentId}"][data-segment-presentation-owner]`);
  const preview = page.locator('path[data-route-owner="proposal"]');
  const generate = async () => {
    await page.getByLabel('Directions mode').selectOption('drive');
    await page.getByRole('button', { name: 'Replace with routed path' }).click();
    await page.getByRole('button', { name: 'Generate replacement' }).click();
    await expect(preview).toHaveCount(1);
    await expect(preview).toHaveAttribute('stroke-dasharray', '8 6');
    await expect.poll(() => preview.evaluate(node => (node as SVGPathElement).getTotalLength())).toBeGreaterThan(10);
  };
  await generate();
  await expect(current).toHaveCount(1);
  // The controlled proposal deliberately overlaps the current route: both strokes must remain present.
  expect(await preview.getAttribute('d')).toBe(await current.getAttribute('d'));
  expect(await preview.getAttribute('stroke')).not.toBe(await current.getAttribute('stroke'));
  expect(await preview.evaluate(node => node.parentNode?.lastChild === node)).toBeTruthy();
  await expect(distance).toHaveValue(before[0]);
  await expect(duration).toHaveValue(before[1]);
  await expect(page.getByText("Used only to calculate this route. The Segment's transport profile stays unchanged.")).toBeVisible();
  await expect(page.getByText('Review the proposed route and estimates. Save Segment uses this proposal and saves your other Segment changes. Discard proposal keeps your previous route.')).toBeVisible();
  for (const theme of ['light', 'dark']) {
    await page.evaluate(value => document.documentElement.setAttribute('data-bs-theme', value), theme);
    await expect(page.locator('.proposal-estimates dt').first()).toHaveText('Proposed distance');
    await expect(page.locator('.proposal-estimates strong').first()).toHaveText('1.25 km');
    await expect(page.locator('.proposal-estimates dt').last()).toHaveText('Estimated travel time');
    await expect(page.locator('.proposal-estimates strong').last()).toHaveText('6 minutes');
    await expect(page.locator('.proposal-estimates strong').first()).toHaveCSS('font-weight', '700');
    // Retain actual theme screenshots for visual contrast review; do not approximate CSS color-mix backgrounds.
    const accent = await page.locator('.proposal-estimates strong').first().evaluate(node => getComputedStyle(node).color);
    testInfo.annotations.push({ type: `${theme} estimate color`, description: accent });
    await page.screenshot({ path: testInfo.outputPath(`proposal-${theme}.png`), fullPage: true });
  }
  await page.getByRole('button', { name: 'Discard proposal' }).click();
  await expect(preview).toHaveCount(0);
  await expect(current).toHaveCount(1);
  await expect(distance).toHaveValue(before[0]);
  await expect(duration).toHaveValue(before[1]);
  await generate();
  await page.getByRole('button', { name: 'Save Segment' }).click();
  await expect(preview).toHaveCount(0);
  await expect(page.locator('.proposal-estimates')).toHaveCount(0);
  await expect(distance).toHaveValue('1.25');
  await expect(duration).toHaveValue('6');
  await expect(current).toHaveCount(1);
  await expect.poll(() => current.evaluate(node => (node as SVGPathElement).getTotalLength())).toBeGreaterThan(10);
  await expect(page.getByRole('button', { name: 'Save Segment' })).toBeDisabled();
});

const capability = () => ({ available: true, unavailableReason: null, providerDisplayName: 'Geoapify',
  modes: [{ key: 'drive', label: 'Drive' }], mappedProfileLabel: null, disclosure: 'Ordered anchor coordinates are sent to Geoapify.',
  attribution: 'Controlled routing data' });

function proposal(state: Record<string, any>, segmentId: string): Record<string, any> {
  const segment = state.segmentsById[segmentId];
  const placeIds = [segment.fromPlaceId, ...segment.waypointPlaceIds, segment.toPlaceId];
  const geometry = segment.route.coordinates.map(([longitude, latitude]: number[]) => ({ longitude, latitude }));
  return { proposalId: crypto.randomUUID(), segmentId, geometry,
    waypointIndices: placeIds.map((id: string) => geometry.findIndex((point: Record<string, number>) => point.longitude === state.placesById[id].location.longitude && point.latitude === state.placesById[id].location.latitude)), protectedContext: 'controlled-context',
    distanceMetres: 1250, durationSeconds: 360, expiresAt: new Date(Date.now() + 600000).toISOString() };
}

async function fulfillSegmentSave(route: Route, state: Record<string, any>, segmentId: string): Promise<void> {
  if (route.request().method() !== 'PUT') { await route.fallback(); return; }
  const request = route.request().postDataJSON();
  const current = state.segmentsById[segmentId];
  const { proposal: pendingProposal, ...fields } = request;
  if (pendingProposal) expect(pendingProposal.protectedContext).toBe('controlled-context');
  const saved = { ...current, ...fields, aggregateConcurrencyToken: `${current.aggregateConcurrencyToken}-saved`,
    effectiveRoute: request.route, estimatedDistanceKm: 1.25, estimatedDurationMinutes: 6, estimatedDurationSource: 'Automatic',
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
  await expect(page.getByText('Geoapify', { exact: true })).toBeVisible();
  await expect(page.getByText('Ordered anchor coordinates are sent to Geoapify.')).toBeVisible();
}

const fixedSegmentEndpoint = (id: string): string => `${editorApiPath}/segments/${id}`;
const escape = (value: string): string => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
