import { expect, test, type Locator, type Page } from '@playwright/test';
import { loadTripEditorConfig } from './tripEditorConfig';

const config = loadTripEditorConfig();
const workspacePath = `/User/Trip/Workspace/${config.tripId}`;
const legacyEditPath = `/User/Trip/Edit/${config.tripId}`;
const editorApiPath = `/api/trips/${config.tripId}/editor`;

test.describe('Trip Editor dev verification', () => {
  test('login succeeds', async ({ page }) => {
    await signIn(page);

    await expect(page).toHaveURL(pathRegex(workspacePath));
    await expectActiveMetadataSurface(page);
  });

  test('workspace loads and Vue app mounts', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(workspacePath));
    await expectMountedWorkspace(page);
  });

  test('editor API returns authenticated JSON', async ({ page }) => {
    await signIn(page);

    const response = await page.request.get(absoluteUrl(editorApiPath), {
      headers: { Accept: 'application/json' }
    });
    expect(response.ok(), `GET ${editorApiPath} returned ${response.status()}`).toBeTruthy();
    expect(response.headers()['content-type']).toMatch(/application\/json/i);

    const payload = await response.json();
    expect(String(payload.tripId).toLowerCase()).toBe(config.tripId.toLowerCase());
  });

  test('legacy trip edit page loads', async ({ page }) => {
    await signIn(page);
    const response = await page.goto(absoluteUrl(legacyEditPath));

    expect(response?.ok(), `GET ${legacyEditPath} returned ${response?.status() ?? 'no response'}`).toBeTruthy();
    await expect(page).toHaveURL(pathRegex(legacyEditPath));
    await expect(page.getByText('Trip Settings')).toBeVisible();
  });

  test('current editor surface keeps metadata docked and excludes mockup-only place controls', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(workspacePath));
    await expectMountedWorkspace(page);

    await expect(page.locator('.trip-editor-sidebar')).toContainText(/Edit Trip -/i);
    await expect(page.locator('.trip-editor-metadata')).toBeVisible();
    await expectTripLevelTagsOnly(page);
    await expectMockupOnlyPlaceControlsAbsent(page);
    await expectAddPlaceButtonsAreRegionScoped(page);
    await expectUnimplementedAreaAndSegmentActionsAbsent(page);
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

  const placeForm = await openFirstPlaceFormIfAvailable(page);
  if (placeForm) {
    await expect(placeForm.getByText(/^Tags$/i)).toHaveCount(0);
    await expect(placeForm.getByLabel(/tags/i)).toHaveCount(0);
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

  const addPlace = page.getByRole('button', { name: 'Add Place' }).first();
  if ((await addPlace.count()) === 0 || !(await addPlace.isVisible()) || !(await addPlace.isEnabled())) {
    return null;
  }

  await addPlace.click();
  await expect(page.getByRole('heading', { name: 'Add Place' })).toBeVisible();
  return form;
}

function pathRegex(path: string): RegExp {
  return new RegExp(`${path.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}/?$`, 'i');
}

function absoluteUrl(path: string): string {
  return `${config.baseUrl}${path}`;
}
