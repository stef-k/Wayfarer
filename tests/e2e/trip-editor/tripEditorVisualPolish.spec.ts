import { expect, test, type Locator, type Page, type Route, type TestInfo } from '@playwright/test';
import {
  absoluteUrl,
  editorApiPath,
  expectMountedWorkspace,
  loadEditorStateFixture,
  signIn,
  editorPath
} from './tripEditorTestUtils';

type MutableEditorState = Record<string, any>;

const regionId = '00000000-0000-0000-0000-000000275101';
const placeId = '00000000-0000-0000-0000-000000275201';
const secondPlaceId = '00000000-0000-0000-0000-000000275202';
const areaId = '00000000-0000-0000-0000-000000275301';
const segmentId = '00000000-0000-0000-0000-000000275401';
const visitId = '00000000-0000-0000-0000-000000275501';
const editorApiMatcher = new RegExp(`${editorApiPath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?:/.*)?$`, 'i');
const matrixViewports = [
  { name: 'desktop-wide', width: 1440, height: 1000 },
  { name: 'laptop', width: 1280, height: 800 },
  { name: 'tablet', width: 768, height: 1024 },
  { name: 'phone-smoke', width: 390, height: 900 }
];

test.describe.serial('Trip Editor issue 275 visual polish evidence', () => {
  test.setTimeout(180000);

  for (const viewport of matrixViewports) {
    test(`metadata, search, visits, confirmations, and map-work evidence at ${viewport.name}`, async ({ page }, testInfo) => {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      await signIn(page);
      await loadWorkspaceWithVisualFixture(page);

      await setTheme(page, 'light');
      await expectLightThemeTone(page);
      await expectNoPageOverflow(page);
      await capture(page, testInfo, `${viewport.name}-light-docked-metadata`);
      note(testInfo, 'docked metadata, tags/share progress, map navigation toolbar', viewport.name, 'light', 'data-bs-theme', 'pass');

      await openTripEditIfNeeded(page);
      await expandDockedEditor(page);
      await expect(page.getByRole('dialog', { name: /Edit Trip -/ })).toBeVisible();
      await expectDialogFitsViewport(page);
      await capture(page, testInfo, `${viewport.name}-light-expanded-metadata`);
      note(testInfo, 'expanded metadata and rich notes', viewport.name, 'light', 'data-bs-theme', 'pass');

      await setTheme(page, 'dark');
      await expectDialogFitsViewport(page);
      await capture(page, testInfo, `${viewport.name}-dark-expanded-metadata`);
      note(testInfo, 'expanded metadata dark theme', viewport.name, 'dark', 'data-bs-theme', 'pass');
      await page.getByRole('dialog', { name: /Edit Trip -/ }).getByRole('button', { name: 'Dock to sidebar' }).click();

      await setTheme(page, 'dark');
      await openMobileTabIfVisible(page, 'Regions');
      await (await sidebarSearchInput(page)).fill('not-a-visual-match');
      await expect(await sidebarNoMatchText(page)).toBeVisible();
      await capture(page, testInfo, `${viewport.name}-dark-sidebar-no-match`);
      note(testInfo, 'sidebar search no-match state', viewport.name, 'dark', 'data-bs-theme', 'pass');
      await (await sidebarSearchInput(page)).fill('');

      await routeGeocode(page);
      await openMobileTabIfVisible(page, 'Trip');
      await page.getByRole('searchbox', { name: 'Map search' }).fill('visual place');
      await page.getByRole('region', { name: 'Map search' }).getByRole('button', { name: 'Search' }).click();
      await page.getByRole('button', { name: 'Visual Search Place' }).click();
      await capture(page, testInfo, `${viewport.name}-dark-map-search-add`);
      note(testInfo, 'map search/search-add', viewport.name, 'dark', 'data-bs-theme', 'pass');

      await page.getByRole('button', { name: 'Visits' }).click();
      await expect(page.getByRole('dialog', { name: 'Visit progress and history' })).toBeVisible();
      await expectDialogFitsViewport(page);
      await capture(page, testInfo, `${viewport.name}-dark-visit-progress`);
      note(testInfo, 'visit progress/history', viewport.name, 'dark', 'data-bs-theme', 'pass');
      await page.getByRole('dialog', { name: 'Visit progress and history' }).getByRole('button', { name: 'Close' }).click();

      await openPlace(page);
      await capture(page, testInfo, `${viewport.name}-dark-place-edit-docked`);
      note(testInfo, 'child entity edit docked', viewport.name, 'dark', 'data-bs-theme', 'pass');
      await openPlace(page);
      await expandDockedEditor(page, '#trip-editor-place-form');
      await expect(page.getByRole('dialog', { name: /Edit Place -/ })).toBeVisible();
      await expectDialogFitsViewport(page);
      await capture(page, testInfo, `${viewport.name}-dark-place-edit-expanded`);
      note(testInfo, 'child entity edit expanded', viewport.name, 'dark', 'data-bs-theme', 'pass');
      await dockPlaceEditorAfterExpandedEvidence(page);

      await page.getByRole('button', { name: 'Pick on map' }).click();
      await capture(page, testInfo, `${viewport.name}-dark-place-coordinate-map-work`);
      note(testInfo, 'place coordinate map-work', viewport.name, 'dark', 'data-bs-theme', 'pass');
      await clickMap(page, { xRatio: 0.48, yRatio: 0.42 });
      await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Cancel' }).click();
      await expect(page.getByRole('dialog', { name: 'Discard map editing changes?' })).toBeVisible();
      await capture(page, testInfo, `${viewport.name}-dark-map-work-confirm`);
      note(testInfo, 'map-work cancel/discard confirmation', viewport.name, 'dark', 'data-bs-theme', 'pass');
      await page.getByRole('dialog', { name: 'Discard map editing changes?' }).getByRole('button', { name: 'Discard' }).click();

      await openArea(page);
      await page.getByRole('button', { name: 'Draw/Edit Area' }).click();
      await capture(page, testInfo, `${viewport.name}-dark-area-polygon-map-work`);
      note(testInfo, 'area polygon map-work', viewport.name, 'dark', 'data-bs-theme', 'pass');
      await finishMapWorkIfVisible(page);

      await openSegment(page);
      await page.getByRole('button', { name: 'Draw/Edit Route' }).click();
      await capture(page, testInfo, `${viewport.name}-dark-segment-route-map-work`);
      note(testInfo, 'segment route map-work', viewport.name, 'dark', 'data-bs-theme', 'pass');
      await finishMapWorkIfVisible(page);

      await openPlace(page);
      await page.locator('#trip-editor-place-form').getByLabel('Name').fill('Unsaved visual place');
      await page.getByRole('button', { name: 'Close' }).click();
      await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toBeVisible();
      await capture(page, testInfo, `${viewport.name}-dark-dirty-discard-confirm`);
      note(testInfo, 'dirty-discard confirmation', viewport.name, 'dark', 'data-bs-theme', 'pass');
      await page.getByRole('dialog', { name: 'Discard changes?' }).getByRole('button', { name: 'Discard' }).click();

      await openPlace(page);
      await page.getByRole('button', { name: 'Delete', exact: true }).click();
      await expect(page.getByRole('dialog', { name: 'Delete place?' })).toBeVisible();
      await capture(page, testInfo, `${viewport.name}-dark-delete-confirm`);
      note(testInfo, 'delete confirmation', viewport.name, 'dark', 'data-bs-theme', 'pass');
      await page.getByRole('dialog', { name: 'Delete place?' }).getByRole('button', { name: 'Keep place' }).click();

      await page.locator('#trip-editor-place-form').getByLabel('Name').fill('Unsaved navigation guard');
      await openVisits(page);
      await page.getByRole('link', { name: 'Manage visit' }).click();
      await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toBeVisible();
      await capture(page, testInfo, `${viewport.name}-dark-navigation-confirm`);
      note(testInfo, 'navigation confirmation', viewport.name, 'dark', 'data-bs-theme', 'pass');
    });
  }
});

async function loadWorkspaceWithVisualFixture(page: Page): Promise<void> {
  await page.unroute(editorApiMatcher).catch(() => undefined);
  const state = await loadEditorStateFixture(page) as MutableEditorState;
  prepareVisualState(state);
  await page.route(editorApiMatcher, async route => routeReadOnlyEditor(route, state));
  await page.goto(absoluteUrl(editorPath));
  await expectMountedWorkspace(page);
}

async function routeReadOnlyEditor(route: Route, state: MutableEditorState): Promise<void> {
  if (route.request().method() !== 'GET') {
    throw new Error(`Unexpected visual evidence mutation ${route.request().method()} ${route.request().url()}`);
  }

  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
}

function prepareVisualState(state: MutableEditorState): void {
  state.permissions.canEditMetadata = true;
  state.permissions.canEditRegions = true;
  state.permissions.canEditPlaces = true;
  state.permissions.canEditAreas = true;
  state.permissions.canEditSegments = true;
  state.permissions.canReadVisitProgress = true;
  state.metadata.isPublic = true;
  state.metadata.shareProgressEnabled = true;
  state.metadata.progressPublicUrl ||= '/Public/Trips/visual-progress';
  state.tagOrder = ['visual'];
  state.tagsBySlug = { visual: { slug: 'visual', name: 'Visual QA' } };

  state.regionsById[regionId] = regionFixture(state);
  state.regionOrder = [regionId];
  state.placesById[placeId] = placeFixture(state, placeId, 'Visual Place', { latitude: 37.9838, longitude: 23.7275 });
  state.placesById[secondPlaceId] = placeFixture(state, secondPlaceId, 'Visual Second Place', { latitude: 37.99, longitude: 23.74 });
  state.placeOrderByRegionId = { [regionId]: [placeId, secondPlaceId] };
  state.areasById = { [areaId]: areaFixture(state) };
  state.areaOrderByRegionId = { [regionId]: [areaId] };
  state.segmentsById = { [segmentId]: segmentFixture(state) };
  state.segmentOrder = [segmentId];
  state.visitProgress = {
    totalPlaces: 2,
    visitedPlaces: 1,
    percentVisited: 50,
    placeSummariesByPlaceId: {
      [placeId]: { placeId, visitCount: 1, isVisited: true, firstVisitAt: '2026-01-01T08:00:00.000Z', lastVisitAt: '2026-01-01T08:45:00.000Z' },
      [secondPlaceId]: { placeId: secondPlaceId, visitCount: 0, isVisited: false, firstVisitAt: null, lastVisitAt: null }
    },
    historyRows: [{ visitId, placeId, regionId, startedAt: '2026-01-01T08:00:00.000Z', endedAt: '2026-01-01T08:45:00.000Z', durationMinutes: 45 }]
  };
}

function regionFixture(state: MutableEditorState): Record<string, any> {
  return { id: regionId, tripId: state.tripId, name: 'Visual Region', notesHtml: '<p>Region notes</p>', coverImage: null, center: null, displayOrder: 1, isShadow: false, capabilities: editableCapabilities() };
}

function placeFixture(state: MutableEditorState, id: string, name: string, location: Record<string, number>): Record<string, any> {
  return { id, tripId: state.tripId, regionId, name, notesHtml: '<p>Place notes</p>', address: 'Athens, Greece', location, iconName: state.options.iconNames[0] ?? 'marker', markerColor: state.options.markerColorClasses[0] ?? 'bg-blue', displayOrder: 1, visitSummary: { placeId: id, visitCount: id === placeId ? 1 : 0, isVisited: id === placeId, firstVisitAt: null, lastVisitAt: null }, capabilities: editableCapabilities() };
}

function areaFixture(state: MutableEditorState): Record<string, any> {
  return { id: areaId, tripId: state.tripId, regionId, name: 'Visual Area', notesHtml: '<p>Area notes</p>', fillHex: '#0d6efd', geometry: { type: 'Polygon', coordinates: [[[23.72, 37.98], [23.73, 37.98], [23.73, 37.99], [23.72, 37.98]]] }, displayOrder: 1, capabilities: editableCapabilities() };
}

function segmentFixture(state: MutableEditorState): Record<string, any> {
  return { id: segmentId, tripId: state.tripId, fromPlaceId: placeId, toPlaceId: secondPlaceId, mode: state.options.transportModes[0]?.value ?? 'walk', estimatedDistanceKm: 2, estimatedDurationMinutes: 30, notesHtml: '<p>Segment notes</p>', route: { type: 'LineString', coordinates: [[23.7275, 37.9838], [23.74, 37.99]] }, displayOrder: 1, capabilities: editableCapabilities() };
}

function editableCapabilities(): Record<string, boolean> {
  return { canEdit: true, canRename: true, canDelete: true, canReorder: true, canMove: true, canAddChildren: true, canTargetForSearchAdd: true };
}

async function routeGeocode(page: Page): Promise<void> {
  await page.route(/\/api\/trips\/[^/]+\/editor\/geocode\/search/i, async route => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        query: 'visual place',
        attribution: 'Visual evidence provider',
        results: [{ id: 'visual:place', provider: 'visual', name: 'Visual Search Place', displayName: 'Visual Search Place, Athens', address: 'Athens, Greece', category: 'tourism', type: 'attraction', latitude: 37.9715, longitude: 23.7257 }]
      })
    });
  });
}

async function openPlace(page: Page): Promise<void> {
  await openMobileTabIfVisible(page, 'Regions');
  await openEntityEditor(page, `[data-place-id="${placeId}"]`, '#trip-editor-place-form', /Edit Place - Visual Place/);
}

async function openArea(page: Page): Promise<void> {
  await openMobileTabIfVisible(page, 'Regions');
  await openEntityEditor(page, `[data-area-id="${areaId}"]`, '#trip-editor-area-form', /Edit Area - Visual Area/);
}

async function openSegment(page: Page): Promise<void> {
  await openMobileTabIfVisible(page, 'Segments');
  await closeActiveDockedEditorIfNeeded(page);
  await page.locator(`[data-segment-id="${segmentId}"] .trip-editor-list-button`).click();
  await expect(page.getByRole('heading', { name: /Edit Segment -/ })).toBeVisible();
}

async function openVisits(page: Page): Promise<void> {
  await openMobileTabIfVisible(page, 'Trip');
  await page.getByRole('button', { name: 'Visits' }).click();
  await expect(page.getByRole('dialog', { name: 'Visit progress and history' })).toBeVisible();
}

async function sidebarSearchInput(page: Page): Promise<Locator> {
  const mobileRegionsSearch = page.locator('.trip-editor-mobile-drawer__tab[aria-label="Regions tab"]').getByLabel('Sidebar search');
  return await page.getByRole('navigation', { name: 'Trip editor sections' }).isVisible().catch(() => false)
    ? mobileRegionsSearch
    : page.getByLabel('Sidebar search');
}

async function sidebarNoMatchText(page: Page): Promise<Locator> {
  const text = 'No matching regions, places, areas, or segments.';
  return await page.getByRole('navigation', { name: 'Trip editor sections' }).isVisible().catch(() => false)
    ? page.locator('.trip-editor-mobile-drawer__tab[aria-label="Regions tab"]').getByText(text)
    : page.getByText(text);
}

async function dockPlaceEditorAfterExpandedEvidence(page: Page): Promise<void> {
  const dialog = page.getByRole('dialog', { name: /Edit Place -/ });
  if (await dialog.isVisible().catch(() => false)) {
    await dialog.getByRole('button', { name: 'Dock to sidebar' }).click();
    await expect(page.locator('#trip-editor-place-form')).toBeVisible();
    return;
  }

  await openPlace(page);
}

async function finishMapWorkIfVisible(page: Page): Promise<void> {
  const mapWork = page.getByRole('region', { name: 'Map work' });
  if (await mapWork.isVisible().catch(() => false)) {
    await mapWork.getByRole('button', { name: 'Done' }).click();
  }
}

async function openTripEditIfNeeded(page: Page): Promise<void> {
  const editTrip = page.getByRole('button', { name: 'Edit Trip' });
  if (await editTrip.isVisible().catch(() => false)) {
    await editTrip.click();
    await expect(page.getByRole('heading', { name: /Edit Trip -/ })).toBeVisible();
  }
}

async function openEntityEditor(page: Page, rowSelector: string, formSelector: string, heading: RegExp): Promise<void> {
  for (let attempt = 0; attempt < 2; attempt += 1) {
    await closeActiveDockedEditorIfNeeded(page);
    await page.locator(rowSelector).getByRole('button', { name: 'Edit', exact: true }).click({ force: true });
    if (await page.locator(formSelector).isVisible().catch(() => false)) {
      await expect(page.getByRole('heading', { name: heading })).toBeVisible();
      return;
    }
  }

  await expect(page.locator(formSelector)).toBeVisible();
}

async function closeActiveDockedEditorIfNeeded(page: Page): Promise<void> {
  const activeSurface = page.locator('.trip-editor-surface--docked').first();
  if (await activeSurface.isVisible().catch(() => false)) {
    await activeSurface.getByRole('button', { name: 'Close' }).click();
    const discardDialog = page.getByRole('dialog', { name: 'Discard changes?' });
    if (await discardDialog.isVisible().catch(() => false)) {
      await discardDialog.getByRole('button', { name: 'Discard' }).click();
    }
  }
}

async function openMobileTabIfVisible(page: Page, name: 'Trip' | 'Regions' | 'Segments'): Promise<void> {
  const tab = page.getByRole('button', { name, exact: true });
  if (await tab.isVisible().catch(() => false)) {
    await tab.click();
  }
}

function dockedEditor(page: Page): Locator {
  return page.locator('.trip-editor-surface--docked').first();
}

async function expandDockedEditor(page: Page, containedSelector?: string): Promise<void> {
  const surface = containedSelector
    ? page.locator('.trip-editor-surface--docked').filter({ has: page.locator(containedSelector) }).first()
    : dockedEditor(page);
  const button = surface.getByRole('button', { name: 'Expand Editor' });
  await button.scrollIntoViewIfNeeded();
  await button.click();
}

async function clickMap(page: Page, position: { xRatio: number; yRatio: number }): Promise<void> {
  const map = page.getByLabel('Read-only trip map');
  await map.evaluate((element, point) => {
    const box = element.getBoundingClientRect();
    const clientX = box.left + box.width * point.xRatio;
    const clientY = box.top + box.height * point.yRatio;
    for (const type of ['mousedown', 'mouseup', 'click']) {
      element.dispatchEvent(new MouseEvent(type, { bubbles: true, cancelable: true, clientX, clientY, view: window }));
    }
  }, position);
}

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate(value => document.documentElement.setAttribute('data-bs-theme', value), theme);
}

async function expectNoPageOverflow(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => {
    const viewportWidth = document.documentElement.clientWidth;
    const documentOverflow = document.documentElement.scrollWidth - viewportWidth;
    const bodyOverflow = document.body ? document.body.scrollWidth - viewportWidth : 0;
    const stableContainerSelectors = ['#trip-editor-app', '.trip-editor-shell', '.trip-editor-workspace'];
    const containerOverflow = stableContainerSelectors
      .map(selector => {
        const element = document.querySelector<HTMLElement>(selector);
        if (!element) {
          return { selector, overflow: 0 };
        }

        const bounds = element.getBoundingClientRect();
        return {
          selector,
          overflow: Math.max(0, bounds.right - viewportWidth)
        };
      })
      .filter(result => result.overflow > 2);

    return { documentOverflow, bodyOverflow, containerOverflow };
  });

  expect(overflow.documentOverflow, 'Trip Editor should not introduce horizontal document overflow.').toBeLessThanOrEqual(2);
  expect(overflow.bodyOverflow, 'Trip Editor should not introduce horizontal body overflow.').toBeLessThanOrEqual(2);
  expect(overflow.containerOverflow, 'Stable Trip Editor containers should fit within the viewport.').toEqual([]);
}

async function expectLightThemeTone(page: Page): Promise<void> {
  const colors = await page.evaluate(() => {
    const workspace = document.querySelector<HTMLElement>('.trip-editor-workspace');
    const sidebar = document.querySelector<HTMLElement>('.trip-editor-sidebar');
    if (!workspace || !sidebar) {
      return null;
    }

    const toRgb = (value: string): [number, number, number] => {
      const rgbMatch = value.match(/rgba?\((\d+),\s*(\d+),\s*(\d+)/);
      if (rgbMatch) {
        return [Number(rgbMatch[1]), Number(rgbMatch[2]), Number(rgbMatch[3])];
      }

      const srgbMatch = value.match(/color\(srgb\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)/);
      if (!srgbMatch) {
        throw new Error(`Unsupported color ${value}`);
      }

      return [Number(srgbMatch[1]) * 255, Number(srgbMatch[2]) * 255, Number(srgbMatch[3]) * 255];
    };

    const luminance = ([red, green, blue]: [number, number, number]) => 0.2126 * red + 0.7152 * green + 0.0722 * blue;
    const workspaceRgb = toRgb(getComputedStyle(workspace).backgroundColor);
    const sidebarRgb = toRgb(getComputedStyle(sidebar).backgroundColor);
    return {
      workspaceRgb,
      sidebarRgb,
      workspaceLuminance: luminance(workspaceRgb),
      sidebarLuminance: luminance(sidebarRgb)
    };
  });

  expect(colors, 'Trip Editor light theme surfaces should render.').not.toBeNull();
  expect(colors!.workspaceRgb, 'Workspace background should be toned down from pure white.').not.toEqual([255, 255, 255]);
  expect(colors!.workspaceLuminance, 'Workspace should remain lighter than sidebar/editor surfaces.').toBeGreaterThan(colors!.sidebarLuminance);
}

async function expectDialogFitsViewport(page: Page): Promise<void> {
  const dialog = page.locator('.trip-editor-expanded__dialog:visible').first();
  const [box, viewport] = await Promise.all([dialog.boundingBox(), page.viewportSize()]);
  expect(box, 'Expanded dialog should have a rendered box.').not.toBeNull();
  expect(viewport, 'Viewport should be available for dialog fit check.').not.toBeNull();
  expect(box!.x).toBeGreaterThanOrEqual(-1);
  expect(box!.y).toBeGreaterThanOrEqual(-1);
  expect(box!.x + box!.width).toBeLessThanOrEqual(viewport!.width + 1);
  expect(box!.y + box!.height).toBeLessThanOrEqual(viewport!.height + 1);
}

async function capture(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  await expectNoPageOverflow(page);
  await page.screenshot({ fullPage: true, path: testInfo.outputPath('screenshots', `${name}.png`) });
}

function note(testInfo: TestInfo, state: string, viewport: string, theme: string, themeSource: string, result: string): void {
  testInfo.annotations.push({ type: 'issue-275-evidence', description: `${state} | ${viewport} | ${theme} | ${themeSource} | ${result}` });
}
