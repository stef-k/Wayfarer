import { expect, test, type Locator, type Page, type Route } from '@playwright/test';
import {
  absoluteUrl,
  editorApiPath,
  editorPath,
  expectMountedWorkspace,
  loadEditorStateFixture,
  signIn
} from './tripEditorTestUtils';

type MutableEditorState = Record<string, any>;

const regionId = '00000000-0000-0000-0000-000000295101';
const newPlaceId = '00000000-0000-0000-0000-000000295102';
const imageUrl = 'https://images.example.test/new-place.png';
const htmlUrl = 'https://images.example.test/not-an-image';
const missingUrl = 'https://images.example.test/missing-image.png';
const editorApiMatcher = new RegExp(`${editorApiPath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?:/.*)?$`, 'i');
const tinyPng = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=', 'base64');

test.describe('Trip Editor new-place notes image URL parity', () => {
  test('new place notes image URL preview uses the proxy and keeps the canonical URL for save', async ({ page }) => {
    await signIn(page);
    const state = await loadWorkspace(page);
    const requests: Array<{ method: string; url: string; body: Record<string, any> }> = [];
    await routeEditorMutations(page, state, requests);
    await routeProxyImage(page, imageUrl);

    await openNewPlace(page);
    const form = page.locator('#trip-editor-place-form');
    await insertImageUrl(form, imageUrl);

    const image = richEditor(form).locator('.ql-editor img');
    await expect(image).toHaveAttribute('src', /\/Public\/ProxyImage\?url=https%3A%2F%2Fimages\.example\.test%2Fnew-place\.png$/);
    await expectLoadedImages(image);

    await form.getByLabel('Name').fill('New place image note');
    await form.getByLabel('Latitude').fill('37.9838');
    await form.getByLabel('Longitude').fill('23.7275');
    await page.getByRole('button', { name: 'Save Place' }).click();

    await expect.poll(() => requests.length).toBe(1);
    expect(requests[0].method).toBe('POST');
    expect(requests[0].url).toContain('/places');
    expectCanonicalNotes(requests[0].body.notesHtml, [imageUrl]);
  });

  test('new place notes remove failed image URL previews without saving broken proxy markup', async ({ page }) => {
    await signIn(page);
    await loadWorkspace(page);
    await routeProxyResponse(page, htmlUrl, { status: 200, contentType: 'text/html', body: '<!doctype html><title>Not an image</title>' });
    await routeProxyResponse(page, missingUrl, { status: 404, contentType: 'text/plain', body: 'missing' });

    await openNewPlace(page);
    const form = page.locator('#trip-editor-place-form');

    await insertImageUrl(form, htmlUrl);
    await expectRejectedImageUrl(form, htmlUrl);

    await insertImageUrl(form, missingUrl);
    await expectRejectedImageUrl(form, missingUrl);
  });
});

async function loadWorkspace(page: Page): Promise<MutableEditorState> {
  await page.unroute(editorApiMatcher).catch(() => undefined);
  const state = await loadEditorStateFixture(page) as MutableEditorState;
  prepareState(state);
  await page.route(editorApiMatcher, async route => routeEditorState(route, state, []));
  await page.goto(absoluteUrl(editorPath));
  await expectMountedWorkspace(page);
  return state;
}

function prepareState(state: MutableEditorState): void {
  state.permissions.canEditPlaces = true;
  state.regionsById[regionId] = {
    id: regionId,
    tripId: state.tripId,
    name: 'PW image notes region',
    notesHtml: '',
    coverImage: null,
    center: null,
    displayOrder: 1,
    isShadow: false,
    capabilities: editableCapabilities()
  };
  state.regionOrder = [regionId, ...state.regionOrder.filter((id: string) => id !== regionId)];
  state.placeOrderByRegionId[regionId] = [];
}

async function routeProxyImage(page: Page, url: string): Promise<void> {
  await routeProxyResponse(page, url, { status: 200, contentType: 'image/png', body: tinyPng });
}

async function routeProxyResponse(page: Page, url: string, response: { status: number; contentType: string; body: string | Buffer }): Promise<void> {
  const escapedUrl = encodeURIComponent(url).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  await page.route(new RegExp(`/Public/ProxyImage\\?url=${escapedUrl}$`, 'i'), async route => {
    await route.fulfill(response);
  });
}

async function routeEditorMutations(page: Page, state: MutableEditorState, requests: Array<{ method: string; url: string; body: Record<string, any> }>): Promise<void> {
  await page.unroute(editorApiMatcher);
  await page.route(editorApiMatcher, async route => routeEditorState(route, state, requests));
}

async function routeEditorState(route: Route, state: MutableEditorState, requests: Array<{ method: string; url: string; body: Record<string, any> }>): Promise<void> {
  const request = route.request();
  if (request.method() === 'GET') {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
    return;
  }

  const body = request.postDataJSON() as Record<string, any>;
  requests.push({ method: request.method(), url: request.url(), body });
  const result = applyMutation(state, request.method(), request.url(), body);
  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(result) });
}

function applyMutation(state: MutableEditorState, method: string, url: string, body: Record<string, any>): Record<string, any> {
  if (method !== 'POST' || !url.includes(`/regions/${regionId}/places`)) {
    throw new Error(`Unexpected new-place image-note mutation ${method} ${url}`);
  }

  const place = {
    id: newPlaceId,
    tripId: state.tripId,
    regionId,
    displayOrder: 1,
    visitSummary: { placeId: newPlaceId, visitCount: 0, isVisited: false, firstVisitAt: null, lastVisitAt: null },
    capabilities: editableCapabilities(),
    ...body
  };
  state.placesById[newPlaceId] = place;
  state.placeOrderByRegionId[regionId] = [newPlaceId];
  return mutationResult(place, { places: [place], placeOrdersByRegionId: { [regionId]: state.placeOrderByRegionId[regionId] } });
}

function editableCapabilities(): Record<string, boolean> {
  return {
    canEdit: true,
    canRename: true,
    canDelete: true,
    canReorder: true,
    canMove: true,
    canAddChildren: true,
    canTargetForSearchAdd: true
  };
}

function mutationResult(data: Record<string, any>, affected: Record<string, any>): Record<string, any> {
  return {
    success: true,
    data,
    affected: {
      metadata: null,
      regions: [],
      regionOrder: null,
      places: [],
      placeOrdersByRegionId: {},
      areas: [],
      areaOrdersByRegionId: {},
      segments: [],
      segmentOrder: null,
      tags: [],
      tagOrder: null,
      visitProgress: null,
      options: null,
      ...affected
    },
    deletedIds: { regions: [], places: [], areas: [], segments: [], tags: [] },
    warnings: []
  };
}

async function openNewPlace(page: Page): Promise<void> {
  await page.locator(`[data-region-id="${regionId}"]`).getByRole('button', { name: 'Add Place' }).click();
  await expect(page.getByRole('heading', { name: 'Add Place' })).toBeVisible();
}

function richEditor(form: Locator): Locator {
  return form.locator('.trip-editor-rich-notes');
}

async function insertImageUrl(form: Locator, url: string): Promise<void> {
  await richEditor(form).locator('.ql-image').click();
  const dialog = form.page().getByRole('dialog', { name: 'Insert image URL' });
  await expect(dialog).toBeVisible();
  await dialog.getByLabel('Image URL').fill(url);
  await dialog.getByRole('button', { name: 'Insert Image' }).click();
  await expect(dialog).toHaveCount(0);
}

async function expectRejectedImageUrl(form: Locator, url: string): Promise<void> {
  const encoded = encodeURIComponent(url);
  await expect(form.getByRole('status')).toContainText('Image URL could not be loaded');
  await expect(richEditor(form).locator(`.ql-editor img[src*="${encoded}"]`)).toHaveCount(0);
  await expect(richEditor(form).locator('.ql-editor')).not.toContainText(url);
}

async function expectLoadedImages(images: Locator): Promise<void> {
  const count = await images.count();
  expect(count, 'Expected at least one image to validate.').toBeGreaterThan(0);
  for (let index = 0; index < count; index += 1) {
    await expect.poll(async () => images.nth(index).evaluate(image => image instanceof HTMLImageElement && image.complete && image.naturalWidth > 0 && image.naturalHeight > 0)).toBe(true);
  }
}

function expectCanonicalNotes(value: string, expectedParts: string[]): void {
  for (const part of expectedParts) {
    expect(value).toContain(part);
  }

  expect(value).not.toContain('/Public/ProxyImage');
  expect(value).not.toContain('data:image');
}
