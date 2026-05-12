import { expect, test, type Locator, type Page, type Route } from '@playwright/test';
import {
  absoluteUrl,
  editorApiPath,
  expectMountedWorkspace,
  loadEditorStateFixture,
  signIn,
  workspacePath
} from './tripEditorTestUtils';

type MutableEditorState = Record<string, any>;

const regionId = '00000000-0000-0000-0000-000000272001';
const placeId = '00000000-0000-0000-0000-000000272002';
const areaId = '00000000-0000-0000-0000-000000272003';
const segmentId = '00000000-0000-0000-0000-000000272004';
const regionName = 'PW rich notes region';
const placeName = 'PW rich notes place';
const areaName = 'PW rich notes area';
const editorApiMatcher = new RegExp(`${editorApiPath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?:/.*)?$`, 'i');

test.describe.serial('Trip Editor rich notes parity', () => {
  test('all owner forms render the shared rich notes editor instead of raw notes textareas', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithRichNotesFixture(page);

    await expectRichNotesOwner(page.locator('#trip-editor-metadata-form'), 'trip-editor-metadata-notes');
    await expect(page.getByText('Notes HTML')).toHaveCount(0);

    await openRegion(page);
    await expectRichNotesOwner(page.locator('#trip-editor-region-form'), 'trip-editor-region-form-notes');
    await expect(page.getByText('Notes HTML')).toHaveCount(0);

    await openPlace(page);
    await expectRichNotesOwner(page.locator('#trip-editor-place-form'), 'trip-editor-place-form-notes');
    await expect(page.getByText('Notes HTML')).toHaveCount(0);

    await openArea(page);
    await expectRichNotesOwner(page.locator('#trip-editor-area-form'), 'trip-editor-area-form-notes');
    await expect(page.getByText('Notes HTML')).toHaveCount(0);

    await openSegment(page);
    await expectRichNotesOwner(page.locator('#trip-editor-segment-form'), 'trip-editor-segment-form-notes');
    await expect(page.getByText('Notes HTML')).toHaveCount(0);
  });

  test('metadata and child owner saves send canonical rich notes through existing mutation endpoints', async ({ page }) => {
    await signIn(page);
    const state = await loadWorkspaceWithRichNotesFixture(page);
    const requests: Array<{ method: string; url: string; body: Record<string, any> }> = [];
    await routeEditorMutations(page, state, requests);

    await richEditor(page.locator('#trip-editor-metadata-form')).locator('.ql-editor').click();
    await page.keyboard.type('Metadata rich note');
    await insertImageUrl(page.locator('#trip-editor-metadata-form'), 'https://example.com/photo.jpg');
    await page.getByRole('button', { name: 'Save & Continue' }).click();
    await expect.poll(() => requests.length).toBe(1);
    expect(requests[0].method).toBe('PATCH');
    expect(requests[0].url).toContain('/metadata');
    expectCanonicalNotes(requests[0].body.notesHtml, ['Metadata rich note', 'https://example.com/photo.jpg']);

    await openRegion(page);
    await richEditor(page.locator('#trip-editor-region-form')).locator('.ql-editor').fill('');
    await page.keyboard.type('Region rich note');
    await page.getByRole('button', { name: 'Save Region' }).click();
    await expect.poll(() => requests.length).toBe(2);
    expect(requests[1].method).toBe('PUT');
    expect(requests[1].url).toContain(`/regions/${regionId}`);
    expectCanonicalNotes(requests[1].body.notesHtml, ['Region rich note']);
  });

  test('metadata save sanitizes unedited persisted notes when another field changes', async ({ page }) => {
    await signIn(page);
    const state = await loadWorkspaceWithRichNotesFixture(page);
    state.metadata.notesHtml = [
      '<h2 class="trip-img-modal" style="color:red">Unsafe persisted heading</h2>',
      '<p class="editor-only" style="font-size:30px"><strong>Bold</strong> <em>Italic</em> <u>Underline</u></p>',
      '<p><a class="unsafe-link" style="color:red" onclick="alert(1)" href="https://example.test/page">Safe link</a></p>',
      '<p><span class="ql-font-serif trip-img-modal" style="font-family:serif" data-original="ignored">Font note</span></p>',
      '<p><img class="trip-img-modal" style="width:400px" data-original="ignored" loading="lazy" src="/Public/ProxyImage?url=https%3A%2F%2Fcdn.example.test%2Fproxied.jpg"></p>',
      '<p><img src="   https://cdn.example.test/direct.jpg   "></p>',
      '<p><img src="data:text/html,ignored"></p>',
      '<p><img src="file:///C:/temp/ignored.png"></p>',
      '<p><img src="vbscript:msgbox(1)"></p>',
      '<p><img src="java&#x0A;script:alert(1)"></p>',
      '<p><img src="not a url"></p>'
    ].join('');
    const requests: Array<{ method: string; url: string; body: Record<string, any> }> = [];
    await routeEditorMutations(page, state, requests);

    await page.locator('#trip-editor-metadata-form').getByLabel('Name').fill('PW rich notes renamed trip');
    await page.getByRole('button', { name: 'Save & Continue' }).click();

    await expect.poll(() => requests.length).toBe(1);
    const notesHtml = requests[0].body.notesHtml as string;
    expectCanonicalNotes(notesHtml, [
      'Unsafe persisted heading',
      '<strong>Bold</strong>',
      '<em>Italic</em>',
      '<u>Underline</u>',
      'href="https://example.test/page"',
      '<span class="ql-font-serif">Font note</span>',
      'https://cdn.example.test/proxied.jpg',
      'https://cdn.example.test/direct.jpg'
    ]);
    expect(notesHtml).not.toContain('style=');
    expect(notesHtml).not.toContain('trip-img-modal');
    expect(notesHtml).not.toContain('editor-only');
    expect(notesHtml).not.toContain('unsafe-link');
    expect(notesHtml).not.toContain('onclick');
    expect(notesHtml).not.toContain('data-original');
    expect(notesHtml).not.toContain('loading=');
    expect(notesHtml).not.toContain('data:text');
    expect(notesHtml).not.toContain('file:');
    expect(notesHtml).not.toContain('vbscript:');
    expect(notesHtml).not.toContain('javascript:');
    expect(notesHtml).not.toContain('not a url');
  });

  test('image dialog inserts URL embeds and data images are blocked with visible feedback', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithRichNotesFixture(page);

    const form = page.locator('#trip-editor-metadata-form');
    await insertImageUrl(form, 'https://images.example.test/rich-note.png');
    const image = richEditor(form).locator('.ql-editor img');
    await expect(image).toHaveAttribute('src', /https:\/\/images\.example\.test\/rich-note\.png$/);

    await pasteDataImage(richEditor(form).locator('.ql-editor'));
    await expect(form.getByRole('status')).toContainText('Embedded data images are not allowed');
    await expect(richEditor(form).locator('.ql-editor img[src^="data:image"]')).toHaveCount(0);
  });

  test('data image blocking handles mixed case and whitespace-padded variants before draft storage', async ({ page }) => {
    await signIn(page);
    const state = await loadWorkspaceWithRichNotesFixture(page);
    const requests: Array<{ method: string; url: string; body: Record<string, any> }> = [];
    await routeEditorMutations(page, state, requests);

    const form = page.locator('#trip-editor-metadata-form');
    const editor = richEditor(form).locator('.ql-editor');

    await pasteDataImage(editor, '<p><img src=" DATA:IMAGE/png;base64,iVBORw0KGgo="></p>');
    await expect(form.getByRole('status')).toContainText('Embedded data images are not allowed');
    await expect(editor.locator('img')).toHaveCount(0);

    await dropDataImage(editor, '<p><img src="\r\ndata : image/png;base64,iVBORw0KGgo="></p>');
    await expect(form.getByRole('status')).toContainText('Embedded data images are not allowed');
    await expect(editor.locator('img')).toHaveCount(0);

    await rejectImageDialogUrl(form, '\tDaTa : ImAgE/png;base64,iVBORw0KGgo=');

    await editor.evaluate(element => {
      const image = document.createElement('img');
      image.setAttribute('src', '\nDATA:\tIMAGE/png;base64,from-export');
      element.append(image);
      element.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertFromPaste' }));
    });
    await expect(form.getByRole('status')).toContainText('Embedded data images are not allowed');
    await expect(editor.locator('img')).toHaveCount(0);

    await editor.click();
    await editor.evaluate(element => {
      element.insertAdjacentHTML('beforeend', '<script>alert("x")</script><a href="javascript:alert(1)" onclick="alert(2)">Unsafe link</a><img src="javascript:alert(3)" onerror="alert(4)">');
    });
    await page.keyboard.type('Normalized rich note');
    await page.getByRole('button', { name: 'Save & Continue' }).click();
    await expect.poll(() => requests.length).toBe(1);
    expectCanonicalNotes(requests[0].body.notesHtml, ['Normalized rich note']);
    expect(requests[0].body.notesHtml).not.toContain('<script');
    expect(requests[0].body.notesHtml).not.toContain('javascript:');
    expect(requests[0].body.notesHtml).not.toContain('onclick');
    expect(requests[0].body.notesHtml).not.toContain('onerror');
  });

  test('docked and expanded modes keep one active notes draft and shared discard/reset flows', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithRichNotesFixture(page);

    const dockedForm = page.locator('#trip-editor-metadata-form');
    await richEditor(dockedForm).locator('.ql-editor').click();
    await page.keyboard.type('Expanded draft note');
    await page.getByRole('button', { name: 'Expand Editor' }).click();

    const dialog = page.getByRole('dialog', { name: /Edit Trip -/ });
    await expect(dialog.locator('#trip-editor-metadata-form')).toHaveCount(1);
    await expect(page.locator('#trip-editor-metadata-form')).toHaveCount(1);
    await expect(richEditor(dialog.locator('#trip-editor-metadata-form')).locator('.ql-editor')).toContainText('Expanded draft note');

    await dialog.getByRole('button', { name: 'Dock to sidebar' }).click();
    await expect(page.locator('#trip-editor-metadata-form')).toHaveCount(1);
    await expect(richEditor(page.locator('#trip-editor-metadata-form')).locator('.ql-editor')).toContainText('Expanded draft note');

    await page.getByRole('button', { name: 'Close' }).click();
    const discard = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(discard).toBeVisible();
    await discard.getByRole('button', { name: 'Keep editing' }).click();
    await expect(richEditor(page.locator('#trip-editor-metadata-form')).locator('.ql-editor')).toContainText('Expanded draft note');

    await page.getByRole('button', { name: 'Cancel / Reset' }).click();
    await expect(richEditor(page.locator('#trip-editor-metadata-form')).locator('.ql-editor')).not.toContainText('Expanded draft note');
  });

  test('area and segment map-work preserve notes without adding another editor surface', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithRichNotesFixture(page);

    await openArea(page);
    await richEditor(page.locator('#trip-editor-area-form')).locator('.ql-editor').click();
    await page.keyboard.type('Area map-work note');
    await page.getByRole('button', { name: 'Draw/Edit Area' }).click();
    await expect(page.getByRole('region', { name: 'Map work' })).toBeVisible();
    await expect(page.locator('.trip-editor-rich-notes')).toHaveCount(0);
    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Done' }).click();
    await expect(richEditor(page.locator('#trip-editor-area-form')).locator('.ql-editor')).toContainText('Area map-work note');
    await page.getByRole('button', { name: 'Reset' }).click();

    await openSegment(page);
    await richEditor(page.locator('#trip-editor-segment-form')).locator('.ql-editor').click();
    await page.keyboard.type('Segment map-work note');
    await page.getByRole('button', { name: 'Draw/Edit Route' }).click();
    await expect(page.getByRole('region', { name: 'Map work' })).toBeVisible();
    await expect(page.locator('.trip-editor-rich-notes')).toHaveCount(0);
    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Done' }).click();
    await expect(richEditor(page.locator('#trip-editor-segment-form')).locator('.ql-editor')).toContainText('Segment map-work note');
  });
});

async function loadWorkspaceWithRichNotesFixture(page: Page): Promise<MutableEditorState> {
  await page.unroute(editorApiMatcher).catch(() => undefined);
  const state = await loadEditorStateFixture(page) as MutableEditorState;
  prepareRichNotesState(state);
  await page.route(editorApiMatcher, async route => routeEditorState(route, state, []));
  await page.goto(absoluteUrl(workspacePath));
  await expectMountedWorkspace(page);
  return state;
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
  if (method === 'PATCH' && url.includes('/metadata')) {
    state.metadata = { ...state.metadata, ...body };
    return mutationResult(state.metadata, { metadata: state.metadata });
  }

  if (method === 'PUT' && url.includes(`/regions/${regionId}`)) {
    state.regionsById[regionId] = { ...state.regionsById[regionId], ...body };
    return mutationResult(state.regionsById[regionId], { regions: [state.regionsById[regionId]] });
  }

  throw new Error(`Unexpected rich notes mutation ${method} ${url}`);
}

function prepareRichNotesState(state: MutableEditorState): void {
  state.permissions.canEditMetadata = true;
  state.permissions.canEditRegions = true;
  state.permissions.canEditPlaces = true;
  state.permissions.canEditAreas = true;
  state.permissions.canEditSegments = true;

  state.regionsById[regionId] = {
    id: regionId,
    tripId: state.tripId,
    name: regionName,
    notesHtml: '<p>Persisted region note</p>',
    coverImage: null,
    center: null,
    displayOrder: 1,
    isShadow: false,
    capabilities: editableCapabilities()
  };
  state.regionOrder = [regionId, ...state.regionOrder.filter((id: string) => id !== regionId)];

  state.placesById[placeId] = {
    id: placeId,
    tripId: state.tripId,
    regionId,
    name: placeName,
    notesHtml: '<p>Persisted place note</p>',
    address: 'Athens',
    location: { latitude: 37.9838, longitude: 23.7275 },
    iconName: state.options.iconNames[0] ?? 'marker',
    markerColor: state.options.markerColorClasses[0] ?? 'bg-blue',
    displayOrder: 1,
    visitSummary: { placeId, visitCount: 0, isVisited: false, firstVisitAt: null, lastVisitAt: null },
    capabilities: editableCapabilities()
  };
  state.placeOrderByRegionId[regionId] = [placeId];

  state.areasById[areaId] = {
    id: areaId,
    tripId: state.tripId,
    regionId,
    name: areaName,
    notesHtml: '<p>Persisted area note</p>',
    fillHex: '#ff6600',
    geometry: { type: 'Polygon', coordinates: [[[23, 37], [24, 37], [24, 38], [23, 37]]] },
    displayOrder: 1,
    capabilities: editableCapabilities()
  };
  state.areaOrderByRegionId[regionId] = [areaId];

  state.segmentsById[segmentId] = {
    id: segmentId,
    tripId: state.tripId,
    fromPlaceId: placeId,
    toPlaceId: placeId,
    mode: state.options.transportModes[0]?.value ?? 'walk',
    estimatedDistanceKm: 1,
    estimatedDurationMinutes: 10,
    notesHtml: '<p>Persisted segment note</p>',
    route: { type: 'LineString', coordinates: [[23, 37], [24, 38]] },
    displayOrder: 1,
    capabilities: editableCapabilities()
  };
  state.segmentOrder = [segmentId];
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

async function expectRichNotesOwner(form: Locator, editorId: string): Promise<void> {
  await expect(form.locator(`[data-rich-notes-editor="${editorId}"]`)).toBeVisible();
  await expect(form.getByText('Notes', { exact: true })).toBeVisible();
  await expect(form.locator('textarea')).toHaveCount(0);
  await expect(richEditor(form).locator('.ql-editor')).toBeVisible();
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

async function pasteDataImage(editor: Locator, html = '<p><img src="data:image/png;base64,iVBORw0KGgo="></p>'): Promise<void> {
  await editor.evaluate((element, value) => {
    const data = new DataTransfer();
    data.setData('text/html', value);
    const event = new ClipboardEvent('paste', { bubbles: true, cancelable: true, clipboardData: data });
    element.dispatchEvent(event);
  }, html);
}

async function dropDataImage(editor: Locator, html: string): Promise<void> {
  await editor.evaluate((element, value) => {
    const data = new DataTransfer();
    data.setData('text/html', value);
    const event = new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: data });
    element.dispatchEvent(event);
  }, html);
}

async function rejectImageDialogUrl(form: Locator, url: string): Promise<void> {
  await richEditor(form).locator('.ql-image').click();
  const dialog = form.page().getByRole('dialog', { name: 'Insert image URL' });
  await expect(dialog).toBeVisible();
  await dialog.getByLabel('Image URL').fill(url);
  await dialog.getByRole('button', { name: 'Insert Image' }).click();
  await expect(dialog.getByText('Embedded data images are not allowed')).toBeVisible();
  await expect(form.getByRole('status')).toContainText('Embedded data images are not allowed');
  await dialog.getByRole('button', { name: 'Cancel' }).click();
}

async function openRegion(page: Page): Promise<void> {
  await page.locator(`[data-region-id="${regionId}"] .trip-editor-region-card__header`).getByRole('button', { name: 'Edit' }).click();
  await expect(page.getByRole('heading', { name: `Edit Region - ${regionName}` })).toBeVisible();
}

async function openPlace(page: Page): Promise<void> {
  await page.locator(`[data-place-id="${placeId}"]`).getByRole('button', { name: 'Edit' }).click();
  await expect(page.getByRole('heading', { name: `Edit Place - ${placeName}` })).toBeVisible();
}

async function openArea(page: Page): Promise<void> {
  await page.locator(`[data-area-id="${areaId}"]`).getByRole('button', { name: 'Edit' }).click();
  await expect(page.getByRole('heading', { name: `Edit Area - ${areaName}` })).toBeVisible();
}

async function openSegment(page: Page): Promise<void> {
  await page.locator(`[data-segment-id="${segmentId}"] .trip-editor-list-button`).click();
  await expect(page.getByRole('heading', { name: /Edit Segment -/ })).toBeVisible();
}

function expectCanonicalNotes(value: string, expectedParts: string[]): void {
  for (const part of expectedParts) {
    expect(value).toContain(part);
  }

  expect(value).not.toContain('/Public/ProxyImage');
  expect(value).not.toContain('data-original');
  expect(value).not.toContain('data:image');
  expect(value).not.toContain('trip-img-modal');
}
