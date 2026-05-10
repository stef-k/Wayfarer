import { expect, test, type Locator, type Page, type TestInfo } from '@playwright/test';
import { loadTripEditorConfig } from './tripEditorConfig';

const config = loadTripEditorConfig();
const workspacePath = `/User/Trip/Workspace/${config.tripId}`;
const legacyEditPath = `/User/Trip/Edit/${config.tripId}`;
const editorApiPath = `/api/trips/${config.tripId}/editor`;

test.describe.serial('Trip Editor dev verification', () => {
  test('login succeeds', async ({ page }) => {
    await signIn(page);

    await expect(page).toHaveURL(pathRegex(workspacePath));
    await expectActiveMetadataSurface(page);
  });

  test('workspace, editor API, and legacy editor load', async ({ page }) => {
    await signIn(page);

    const apiResponse = await page.request.get(absoluteUrl(editorApiPath), {
      headers: { Accept: 'application/json' }
    });
    expect(apiResponse.ok(), `GET ${editorApiPath} returned ${apiResponse.status()}`).toBeTruthy();
    expect(apiResponse.headers()['content-type']).toMatch(/application\/json/i);
    expect(String((await apiResponse.json()).tripId).toLowerCase()).toBe(config.tripId.toLowerCase());

    await page.goto(absoluteUrl(workspacePath));
    await expectMountedWorkspace(page);

    const legacyResponse = await page.goto(absoluteUrl(legacyEditPath));
    expect(legacyResponse?.ok(), `GET ${legacyEditPath} returned ${legacyResponse?.status() ?? 'no response'}`).toBeTruthy();
    await expect(page).toHaveURL(pathRegex(legacyEditPath));
    await expect(page.getByText('Trip Settings')).toBeVisible();
  });

  test('metadata surfaces work in docked and expanded dark/light states', async ({ page }, testInfo) => {
    await signIn(page);
    await page.goto(absoluteUrl(workspacePath));
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

  test('region edit and place create surfaces validate, save temporary data, and clean up', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(workspacePath));
    await expectMountedWorkspace(page);

    const unique = uniqueName('PW matrix');
    const placeName = `${unique} place`;
    let savedPlace = false;

    try {
      const editableRegion = page.locator('.trip-editor-region-card--normal').first();
      const regionHeading = (await editableRegion.getByRole('heading').innerText()).trim();
      await regionEditButton(editableRegion).click();
      await expect(page.getByRole('heading', { name: new RegExp(`Edit Region - ${escapeRegex(regionHeading)}`) })).toBeVisible();
      await page.getByRole('button', { name: 'Expand Editor' }).click();
      const regionDialog = page.getByRole('dialog', { name: new RegExp(`Edit Region - ${escapeRegex(regionHeading)}`) });
      await expect(regionDialog).toBeVisible();
      await regionDialog.getByRole('button', { name: 'Dock to sidebar' }).click();
      await page.locator('.trip-editor-surface--docked').getByLabel('Name').fill('');
      await page.getByRole('button', { name: 'Save Region' }).click();
      await expect(page.getByRole('alert')).toBeVisible();
      await page.getByRole('button', { name: 'Reset' }).click();
      await page.getByRole('button', { name: 'Cancel' }).click();

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
      await page.getByRole('button', { name: 'Save Place' }).click();
      await expectSaved(page);
      savedPlace = true;
      await expect(editableRegion).toContainText(placeName);
      await expectReverseGeocodeWarningIfPresent(page);

      await editableRegion.getByText(placeName).locator('xpath=ancestor::li[contains(@class, "trip-editor-place-row")]').getByRole('button', { name: 'Edit', exact: true }).click();
      await expect(page.getByRole('heading', { name: new RegExp(`Edit Place - ${escapeRegex(placeName)}`) })).toBeVisible();
    } finally {
      await cleanupTemporaryPlace(page, placeName, savedPlace);
    }
  });

  test('draft values survive dock-expanded-dock and dirty close prompts use the shared dialog', async ({ page }, testInfo) => {
    await signIn(page);
    await page.goto(absoluteUrl(workspacePath));
    await expectMountedWorkspace(page);

    const draftName = uniqueName('Unsaved metadata');
    await page.getByLabel('Name').fill(draftName);
    await page.getByRole('button', { name: 'Expand Editor' }).click();
    const metadataDialog = page.getByRole('dialog', { name: /Edit Trip -/i });
    await expect(metadataDialog.getByLabel('Name')).toHaveValue(draftName);
    await metadataDialog.getByRole('button', { name: 'Dock to sidebar' }).click();
    await expect(page.locator('.trip-editor-surface--docked').getByLabel('Name')).toHaveValue(draftName);

    await page.getByRole('button', { name: 'Close' }).click();
    const dialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(dialog).toBeVisible();
    await capture(page, testInfo, 'validation-confirmation');
    await dialog.getByRole('button', { name: 'Keep editing' }).click();
    await expect(dialog).toHaveCount(0);
    await expect(page.locator('.trip-editor-surface--docked').getByLabel('Name')).toHaveValue(draftName);

    await page.getByRole('button', { name: 'Close' }).click();
    await page.getByRole('dialog', { name: 'Discard changes?' }).getByRole('button', { name: 'Discard' }).click();
    await expect(page.getByRole('button', { name: 'Edit Trip' })).toBeVisible();
    await page.getByRole('button', { name: 'Edit Trip' }).click();
    await expectActiveMetadataSurface(page);
  });

  test('dirty target switch prompts and clean target switch does not', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(workspacePath));
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

    await page.getByRole('button', { name: 'Cancel' }).click();
    await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Add Region' })).toBeVisible();
  });

  test('region hierarchy and unavailable #249 mockup controls remain correct', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(workspacePath));
    await expectMountedWorkspace(page);

    await expectTripLevelTagsOnly(page);
    await expectMockupOnlyPlaceControlsAbsent(page);
    await expectAddPlaceButtonsAreRegionScoped(page);
    await expectUnimplementedAreaAndSegmentActionsAbsent(page);

    const card = firstRegionWithChildren(page);
    const children = card.locator('ul');
    await expect(children).toBeVisible();
    await card.getByRole('button', { name: 'Collapse' }).click();
    await expect(children).toBeHidden();
    await card.getByRole('button', { name: 'Expand' }).click();
    await expect(children).toBeVisible();
  });

  test('responsive narrow workspace remains usable', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 390, height: 900 });
    await signIn(page);
    await page.goto(absoluteUrl(workspacePath));
    await expectMountedWorkspace(page);

    await expect(page.locator('.trip-editor-sidebar')).toBeVisible();
    await expect(page.getByLabel('Read-only trip map')).toBeVisible();
    await page.getByRole('button', { name: 'Expand Editor' }).click();
    await expect(page.getByRole('dialog', { name: /Edit Trip -/i })).toBeVisible();
    await expectNoObviousOverflow(page.getByRole('dialog', { name: /Edit Trip -/i }));
    await capture(page, testInfo, 'responsive-narrow');
  });

  test('safe reorder coverage is documented unless suitable rows exist', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(workspacePath));
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

// Signs in through the real Identity page without logging credential values.
async function signIn(page: Page): Promise<void> {
  await page.goto(absoluteUrl(`/Identity/Account/Login?ReturnUrl=${encodeURIComponent(workspacePath)}`));
  await page.getByLabel('Username').fill(config.username);
  await page.getByLabel('Password').fill(config.password);
  await Promise.all([
    page.waitForURL(url => !url.pathname.includes('/Identity/Account/Login')),
    page.getByRole('button', { name: 'Log in' }).click()
  ]);
}

// Waits for the Vue workspace to replace the Razor loading shell.
async function expectMountedWorkspace(page: Page): Promise<void> {
  const app = page.locator('#trip-editor-app');
  await expect(app).toBeVisible();
  await expect(app.locator('.trip-editor-workspace')).toBeVisible();
  await expect(app).not.toContainText('Trip Editor development server is not available');
  await expectActiveMetadataSurface(page);
  await expect(page.getByLabel('Read-only trip map')).toBeVisible();
}

// Confirms the #252 shared surface hosts the active trip metadata editor.
async function expectActiveMetadataSurface(page: Page): Promise<void> {
  await expect(page.locator('.trip-editor-surface--docked .trip-editor-metadata')).toBeVisible();
  await expect(page.locator('.trip-editor-surface--docked')).toContainText(/Edit Trip -/i);
}

// Confirms tags appear in the Trip-level panel and not inside the place editor form.
async function expectTripLevelTagsOnly(page: Page): Promise<void> {
  const tagsHeading = page.getByRole('heading', { name: 'Tags' });
  if (await tagsHeading.isVisible()) {
    await expect(tagsHeading.locator('xpath=ancestor::section[contains(@class, "trip-editor-panel")]')).toBeVisible();
  }
}

// Guards against fields from the design mockups that are not implemented on main.
async function expectMockupOnlyPlaceControlsAbsent(page: Page): Promise<void> {
  await expect(page.getByRole('tab', { name: /visit progress/i })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /visit progress/i })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /photos?/i })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /links?|official site/i })).toHaveCount(0);

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

// Keeps future Add Area/Add Segment work from appearing as inert controls in this tooling baseline.
async function expectUnimplementedAreaAndSegmentActionsAbsent(page: Page): Promise<void> {
  await expect(page.getByRole('button', { name: /add area/i })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /add segment/i })).toHaveCount(0);
  await expect(page.getByRole('link', { name: /add area/i })).toHaveCount(0);
  await expect(page.getByRole('link', { name: /add segment/i })).toHaveCount(0);
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

function firstVisibleAddPlace(page: Page): Locator {
  return page.getByRole('button', { name: 'Add Place' }).filter({ visible: true }).first();
}

function firstRegionWithChildren(page: Page): Locator {
  return page.locator('.trip-editor-region-card').filter({ has: page.locator('ul li') }).first();
}

function regionCard(page: Page, name: string): Locator {
  return page.locator('.trip-editor-region-card').filter({ has: page.getByRole('heading', { name }) });
}

async function cleanupTemporaryPlace(page: Page, name: string, shouldCleanup: boolean): Promise<void> {
  if (!shouldCleanup || (await page.getByText(name).count()) === 0) {
    return;
  }

  await page.getByText(name).locator('xpath=ancestor::li[contains(@class, "trip-editor-place-row")]').getByRole('button', { name: 'Edit', exact: true }).click();
  await page.getByRole('button', { name: 'Delete' }).click();
  await page.getByRole('dialog', { name: 'Delete place?' }).getByRole('button', { name: 'Delete' }).click();
  await expect(page.getByText(name)).toHaveCount(0);
}

async function closeDraftWithDiscard(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Cancel' }).click();
  const dialog = page.getByRole('dialog', { name: 'Discard changes?' });
  if (await dialog.isVisible({ timeout: 1000 }).catch(() => false)) {
    await dialog.getByRole('button', { name: 'Discard' }).click();
  }
  await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toHaveCount(0);
}

async function cleanupTemporaryRegion(page: Page, name: string, shouldCleanup: boolean): Promise<void> {
  if (!shouldCleanup || (await regionCard(page, name).count()) === 0) {
    return;
  }

  await regionEditButton(regionCard(page, name)).click();
  await page.getByRole('button', { name: 'Delete' }).click();
  await page.getByRole('dialog', { name: 'Delete region?' }).getByRole('button', { name: 'Delete' }).click();
  await expect(regionCard(page, name)).toHaveCount(0);
}

function regionEditButton(card: Locator): Locator {
  return card.locator('.trip-editor-region-card__header').getByRole('button', { name: 'Edit' });
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

function uniqueName(prefix: string): string {
  return `${prefix} ${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
}

function pathRegex(path: string): RegExp {
  return new RegExp(`${path.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}/?$`, 'i');
}

function escapeRegex(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function absoluteUrl(path: string): string {
  return `${config.baseUrl}${path}`;
}
