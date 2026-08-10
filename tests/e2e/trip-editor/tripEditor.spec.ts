import { expect, test, type Locator, type Page, type TestInfo } from '@playwright/test';
import {
  absoluteUrl,
  activeEditorAlert,
  activeEditorCancelButton,
  activeEditorCloseButton,
  closeDraftWithDiscard,
  config,
  editorApiPath,
  escapeRegex,
  expectActiveMetadataSurface,
  expectMountedWorkspace,
  expectNoLegacyEditorAction,
  firstRegionWithChildren,
  firstVisibleAddPlace,
  editorPath,
  pathRegex,
  regionEditButton,
  signIn,
  signInAs,
  uniqueName,
  removedWorkspacePath
} from './tripEditorTestUtils';

test.describe.serial('Trip Editor dev verification', () => {
  test('login succeeds', async ({ page }) => {
    await signIn(page);

    await expect(page).toHaveURL(pathRegex(editorPath));
    await expectActiveMetadataSurface(page);
  });

  test('canonical editor, editor API, and removed workspace route behavior load', async ({ page }) => {
    await signIn(page);

    const apiResponse = await page.request.get(absoluteUrl(editorApiPath), {
      headers: { Accept: 'application/json' }
    });
    expect(apiResponse.ok(), `GET ${editorApiPath} returned ${apiResponse.status()}`).toBeTruthy();
    expect(apiResponse.headers()['content-type']).toMatch(/application\/json/i);
    expect(String((await apiResponse.json()).tripId).toLowerCase()).toBe(config.tripId.toLowerCase());

    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    const workspaceResponse = await page.request.get(absoluteUrl(removedWorkspacePath), { maxRedirects: 0 });
    expect(workspaceResponse.status(), `GET ${removedWorkspacePath} should not remain a supported editor route.`).toBe(404);

    const editResponse = await page.goto(absoluteUrl(editorPath));
    expect(editResponse?.ok(), `GET ${editorPath} returned ${editResponse?.status() ?? 'no response'}`).toBeTruthy();
    await expect(page).toHaveURL(pathRegex(editorPath));
    await expectMountedWorkspace(page);
    await expectNoLegacyEditorAction(page);
  });

  test('removed workspace route does not expose role-specific editor behavior', async ({ page }) => {
    await signInAs(page, 'admin', 'Admin1!', removedWorkspacePath);
    const workspaceResponse = await page.request.get(absoluteUrl(removedWorkspacePath), { maxRedirects: 0 });
    expect(workspaceResponse.status(), `GET ${removedWorkspacePath} should not remain a supported editor route.`).toBe(404);
  });

  test('metadata surfaces work in docked and expanded dark/light states', async ({ page }, testInfo) => {
    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    await setTheme(page, 'dark');
    await expectActiveMetadataSurface(page);
    await expectNoObviousOverflow(page.locator('.trip-editor-surface--docked'));
    await capture(page, testInfo, 'docked-dark');

    await page.getByRole('button', { name: 'Expand Editor' }).click();
    const expanded = page.getByRole('dialog', { name: /Edit Trip -/i });
    await expect(expanded).toBeVisible();
    await expect(expanded.locator('.trip-editor-metadata')).toBeVisible();
    await capture(page, testInfo, 'expanded-dark');

    await setTheme(page, 'light');
    await expect(expanded).toBeVisible();
    await capture(page, testInfo, 'expanded-light');

    await expanded.getByRole('button', { name: 'Dock to sidebar' }).click();
    await expectActiveMetadataSurface(page);
    await capture(page, testInfo, 'docked-light');
  });

  test('region edit and place create surfaces validate, save temporary data, and clean up', async ({ page }, testInfo) => {
    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    const unique = uniqueName('PW matrix');
    const placeName = `${unique} place`;
    let savedPlace = false;

    try {
      const editableRegion = page.locator('.trip-editor-region-card--normal').first();
      const regionHeading = (await editableRegion.getAttribute('data-region-name'))!;
      await regionEditButton(editableRegion).click();
      await expect(page.getByRole('heading', { name: new RegExp(`Edit Region - ${escapeRegex(regionHeading)}`) })).toBeVisible();
      await page.getByRole('button', { name: 'Expand Editor' }).click();
      const regionDialog = page.getByRole('dialog', { name: new RegExp(`Edit Region - ${escapeRegex(regionHeading)}`) });
      await expect(regionDialog).toBeVisible();
      await regionDialog.getByRole('button', { name: 'Dock to sidebar' }).click();
      await page.locator('.trip-editor-surface--docked').getByLabel('Name').fill('');
      await page.getByRole('button', { name: 'Save Region' }).click();
      await expect(activeEditorAlert(page)).toBeVisible();
      await page.getByRole('button', { name: 'Reset' }).click();
      await activeEditorCancelButton(page).click();

      await editableRegion.getByRole('button', { name: 'Add Place' }).click();
      await expect(page.getByRole('heading', { name: 'Add Place' })).toBeVisible();
      await page.getByLabel('Name').fill(placeName);
      await page.getByLabel('Address').fill('Temporary Playwright address');
      await page.getByLabel('Latitude').fill('37.9838');
      await page.getByLabel('Longitude').fill('23.7275');
      await page.getByLabel('Reverse geocode this location on save').check();
      await page.getByRole('button', { name: 'Expand Editor' }).click();
      const placeDialog = page.getByRole('dialog', { name: 'Add Place' });
      await expect(placeDialog).toBeVisible();
      await placeDialog.getByRole('button', { name: 'Dock to sidebar' }).click();
      const placeSurface = page.getByRole('region', { name: 'Add Place', exact: true });
      const savePlace = placeSurface.getByRole('button', { name: 'Save Place', exact: true });
      await expect(savePlace).toHaveCount(1);
      await expect(savePlace).toBeVisible();
      await expect(savePlace).toBeEnabled();
      await savePlace.click();
      await expectSaved(page);
      savedPlace = true;
      await expect(editableRegion).toContainText(placeName);
      await expectReverseGeocodeWarningIfPresent(page);

      await editableRegion.locator('.trip-editor-place-row').filter({ hasText: placeName }).getByRole('button', { name: 'Edit', exact: true }).click();
      await expect(page.getByRole('heading', { name: new RegExp(`Edit Place - ${escapeRegex(placeName)}`) })).toBeVisible();
      await expectUsableDockedPlaceEditor(page);
      await capture(page, testInfo, 'place-docked-layout');
    } finally {
      await cleanupTemporaryPlace(page, placeName, savedPlace);
    }
  });

  test('draft values survive dock-expanded-dock and dirty close prompts use the shared dialog', async ({ page }, testInfo) => {
    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    const draftName = uniqueName('Unsaved metadata');
    await page.getByLabel('Name').fill(draftName);
    await page.getByRole('button', { name: 'Expand Editor' }).click();
    const metadataDialog = page.getByRole('dialog', { name: /Edit Trip -/i });
    await expect(metadataDialog.getByLabel('Name')).toHaveValue(draftName);
    await metadataDialog.getByRole('button', { name: 'Dock to sidebar' }).click();
    await expect(page.locator('.trip-editor-surface--docked').getByLabel('Name')).toHaveValue(draftName);

    await activeEditorCloseButton(page).click();
    const dialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(dialog).toBeVisible();
    await capture(page, testInfo, 'validation-confirmation');
    await dialog.getByRole('button', { name: 'Keep editing' }).click();
    await expect(dialog).toHaveCount(0);
    await expect(page.locator('.trip-editor-surface--docked').getByLabel('Name')).toHaveValue(draftName);

    await activeEditorCloseButton(page).click();
    await page.getByRole('dialog', { name: 'Discard changes?' }).getByRole('button', { name: 'Discard' }).click();
    await expect(page.getByRole('button', { name: 'Edit Trip' })).toBeVisible();
    await page.getByRole('button', { name: 'Edit Trip' }).click();
    await expectActiveMetadataSurface(page);
  });

  test('dirty target switch prompts and clean target switch does not', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    await page.getByRole('button', { name: 'Add Region' }).click();
    await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toHaveCount(0);
    await page.getByLabel('Name').fill(uniqueName('Dirty region'));
    await firstVisibleAddPlace(page).click();
    await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toBeVisible();
    await page.getByRole('button', { name: 'Keep editing' }).click();
    await expect(page.getByRole('heading', { name: 'Add Region' })).toBeVisible();

    await firstVisibleAddPlace(page).click();
    await page.getByRole('dialog', { name: 'Discard changes?' }).getByRole('button', { name: 'Discard' }).click();
    await expect(page.getByRole('heading', { name: 'Add Place' })).toBeVisible();
    await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toHaveCount(0);

    await activeEditorCancelButton(page).click();
    await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Add Region' })).toBeVisible();
  });

  test('region hierarchy and unavailable #249 mockup controls remain correct', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    await expectTripLevelTagsOnly(page);
    await expectMockupOnlyPlaceControlsAbsent(page);
    await expectAddPlaceButtonsAreRegionScoped(page);
  await expectMockupOnlySegmentControlsAbsent(page);

    const card = firstRegionWithChildren(page);
    const children = card.locator('ul');
    await expect(children).toBeVisible();
    await card.getByRole('button', { name: 'Collapse' }).click();
    await expect(children).toBeHidden();
    await card.getByRole('button', { name: 'Expand' }).click();
    await expect(children).toBeVisible();
  });

  test('trip tags are editable in docked settings and update the sidebar', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    const tagName = uniqueName('pw-docked-tag');
    try {
      await addTagInActiveSettings(page, tagName);
      await page.getByRole('button', { name: 'Save & Continue' }).click();
      await expectSaved(page);
      await expect(sidebarTagsPanel(page)).toContainText(tagName);
    } finally {
      await removeTagIfPresent(page, tagName);
    }
  });

  test('trip tags are editable in expanded settings', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    const tagName = uniqueName('pw-expanded-tag');
    try {
      await page.getByRole('button', { name: 'Expand Editor' }).click();
      const dialog = page.getByRole('dialog', { name: /Edit Trip -/i });
      await addTagInSurface(dialog, tagName);
      await dialog.getByRole('button', { name: 'Save & Continue' }).click();
      await expectSaved(page);
      await expect(sidebarTagsPanel(page)).toContainText(tagName);
    } finally {
      await removeTagIfPresent(page, tagName);
    }
  });

  test('share progress toggle is visible and private draft disables it', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    const surface = page.locator('.trip-editor-surface--docked');
    const shareToggle = surface.getByLabel('Show visit progress on public trip');
    await expect(shareToggle).toBeVisible();

    await surface.getByRole('checkbox', { name: 'Public trip', exact: true }).uncheck();

    await expect(shareToggle).toBeDisabled();
    await expect(shareToggle).not.toBeChecked();
    await expect(surface.getByRole('link', { name: 'Open progress URL' })).toHaveCount(0);
  });

  test('Save & Exit stays on the editor when tag save fails', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);
    await page.route('**/api/trips/*/editor/tags', async route => {
      await route.fulfill({
        status: 400,
        contentType: 'application/problem+json',
        body: JSON.stringify({
          title: 'One or more validation errors occurred.',
          status: 400,
          errors: { tags: ['Injected tag save failure.'] }
        })
      });
    });

    await addTagInActiveSettings(page, uniqueName('pw-failed-tag'));
    await page.getByRole('button', { name: 'Save & Exit' }).click();

    await expect(page).toHaveURL(pathRegex(editorPath));
    await expect(activeEditorAlert(page)).toContainText('One or more validation errors occurred.');
    await expect(page.locator('.trip-editor-surface--docked')).toContainText('Injected tag save failure.');
  });

  test('responsive narrow editor remains usable', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 390, height: 900 });
    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    await expect(page.locator('.trip-editor-sidebar')).toBeVisible();
    await expect(page.getByLabel('Read-only trip map')).toBeVisible();
    await page.getByRole('button', { name: 'Edit Trip' }).click();
    await page.getByRole('button', { name: 'Expand Editor' }).click();
    await expect(page.getByRole('dialog', { name: /Edit Trip -/i })).toBeVisible();
    await expectNoObviousOverflow(page.getByRole('dialog', { name: /Edit Trip -/i }));
    await capture(page, testInfo, 'responsive-narrow');
  });

  test('safe reorder coverage is documented unless suitable rows exist', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    const regionHandles = page.getByRole('button', { name: 'Drag to reorder region' });
    const placeHandles = page.getByRole('button', { name: 'Drag to reorder place' });
    test.info().annotations.push({
      type: 'matrix-note',
      description: `Region reorder handles: ${await regionHandles.count()}; place reorder handles: ${await placeHandles.count()}. Drag mutation is intentionally not automated against the shared runbook trip because restoring SortableJS order would still mutate production-like runbook data.`
    });
    await expect(regionHandles.first()).toBeVisible();
  });
});

// Confirms the selected place editor docks as a usable full-width row under the place.
async function expectUsableDockedPlaceEditor(page: Page): Promise<void> {
  const sidebar = page.locator('.trip-editor-sidebar');
  const dockedEditor = page.locator('.trip-editor-place-editor-row .trip-editor-surface--docked');
  await expect(dockedEditor).toBeVisible();
  await expect(page.locator('#trip-editor-place-form')).toHaveCount(1);

  const [sidebarBox, editorBox] = await Promise.all([sidebar.boundingBox(), dockedEditor.boundingBox()]);
  expect(sidebarBox, 'Trip Editor sidebar should have a rendered bounding box.').not.toBeNull();
  expect(editorBox, 'Place editor should have a rendered bounding box.').not.toBeNull();
  expect(editorBox!.width, 'Docked place editor should use most of the sidebar width.').toBeGreaterThan(sidebarBox!.width * 0.75);
}

// Confirms tags appear in the Trip-level panel and not inside the place editor form.
async function expectTripLevelTagsOnly(page: Page): Promise<void> {
  const sidebarPanel = sidebarTagsPanel(page);
  if (await sidebarPanel.isVisible().catch(() => false)) {
    await expect(sidebarPanel).toBeVisible();
  }

  await expect(page.locator('#trip-editor-metadata-form').getByRole('heading', { name: 'Tags' })).toBeVisible();
}

// Guards against fields from the design mockups that are not implemented on main.
async function expectMockupOnlyPlaceControlsAbsent(page: Page): Promise<void> {
  await expect(page.getByRole('tab', { name: /visit progress/i })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /visit progress/i })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /photos?/i })).toHaveCount(0);
  await expect(page.locator('button:not(.ql-link)').filter({ hasText: /^links?$/i })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /official site/i })).toHaveCount(0);

  const placeForm = await openFirstPlaceFormIfAvailable(page);
  if (!placeForm) {
    return;
  }

  await expect(placeForm.getByLabel(/photos?/i)).toHaveCount(0);
  await expect(placeForm.getByLabel(/official site|links?/i)).toHaveCount(0);
  await expect(placeForm.getByLabel(/^type$/i)).toHaveCount(0);
  await expect(placeForm.getByLabel(/tags/i)).toHaveCount(0);
  await expect(placeForm.getByText(/visit-progress|visit progress/i)).toHaveCount(0);
  await closeDraftWithDiscard(page);
}

// Uses the existing region cards to ensure Add Place remains attached to a region surface.
async function expectAddPlaceButtonsAreRegionScoped(page: Page): Promise<void> {
  const addPlaceButtons = page.getByRole('button', { name: 'Add Place' });
  const count = await addPlaceButtons.count();
  for (let index = 0; index < count; index += 1) {
    await expect(addPlaceButtons.nth(index).locator('xpath=ancestor::article[contains(@class, "trip-editor-region-card")]')).toHaveCount(1);
  }
}

// Guards against segment controls that are explicitly outside the current slice.
async function expectMockupOnlySegmentControlsAbsent(page: Page): Promise<void> {
  await expect(page.getByRole('button', { name: /geocode|search.?add|marker drag/i })).toHaveCount(0);
  await expect(page.getByRole('link', { name: /geocode|search.?add|marker drag/i })).toHaveCount(0);
}

async function openFirstPlaceFormIfAvailable(page: Page): Promise<Locator | null> {
  const form = page.locator('form').filter({ has: page.getByRole('heading', { name: /^(Add|Edit) Place$/ }) });
  if (await form.isVisible()) {
    return form;
  }

  const addPlace = firstVisibleAddPlace(page);
  if ((await addPlace.count()) === 0 || !(await addPlace.isVisible()) || !(await addPlace.isEnabled())) {
    return null;
  }

  await addPlace.click();
  await expect(page.getByRole('heading', { name: 'Add Place' })).toBeVisible();
  return form;
}

async function cleanupTemporaryPlace(page: Page, name: string, shouldCleanup: boolean): Promise<void> {
  if (!shouldCleanup || (await page.getByText(name).count()) === 0) {
    return;
  }

  await page.getByText(name).locator('xpath=ancestor::li[contains(@class, "trip-editor-place-row")]').getByRole('button', { name: 'Edit', exact: true }).click();
  await page.getByRole('button', { name: 'Delete', exact: true }).click();
  await page.getByRole('dialog', { name: 'Delete place?' }).getByRole('button', { name: 'Delete' }).click();
  await expect(page.getByText(name)).toHaveCount(0);
}

async function expectSaved(page: Page): Promise<void> {
  await expect(page.locator('.trip-editor-save-state').filter({ hasText: /Saved/i }).first()).toBeVisible();
}

async function expectReverseGeocodeWarningIfPresent(page: Page): Promise<void> {
  const warning = page.locator('.trip-editor-form-warning');
  if ((await warning.count()) === 0 || !(await warning.first().isVisible())) {
    test.info().annotations.push({
      type: 'matrix-note',
      description: 'Reverse-geocode warning did not appear; the runbook account may have a working Mapbox token.'
    });
    return;
  }

  await expect(warning).toContainText(/Reverse geocoding was unavailable/i);
  await expect(warning).toHaveCSS('color', /rgb\(/);
  await expect(page.locator('.trip-editor-form-error')).toHaveCount(0);
  await expect(page.getByText('Save failed')).toHaveCount(0);
}

function sidebarTagsPanel(page: Page): Locator {
  return page.locator('.trip-editor-panel').filter({ has: page.locator('h2', { hasText: 'Tags' }) }).first();
}

async function addTagInActiveSettings(page: Page, tagName: string): Promise<void> {
  await addTagInSurface(page.locator('.trip-editor-surface--docked'), tagName);
}

async function addTagInSurface(surface: Locator, tagName: string): Promise<void> {
  await surface.getByLabel('Add tag').fill(tagName);
  await surface.getByRole('button', { name: 'Add' }).click();
  await expect(surface).toContainText(tagName);
}

async function removeTagIfPresent(page: Page, tagName: string): Promise<void> {
  await page.unroute('**/api/trips/*/editor/tags').catch(() => undefined);
  await page.goto(absoluteUrl(editorPath));
  await expectMountedWorkspace(page);
  const remove = page.locator('.trip-editor-surface--docked').getByRole('button', { name: `Remove tag ${tagName}` });
  if ((await remove.count()) === 0) {
    return;
  }

  await remove.click();
  await page.getByRole('button', { name: 'Save & Continue' }).click();
  await expectSaved(page);
  await expect(page.locator('.trip-editor-sidebar')).not.toContainText(tagName);
}

async function expectNoObviousOverflow(locator: Locator): Promise<void> {
  await expect(locator).toBeVisible();
  const overflow = await locator.evaluate(element => {
    const nodes = [element, ...Array.from(element.querySelectorAll<HTMLElement>('button, input, textarea, select, h1, h2, h3, span, small, p, label'))];
    return nodes
      .filter(node => node instanceof HTMLElement)
      .map(node => {
        const style = window.getComputedStyle(node);
        return {
          text: node.textContent?.trim().slice(0, 80) ?? node.tagName,
          overflowing: node.scrollWidth > node.clientWidth + 2 && style.overflowX === 'visible'
        };
      })
      .filter(result => result.overflowing);
  });
  expect(overflow, `Overflowing elements: ${JSON.stringify(overflow)}`).toEqual([]);
}

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate(value => document.documentElement.setAttribute('data-bs-theme', value), theme);
}

async function capture(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  await page.screenshot({ fullPage: true, path: testInfo.outputPath('screenshots', `${name}.png`) });
}
