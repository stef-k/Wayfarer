import { expect, test, type Locator, type Page } from '@playwright/test';
import { loadTripEditorConfig } from './tripEditorConfig';

export const config = loadTripEditorConfig();
export const editorPath = `/User/Trip/Edit/${config.tripId}`;
export const workspaceRedirectPath = `/User/Trip/Workspace/${config.tripId}`;
export const editorApiPath = `/api/trips/${config.tripId}/editor`;

const forbiddenSidebarSearchRequest = /nominatim|geosearch|search-add|searchadd|\/search(?:[/?#]|$)/i;

export type EditorTripFixture = {
  regionsById: Record<string, { id: string; name: string; isShadow: boolean }>;
  regionOrder: string[];
  placesById: Record<string, { id: string; name: string; address: string; regionId: string }>;
  placeOrderByRegionId: Record<string, string[]>;
  areasById: Record<string, { id: string; name: string; regionId: string }>;
  areaOrderByRegionId: Record<string, string[]>;
  segmentsById: Record<string, { id: string; mode: string; fromPlaceId: string | null; toPlaceId: string | null }>;
  segmentOrder: string[];
  tagOrder: string[];
  tagsBySlug: Record<string, { name: string }>;
  options: { transportModes: Array<{ value: string; label: string }> };
};

export type SidebarSearchFixture = {
  region: { name: string };
  place: { name: string; regionName: string };
  area: { name: string; regionName: string } | null;
  segment: { query: string; label: string } | null;
};

export type ShadowChildFixture = {
  regionName: string;
  childName: string;
  childKind: 'place' | 'area';
};

// Builds a local URL against the configured ASP.NET dev server.
export function absoluteUrl(path: string): string {
  return `${config.baseUrl}${path}`;
}

// Escapes dynamic fixture text before using it in a regular expression.
export function escapeRegex(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

// Builds a URL matcher that tolerates the trailing slash added by routing.
export function pathRegex(path: string): RegExp {
  return new RegExp(`${path.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}/?$`, 'i');
}

// Creates deterministic-enough run labels for temporary UI data.
export function uniqueName(prefix: string): string {
  return `${prefix} ${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
}

// Signs in through the real Identity page without logging credential values.
export async function signIn(page: Page): Promise<void> {
  await page.goto(absoluteUrl(`/Identity/Account/Login?ReturnUrl=${encodeURIComponent(editorPath)}`));
  await page.getByLabel('Username').fill(config.username);
  await page.getByLabel('Password').fill(config.password);
  await Promise.all([
    page.waitForURL(url => !url.pathname.includes('/Identity/Account/Login')),
    page.getByRole('button', { name: 'Log in' }).click()
  ]);
}

// Waits for the Vue editor to replace the Razor loading shell.
export async function expectMountedWorkspace(page: Page): Promise<void> {
  const app = page.locator('#trip-editor-app');
  await expect(app).toBeVisible();
  await expect(app.locator('.trip-editor-workspace')).toBeVisible();
  await expect(app).not.toContainText('Trip Editor development server is not available');
  await expectActiveMetadataSurface(page);
  await expectInitializedTripMap(page);
}

// Confirms the #252 shared surface hosts the active trip metadata editor.
export async function expectActiveMetadataSurface(page: Page): Promise<void> {
  await expect(page.locator('.trip-editor-surface--docked .trip-editor-metadata')).toBeVisible();
  await expect(page.locator('.trip-editor-surface--docked')).toContainText(/Edit Trip -/i);
}

// Confirms Leaflet mounted into a real map box after Vue rendered the workspace.
export async function expectInitializedTripMap(page: Page): Promise<void> {
  const map = page.getByLabel('Read-only trip map');
  await expect(map).toBeVisible();
  await expect(map).toHaveClass(/leaflet-container/);
  await expect(map.locator('.leaflet-pane')).not.toHaveCount(0);
  await expect(map.locator('.leaflet-tile-pane')).toHaveCount(1);
  await expect(map.locator('.leaflet-overlay-pane')).toHaveCount(1);

  const box = await map.boundingBox();
  expect(box, 'Trip Editor map should have a rendered bounding box.').not.toBeNull();
  expect(box!.width, 'Trip Editor map should have usable width.').toBeGreaterThan(300);
  expect(box!.height, 'Trip Editor map should have usable height.').toBeGreaterThan(300);
}

// Loads the editor API state used to derive runbook-specific E2E fixtures.
export async function loadEditorStateFixture(page: Page): Promise<EditorTripFixture> {
  const response = await page.request.get(absoluteUrl(editorApiPath), {
    headers: { Accept: 'application/json' }
  });
  expect(response.ok(), `GET ${editorApiPath} returned ${response.status()}`).toBeTruthy();
  return (await response.json()) as EditorTripFixture;
}

// Derives stable sidebar search examples from the configured trip fixture.
export function sidebarSearchFixture(state: EditorTripFixture): SidebarSearchFixture {
  const place = Object.values(state.placesById)[0];
  if (!place) {
    throw new Error('Configured Trip Editor fixture must contain at least one loaded place for sidebar search coverage.');
  }

  const placeRegion = state.regionsById[place.regionId];
  if (!placeRegion) {
    throw new Error(`Configured Trip Editor fixture place ${place.id} references a missing parent region.`);
  }

  const area = Object.values(state.areasById)[0] ?? null;
  const areaRegion = area ? state.regionsById[area.regionId] : null;
  const segment = state.segmentOrder.map(id => state.segmentsById[id]).find(Boolean) ?? null;

  return {
    region: { name: placeRegion.name },
    place: { name: place.name, regionName: placeRegion.name },
    area: area && areaRegion ? { name: area.name, regionName: areaRegion.name } : null,
    segment: segment ? segmentSearchFixture(state, segment) : null
  };
}

// Finds a child under the shadow region, or returns null so the spec can skip explicitly.
export function shadowChildFixture(state: EditorTripFixture): ShadowChildFixture | null {
  const shadow = Object.values(state.regionsById).find(region => region.isShadow);
  if (!shadow) {
    return null;
  }

  const shadowPlaceId = state.placeOrderByRegionId[shadow.id]?.find(id => state.placesById[id]);
  if (shadowPlaceId) {
    return { regionName: shadow.name, childName: state.placesById[shadowPlaceId].name, childKind: 'place' };
  }

  const shadowAreaId = state.areaOrderByRegionId[shadow.id]?.find(id => state.areasById[id]);
  if (shadowAreaId) {
    return { regionName: shadow.name, childName: state.areasById[shadowAreaId].name, childKind: 'area' };
  }

  return null;
}

// Collects disallowed network calls caused by sidebar-only filtering.
export function collectForbiddenSidebarSearchRequests(page: Page): () => string[] {
  const urls: string[] = [];
  page.on('request', request => {
    if (forbiddenSidebarSearchRequest.test(request.url())) {
      urls.push(request.url());
    }
  });

  return () => urls;
}

// Locates a rendered region card by its heading.
export function regionCard(page: Page, name: string): Locator {
  return page.locator('.trip-editor-region-card').filter({ has: page.getByRole('heading', { name }) });
}

// Locates the region header edit action when the current region allows it.
export function regionEditButton(card: Locator): Locator {
  return card.locator('.trip-editor-region-card__header').getByRole('button', { name: 'Edit' });
}

// Locates the first currently visible Add Place action.
export function firstVisibleAddPlace(page: Page): Locator {
  return page.getByRole('button', { name: 'Add Place' }).filter({ visible: true }).first();
}

// Locates the first region that currently renders child rows.
export function firstRegionWithChildren(page: Page): Locator {
  return page.locator('.trip-editor-region-card').filter({ has: page.locator('ul li') }).first();
}

// Closes the active editor, accepting the shared discard dialog when dirty.
export async function closeDraftWithDiscard(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Cancel' }).click();
  const dialog = page.getByRole('dialog', { name: 'Discard changes?' });
  if (await dialog.isVisible({ timeout: 1000 }).catch(() => false)) {
    await dialog.getByRole('button', { name: 'Discard' }).click();
  }
  await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toHaveCount(0);
}

// Confirms search-add UI is not exposed by sidebar filtering.
export async function expectNoSearchAddUi(page: Page): Promise<void> {
  await expect(page.getByRole('button', { name: /search.?add|add from search/i })).toHaveCount(0);
  await expect(page.getByRole('link', { name: /search.?add|add from search/i })).toHaveCount(0);
}

function segmentSearchFixture(state: EditorTripFixture, segment: EditorTripFixture['segmentsById'][string]): { query: string; label: string } {
  const from = segment.fromPlaceId ? state.placesById[segment.fromPlaceId]?.name : null;
  const to = segment.toPlaceId ? state.placesById[segment.toPlaceId]?.name : null;
  const label = [from, to].filter(Boolean).join(' to ') || segment.mode || 'Segment';
  const modeLabel = state.options.transportModes.find(mode => mode.value === segment.mode)?.label;
  return { query: modeLabel || segment.mode || label, label };
}
