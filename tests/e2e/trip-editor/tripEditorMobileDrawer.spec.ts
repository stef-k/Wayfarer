import { expect, test, type Page, type Route, type TestInfo } from '@playwright/test';
import {
  absoluteUrl,
  activeEditorSurface,
  editorApiPath,
  editorPath,
  expectInitializedTripMap,
  expectMountedWorkspace,
  firstVisibleAddPlace,
  loadEditorStateFixture,
  signIn
} from './tripEditorTestUtils';

const geocodePath = /\/api\/trips\/[^/]+\/editor\/geocode\/search/i;
const editorApiMatcher = new RegExp(`${editorApiPath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?:/.*)?$`, 'i');
type MutableEditorState = Record<string, any>;

test.describe.serial('Trip Editor mobile bottom drawer', () => {
  test('phone starts map-first with Trip as the drawer tab', async ({ page }, testInfo) => {
    await signIn(page);
    await openEditorWithTripSummaryFixture(page, { width: 390, height: 844 }, state => {
      state.metadata.isPublic = true;
      state.metadata.shareProgressEnabled = true;
      state.metadata.publicUrl = '/Public/Trips/mobile-drawer-public';
      state.metadata.progressPublicUrl = '/Public/Trips/mobile-drawer-progress';
    });

    await expect(page.getByRole('navigation', { name: 'Trip editor sections' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Trip', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('button', { name: 'Regions' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Segments' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Edit Trip' })).toBeVisible();
    const tripTab = page.locator('.trip-editor-mobile-drawer__tab--trip');
    await expect(tripTab).toContainText('Public trip');
    await expect(tripTab).toContainText('Share progress: Enabled');
    await expect(tripTab.getByRole('link', { name: 'Open public trip' })).toHaveAttribute('href', '/Public/Trips/mobile-drawer-public');
    await expect(tripTab.getByRole('link', { name: 'Open progress URL' })).toHaveAttribute('href', '/Public/Trips/mobile-drawer-progress');
    await expect(page.locator('.trip-editor-surface--docked .trip-editor-metadata')).toHaveCount(0);
    await expectDrawerState(page, 'peek');
    await expectMapFirstPhoneLayout(page);
    await capture(page, testInfo, 'phone-light-initial-trip-map-first');

    await page.evaluate(() => document.documentElement.setAttribute('data-bs-theme', 'dark'));
    await expectMapFirstPhoneLayout(page);
    await capture(page, testInfo, 'phone-dark-initial-trip-map-first');
  });

  test('desktop clean metadata edit resizes to phone Trip summary', async ({ page }) => {
    await signIn(page);
    await openEditorAt(page, { width: 1280, height: 900 });

    await expect(page.locator('.trip-editor-surface--docked .trip-editor-metadata')).toBeVisible();

    await page.setViewportSize({ width: 390, height: 844 });

    await expect(page.getByRole('navigation', { name: 'Trip editor sections' })).toBeVisible();
    await expect(drawerTab(page, 'Trip')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('#trip-editor-metadata-form')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Edit Trip' })).toBeVisible();
    await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toHaveCount(0);
    await expectDrawerState(page, 'peek');
  });

  test('Trip summary exposes disabled and private share-progress states without progress links', async ({ page }) => {
    await signIn(page);

    await openEditorWithTripSummaryFixture(page, { width: 390, height: 844 }, state => {
      state.metadata.isPublic = true;
      state.metadata.shareProgressEnabled = false;
      state.metadata.publicUrl = '/Public/Trips/mobile-drawer-public-disabled-progress';
      state.metadata.progressPublicUrl = '/Public/Trips/mobile-drawer-progress-disabled';
    });
    let tripTab = page.locator('.trip-editor-mobile-drawer__tab--trip');
    await expect(tripTab).toContainText('Public trip');
    await expect(tripTab).toContainText('Share progress: Disabled');
    await expect(tripTab.getByRole('link', { name: 'Open public trip' })).toHaveAttribute('href', '/Public/Trips/mobile-drawer-public-disabled-progress');
    await expect(tripTab.getByRole('link', { name: 'Open progress URL' })).toHaveCount(0);

    await openEditorWithTripSummaryFixture(page, { width: 390, height: 844 }, state => {
      state.metadata.isPublic = false;
      state.metadata.shareProgressEnabled = true;
      state.metadata.publicUrl = null;
      state.metadata.progressPublicUrl = '/Public/Trips/mobile-drawer-progress-private';
    });
    tripTab = page.locator('.trip-editor-mobile-drawer__tab--trip');
    await expect(tripTab).toContainText('Private trip');
    await expect(tripTab).toContainText('Share progress: Unavailable until trip is public');
    await expect(tripTab.getByRole('link', { name: 'Open public trip' })).toHaveCount(0);
    await expect(tripTab.getByRole('link', { name: 'Open progress URL' })).toHaveCount(0);
  });

  test('phone drawer exposes deterministic collapsed, peek, and expanded view states', async ({ page }) => {
    await signIn(page);
    await openEditorAt(page, { width: 390, height: 844 });

    await expect(page.locator('.trip-editor-mobile-drawer__handle')).toHaveCount(0);
    await expectDrawerState(page, 'peek');
    await expectCompactDrawerChrome(page);
    await expectDrawerHeight(page, { min: 170, max: 190 });
    const initialPeekHeight = await drawerHeight(page);

    await clickAndExpectDrawerState(page, 'Collapse', 'collapsed');
    await expect(page.getByRole('navigation', { name: 'Trip editor sections' })).toBeHidden();
    await expectDrawerHeight(page, { min: 84, max: 100 });
    await expectMapFirstPhoneLayout(page);

    await clickAndExpectDrawerState(page, 'Peek', 'peek');
    await expect(page.getByRole('navigation', { name: 'Trip editor sections' })).toBeVisible();
    await expectHeightCloseTo(page, initialPeekHeight);
    await expectMapFirstPhoneLayout(page);

    await clickAndExpectDrawerState(page, 'Expand', 'expanded-view');
    await expect(page.getByRole('button', { name: 'Trip', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expectDrawerHeight(page, { min: 640, max: 735 });
    const expandedHeight = await drawerHeight(page);

    await clickAndExpectDrawerState(page, 'Regions', 'expanded-view');
    await expectHeightCloseTo(page, expandedHeight);

    await clickAndExpectDrawerState(page, 'Segments', 'expanded-view');
    await expectHeightCloseTo(page, expandedHeight);
  });

  test('phone routes tab-owned editing, search-add, and map-work through the drawer', async ({ page }, testInfo) => {
    await signIn(page);
    await routeGeocode(page);
    await openEditorAt(page, { width: 390, height: 844 });

    await page.getByRole('button', { name: 'Regions' }).click();
    await expect(page.getByRole('button', { name: 'Regions' })).toHaveAttribute('aria-pressed', 'true');
    await capture(page, testInfo, 'phone-regions-tab');

    await page.getByRole('button', { name: 'Segments' }).click();
    await expect(page.getByRole('button', { name: 'Segments' })).toHaveAttribute('aria-pressed', 'true');
    await capture(page, testInfo, 'phone-segments-tab');

    await page.getByRole('button', { name: 'Trip', exact: true }).click();
    await page.getByRole('button', { name: 'Edit Trip' }).click();
    await expect(page.getByRole('button', { name: 'Trip', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('.trip-editor-surface--docked .trip-editor-metadata')).toBeVisible();
    await expect(page.locator('.trip-editor-mobile-drawer__state-controls')).toBeVisible();
    await expectDrawerState(page, 'expanded-edit');
    await expectDrawerHeight(page, { min: 720, max: 735 });
    await expectOpaqueStickyChrome(page);
    const metadataName = page.locator('#trip-editor-metadata-form').getByLabel('Name');
    const activeDraftName = `${await metadataName.inputValue()} drawer recoverable edit`;
    await metadataName.fill(activeDraftName);
    await clickAndExpectDrawerState(page, 'Collapse', 'collapsed');
    await expect(page.locator('#trip-editor-metadata-form')).toBeHidden();
    await clickAndExpectDrawerState(page, 'Expand', 'expanded-edit');
    await expect(page.locator('#trip-editor-metadata-form').getByLabel('Name')).toHaveValue(activeDraftName);
    await capture(page, testInfo, 'phone-dark-active-trip-edit');
    await page.locator('.trip-editor-surface--docked').getByRole('button', { name: 'Close' }).click();
    const discardActiveEdit = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(discardActiveEdit).toBeVisible();
    await discardActiveEdit.getByRole('button', { name: 'Discard' }).click();

    await page.getByRole('searchbox', { name: 'Map search' }).fill('drawer search place');
    await page.getByRole('region', { name: 'Map search' }).getByRole('button', { name: 'Search' }).click();
    await page.getByRole('button', { name: 'Drawer Search Place' }).click();
    await expectContainedSearch(page);
    await capture(page, testInfo, 'phone-search-results-add-place');
    await page.getByRole('button', { name: 'Add as place' }).click();
    await expect(page.getByRole('button', { name: 'Regions' })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('heading', { name: 'Add Place' })).toBeVisible();
    await capture(page, testInfo, 'phone-search-add-active-place-edit');

    await page.getByRole('button', { name: 'Pick on map' }).click();
    await expectInitializedTripMap(page);
    await expectDrawerState(page, 'peek');
    const mapWorkEditor = page.locator('.trip-editor-sidebar--mobile-drawer .trip-editor-surface--map-work');
    await expect(mapWorkEditor).toContainText('New place');
    await expect(mapWorkEditor).toContainText('Add Place');
    await expect(mapWorkEditor.getByRole('status')).toContainText('Saved');
    await expect(page.getByRole('region', { name: 'Map search' })).toHaveCount(0);
    await expectMapFirstPhoneLayout(page);
    await expectMapWorkToolbarHitTesting(page);
    await capture(page, testInfo, 'phone-place-coordinate-map-work');
    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Cancel' }).click();
    await expect(page.getByRole('region', { name: 'Map work' })).toHaveCount(0);

    await page.setViewportSize({ width: 430, height: 932 });
    await clickAndExpectDrawerState(page, 'Peek', 'peek');
    await expectMapFirstPhoneLayout(page);
    await clickAndExpectDrawerState(page, 'Expand', 'expanded-edit');
    await page.getByRole('button', { name: 'Pick on map' }).click();
    await expectDrawerState(page, 'peek');
    await expectMapWorkToolbarHitTesting(page);
    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Done' }).click();
    await expect(page.getByRole('region', { name: 'Map work' })).toHaveCount(0);
  });

  test('dirty metadata tab switch keeps editing on cancel and discards before switching', async ({ page }) => {
    await signIn(page);
    await openEditorAt(page, { width: 390, height: 844 });

    await page.getByRole('button', { name: 'Edit Trip' }).click();
    const form = activeEditorSurface(page);
    const name = form.getByLabel('Name');
    const originalName = await name.inputValue();
    const draftName = `${originalName} mobile tab draft`;
    await name.fill(draftName);

    await page.getByRole('button', { name: 'Segments' }).click();
    const keepDialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(keepDialog).toContainText('Discard unsaved trip changes before switching tabs?');
    await keepDialog.getByRole('button', { name: 'Keep editing' }).click();
    await expect(page.getByRole('button', { name: 'Trip', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(name).toHaveValue(draftName);

    await page.getByRole('button', { name: 'Segments' }).click();
    const discardDialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(discardDialog).toContainText('Discard unsaved trip changes before switching tabs?');
    await discardDialog.getByRole('button', { name: 'Discard' }).click();
    await expect(page.getByRole('button', { name: 'Segments' })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('#trip-editor-metadata-form')).toHaveCount(0);
  });

  test('dirty metadata blocks marker selection routing until discard', async ({ page }) => {
    await signIn(page);
    await openEditorAt(page, { width: 390, height: 844 });
    await expectInitializedTripMap(page);

    await page.getByRole('button', { name: 'Edit Trip' }).click();
    const form = activeEditorSurface(page);
    const name = form.getByLabel('Name');
    const originalName = await name.inputValue();
    const draftName = `${originalName} marker route draft`;
    await name.fill(draftName);

    await tapFirstSavedPlaceMarker(page);
    const keepDialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(keepDialog).toContainText('Discard unsaved trip changes before switching tabs?');
    await keepDialog.getByRole('button', { name: 'Keep editing' }).click();
    await expect(drawerTab(page, 'Trip')).toHaveAttribute('aria-pressed', 'true');
    await expect(name).toHaveValue(draftName);
    await expectSelectedMarkerCount(page, 0);
    await expect(page.locator('.leaflet-popup')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Clear Selection' })).toHaveCount(0);

    await tapFirstSavedPlaceMarker(page);
    const discardDialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(discardDialog).toContainText('Discard unsaved trip changes before switching tabs?');
    await discardDialog.getByRole('button', { name: 'Discard' }).click();
    await expect(drawerTab(page, 'Regions')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('#trip-editor-metadata-form')).toHaveCount(0);
    await expectSelectedMarkerCount(page, 1);
    await expect(page.locator('.leaflet-popup')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Clear Selection' })).toBeVisible();
  });

  test('dirty regions-owned area editor remains visible during same-tab marker selection', async ({ page }) => {
    await signIn(page);
    await openEditorAt(page, { width: 390, height: 844 });
    await expectInitializedTripMap(page);

    await page.getByRole('button', { name: 'Regions' }).click();
    await page.getByRole('button', { name: 'Add Area' }).first().click();
    const form = activeEditorSurface(page);
    const name = form.getByLabel('Name');
    await expect(page.getByRole('heading', { name: 'Add Area' })).toBeVisible();
    await name.fill('Mobile dirty area selection guard');

    await tapFirstSavedPlaceMarker(page);
    await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toHaveCount(0);
    await expect(drawerTab(page, 'Regions')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('heading', { name: 'Add Area' })).toBeVisible();
    await expect(name).toHaveValue('Mobile dirty area selection guard');
    await expectSelectedMarkerCount(page, 1);
    await expect(page.getByRole('button', { name: 'Clear Selection' })).toBeVisible();
  });

  test('dirty segment editor blocks marker selection routing until discard', async ({ page }) => {
    await signIn(page);
    await openEditorAt(page, { width: 390, height: 844 });
    await expectInitializedTripMap(page);

    await page.getByRole('button', { name: 'Segments' }).click();
    await page.getByRole('button', { name: 'Add Segment' }).click();
    const form = activeEditorSurface(page); const duration = form.getByLabel('Estimated duration minutes');
    await form.getByLabel('Enter manually').check();
    await expect(page.getByRole('heading', { name: 'Add Segment' })).toBeVisible();
    await duration.fill('14');

    await tapFirstSavedPlaceMarker(page);
    const keepDialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(keepDialog).toContainText('Discard unsaved segment changes before switching tabs?');
    await keepDialog.getByRole('button', { name: 'Keep editing' }).click();
    await expect(drawerTab(page, 'Segments')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('heading', { name: 'Add Segment' })).toBeVisible();
    await expect(duration).toHaveValue('14');
    await expectSelectedMarkerCount(page, 0);
    await expect(page.locator('.leaflet-popup')).toHaveCount(0);

    await tapFirstSavedPlaceMarker(page);
    const discardDialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(discardDialog).toContainText('Discard unsaved segment changes before switching tabs?');
    await discardDialog.getByRole('button', { name: 'Discard' }).click();
    await expect(drawerTab(page, 'Regions')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('heading', { name: 'Add Segment' })).toHaveCount(0);
    await expectSelectedMarkerCount(page, 1);
    await expect(page.locator('.leaflet-popup')).toBeVisible();
  });

  test('dirty region and place editors cannot be hidden by mobile tab switching', async ({ page }) => {
    await signIn(page);
    await openEditorAt(page, { width: 390, height: 844 });

    await page.getByRole('button', { name: 'Regions' }).click();
    await page.locator('.trip-editor-region-card--normal').first().locator('.trip-editor-region-card__header').getByRole('button', { name: 'Edit' }).click();
    await expectDirtyTabSwitchGuard(page, {
      dirtyFieldLabel: 'Name',
      draftValue: 'Mobile dirty region tab guard',
      owningTab: 'Regions',
      targetTab: 'Segments',
      promptText: 'Discard unsaved region changes before switching tabs?',
      activeHeading: /Edit Region -/
    });

    await page.getByRole('button', { name: 'Regions' }).click();
    await firstVisibleAddPlace(page).click();
    await expectDirtyTabSwitchGuard(page, {
      dirtyFieldLabel: 'Name',
      draftValue: 'Mobile dirty place tab guard',
      owningTab: 'Regions',
      targetTab: 'Trip',
      promptText: 'Discard unsaved place changes before switching tabs?',
      activeHeading: 'Add Place'
    });
  });

  test('dirty segment editor cannot be hidden by mobile tab switching', async ({ page }) => {
    await signIn(page);
    await openEditorAt(page, { width: 390, height: 844 });

    await page.getByRole('button', { name: 'Segments' }).click();
    await page.getByRole('button', { name: 'Add Segment' }).click();
    await activeEditorSurface(page).getByLabel('Enter manually').check();
    await expectDirtyTabSwitchGuard(page, {
      dirtyFieldLabel: 'Estimated duration minutes',
      draftValue: '12',
      owningTab: 'Segments',
      targetTab: 'Trip',
      promptText: 'Discard unsaved segment changes before switching tabs?',
      activeHeading: 'Add Segment'
    });
  });

  test('dirty metadata edit survives desktop-phone-desktop breakpoint transitions', async ({ page }) => {
    await signIn(page);
    await openEditorAt(page, { width: 1280, height: 900 });

    const nameInput = page.locator('#trip-editor-metadata-form').getByLabel('Name');
    const originalName = await nameInput.inputValue();
    const draftName = `${originalName} unsaved resize`;
    await nameInput.fill(draftName);

    await page.setViewportSize({ width: 390, height: 844 });
    await expect(page.getByRole('navigation', { name: 'Trip editor sections' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Trip', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expectDrawerState(page, 'expanded-edit');
    await expect(page.locator('#trip-editor-metadata-form').getByLabel('Name')).toHaveValue(draftName);

    await page.locator('.trip-editor-surface--docked').getByRole('button', { name: 'Close' }).click();
    const discardDialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(discardDialog).toBeVisible();
    await discardDialog.getByRole('button', { name: 'Keep editing' }).click();
    await expect(discardDialog).toHaveCount(0);
    await expect(page.locator('#trip-editor-metadata-form').getByLabel('Name')).toHaveValue(draftName);

    await page.setViewportSize({ width: 1280, height: 900 });
    await expect(page.getByRole('navigation', { name: 'Trip editor sections' })).toHaveCount(0);
    await expect(page.locator('#trip-editor-metadata-form').getByLabel('Name')).toHaveValue(draftName);

    await page.getByRole('button', { name: 'Cancel / Reset' }).click();
    await expect(page.locator('#trip-editor-metadata-form').getByLabel('Name')).toHaveValue(originalName);
    await page.locator('.trip-editor-surface--docked').getByRole('button', { name: 'Close' }).click();
    await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Edit Trip' })).toBeVisible();
  });

  test('dirty metadata phone-desktop-phone resize preserves draft and close prompt', async ({ page }) => {
    await signIn(page);
    await openEditorAt(page, { width: 390, height: 844 });

    await page.getByRole('button', { name: 'Edit Trip' }).click();
    const nameInput = page.locator('#trip-editor-metadata-form').getByLabel('Name');
    const originalName = await nameInput.inputValue();
    const draftName = `${originalName} phone resize draft`;
    await nameInput.fill(draftName);
    await expectDrawerState(page, 'expanded-edit');

    await page.setViewportSize({ width: 1280, height: 900 });
    await expect(page.getByRole('navigation', { name: 'Trip editor sections' })).toHaveCount(0);
    await expect(page.locator('#trip-editor-metadata-form').getByLabel('Name')).toHaveValue(draftName);

    await page.setViewportSize({ width: 390, height: 844 });
    await expect(page.getByRole('navigation', { name: 'Trip editor sections' })).toBeVisible();
    await expect(drawerTab(page, 'Trip')).toHaveAttribute('aria-pressed', 'true');
    await expectDrawerState(page, 'expanded-edit');
    await expect(page.locator('#trip-editor-metadata-form').getByLabel('Name')).toHaveValue(draftName);

    await page.locator('.trip-editor-surface--docked').getByRole('button', { name: 'Close' }).click();
    const discardDialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(discardDialog).toContainText('Discard unsaved changes and close this editor?');
    await discardDialog.getByRole('button', { name: 'Keep editing' }).click();
    await expect(page.locator('#trip-editor-metadata-form').getByLabel('Name')).toHaveValue(draftName);
  });

  test('protected tablet and intermediate widths do not activate the drawer', async ({ page }, testInfo) => {
    await signIn(page);
    for (const viewport of [
      { name: 'exact-boundary-641', width: 641, height: 900 },
      { name: 'intermediate-700', width: 700, height: 900 },
      { name: 'tablet-768', width: 768, height: 1024 }
    ]) {
      await openEditorAt(page, { width: viewport.width, height: viewport.height });
      await expect(page.getByRole('navigation', { name: 'Trip editor sections' })).toHaveCount(0);
      await expect(page.locator('.trip-editor-sidebar--mobile-drawer')).toHaveCount(0);
      await expect(page.locator('.trip-editor-surface--docked .trip-editor-metadata')).toBeVisible();
      await expect(page.locator('.trip-editor-map-shell > .trip-editor-map-search')).toBeVisible();
      await capture(page, testInfo, `${viewport.name}-drawer-not-active`);
    }
  });

  test('desktop wide keeps the accepted sidebar and map shell', async ({ page }, testInfo) => {
    await signIn(page);
    await openEditorAt(page, { width: 1440, height: 1000 });

    await expect(page.getByRole('navigation', { name: 'Trip editor sections' })).toHaveCount(0);
    await expect(page.locator('.trip-editor-sidebar--mobile-drawer')).toHaveCount(0);
    await expect(page.locator('.trip-editor-surface--docked .trip-editor-metadata')).toBeVisible();
    await expect(page.locator('.trip-editor-map-shell > .trip-editor-map-search')).toBeVisible();

    const metrics = await page.evaluate(() => {
      const sidebar = document.querySelector<HTMLElement>('.trip-editor-sidebar')?.getBoundingClientRect();
      const map = document.querySelector<HTMLElement>('.trip-editor-map-shell')?.getBoundingClientRect();
      return { mapLeft: map?.left ?? 0, sidebarLeft: sidebar?.left ?? 0 };
    });
    expect(metrics.sidebarLeft).toBeLessThan(metrics.mapLeft);
    await capture(page, testInfo, 'desktop-wide-accepted-layout');
  });
});

async function openEditorAt(page: Page, viewport: { width: number; height: number }): Promise<void> {
  await page.setViewportSize(viewport);
  await page.goto(absoluteUrl(editorPath));
  await expectMountedWorkspace(page);
}

// Opens a read-only state variant for Trip summary assertions.
async function openEditorWithTripSummaryFixture(
  page: Page,
  viewport: { width: number; height: number },
  configureState: (state: MutableEditorState) => void
): Promise<void> {
  await page.setViewportSize(viewport);
  await page.unroute(editorApiMatcher).catch(() => undefined);
  const state = await loadEditorStateFixture(page) as MutableEditorState;
  configureState(state);
  await page.route(editorApiMatcher, async route => {
    if (route.request().method() !== 'GET') {
      throw new Error(`Unexpected Trip summary fixture mutation ${route.request().method()} ${route.request().url()}`);
    }

    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
  });
  await page.goto(absoluteUrl(editorPath));
  await expectMountedWorkspace(page);
}

async function expectDrawerState(page: Page, state: string): Promise<void> {
  await expect(page.locator('.trip-editor-sidebar--mobile-drawer')).toHaveAttribute('data-mobile-drawer-state', state);
}

// Exercises a visible drawer control and verifies the resulting public state.
async function clickAndExpectDrawerState(page: Page, controlName: string, state: string): Promise<void> {
  await page.getByRole('button', { name: controlName, exact: true }).click();
  await expectDrawerState(page, state);
}

const drawerHeight = (page: Page) => page.locator('.trip-editor-sidebar--mobile-drawer').evaluate(element => element.getBoundingClientRect().height);

async function expectDrawerHeight(page: Page, expected: { min: number; max: number }): Promise<void> {
  const height = await drawerHeight(page);
  expect(height).toBeGreaterThanOrEqual(expected.min);
  expect(height).toBeLessThanOrEqual(expected.max);
}

async function expectHeightCloseTo(page: Page, expectedHeight: number): Promise<void> {
  const height = await drawerHeight(page);
  expect(Math.abs(height - expectedHeight)).toBeLessThanOrEqual(2);
}

async function expectCompactDrawerChrome(page: Page): Promise<void> {
  const metrics = await page.evaluate(() => {
    const stateControl = document.querySelector<HTMLElement>('.trip-editor-mobile-drawer__state-controls button')?.getBoundingClientRect();
    const tabControl = document.querySelector<HTMLElement>('.trip-editor-mobile-drawer__tabs button')?.getBoundingClientRect();
    return {
      stateControlHeight: stateControl?.height ?? 0,
      tabControlHeight: tabControl?.height ?? 0
    };
  });
  expect(metrics.stateControlHeight).toBeLessThanOrEqual(34);
  expect(metrics.tabControlHeight).toBeLessThanOrEqual(34);
  expect(Math.abs(metrics.tabControlHeight - metrics.stateControlHeight)).toBeLessThanOrEqual(4);
}

async function expectOpaqueStickyChrome(page: Page): Promise<void> {
  const chrome = await page.evaluate(() => {
    const header = document.querySelector<HTMLElement>('.trip-editor-sidebar--mobile-drawer .trip-editor-surface__header');
    const footer = document.querySelector<HTMLElement>('.trip-editor-sidebar--mobile-drawer .trip-editor-surface__footer');
    return [header, footer].map(element => {
      const backgroundColor = element ? getComputedStyle(element).backgroundColor : '';
      return {
        backgroundColor,
        isOpaque: backgroundColor !== 'transparent' && !backgroundColor.endsWith(', 0)') && !backgroundColor.includes('/ 0')
      };
    });
  });
  expect(chrome).toHaveLength(2);
  for (const item of chrome) {
    expect(item.backgroundColor.length).toBeGreaterThan(0);
    expect(item.isOpaque).toBe(true);
  }
}

// Proves the map-work actions are the topmost focusable targets without covering the Peek drawer.
async function expectMapWorkToolbarHitTesting(page: Page): Promise<void> {
  const toolbar = page.getByRole('region', { name: 'Map work' });
  const done = toolbar.getByRole('button', { name: 'Done' });
  const cancel = toolbar.getByRole('button', { name: 'Cancel' });
  await expect(done).toBeVisible();
  await expect(cancel).toBeVisible();

  for (const button of [done, cancel]) {
    await button.focus();
    await expect(button).toBeFocused();
    const hitTargetIsButton = await button.evaluate(element => {
      const rect = element.getBoundingClientRect();
      const hitTarget = document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
      return hitTarget === element || element.contains(hitTarget);
    });
    expect(hitTargetIsButton).toBe(true);
  }

  const geometry = await page.evaluate(() => {
    const map = document.querySelector<HTMLElement>('.trip-editor-map')?.getBoundingClientRect();
    const mapWork = document.querySelector<HTMLElement>('.trip-editor-map-work-toolbar')?.getBoundingClientRect();
    const drawer = document.querySelector<HTMLElement>('.trip-editor-sidebar--mobile-drawer')?.getBoundingClientRect();
    const utilities = document.querySelector<HTMLElement>('.trip-editor-map-utilities')?.getBoundingClientRect();
    return {
      mapTop: map?.top ?? 0,
      mapBottom: map?.bottom ?? 0,
      toolbarTop: mapWork?.top ?? 0,
      toolbarBottom: mapWork?.bottom ?? 0,
      drawerTop: drawer?.top ?? 0,
      utilitiesBottom: utilities?.bottom ?? 0
    };
  });
  expect(geometry.toolbarTop).toBeGreaterThanOrEqual(geometry.mapTop);
  expect(geometry.toolbarBottom).toBeLessThanOrEqual(geometry.mapBottom);
  expect(geometry.toolbarBottom).toBeLessThanOrEqual(geometry.drawerTop);
  expect(geometry.toolbarTop).toBeGreaterThanOrEqual(geometry.utilitiesBottom);
}

async function expectDirtyTabSwitchGuard(
  page: Page,
  options: {
    activeHeading: string | RegExp;
    dirtyFieldLabel: string;
    draftValue: string;
    owningTab: string;
    promptText: string;
    targetTab: string;
  }
): Promise<void> {
  const form = activeEditorSurface(page);
  const field = form.getByLabel(options.dirtyFieldLabel);
  await expect(page.getByRole('heading', { name: options.activeHeading })).toBeVisible();
  await field.fill(options.draftValue);

  await drawerTab(page, options.targetTab).click();
  const keepDialog = page.getByRole('dialog', { name: 'Discard changes?' });
  await expect(keepDialog).toContainText(options.promptText);
  await keepDialog.getByRole('button', { name: 'Keep editing' }).click();
  await expect(drawerTab(page, options.owningTab)).toHaveAttribute('aria-pressed', 'true');
  await expect(field).toHaveValue(options.draftValue);

  await drawerTab(page, options.targetTab).click();
  const discardDialog = page.getByRole('dialog', { name: 'Discard changes?' });
  await expect(discardDialog).toContainText(options.promptText);
  await discardDialog.getByRole('button', { name: 'Discard' }).click();
  await expect(drawerTab(page, options.targetTab)).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('heading', { name: options.activeHeading })).toHaveCount(0);
}

const drawerTab = (page: Page, name: string) => page.getByRole('navigation', { name: 'Trip editor sections' }).getByRole('button', { name, exact: true });

async function tapFirstSavedPlaceMarker(page: Page): Promise<void> {
  const marker = page.locator('[data-place-marker-icon]').first();
  await expect(marker).toBeVisible();
  await marker.evaluate(element => {
    element.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
  });
}

const expectSelectedMarkerCount = async (page: Page, count: number) => expect(page.locator('.trip-editor-map-marker--selected [data-place-marker-icon]')).toHaveCount(count);

async function expectMapFirstPhoneLayout(page: Page): Promise<void> {
  await expectInitializedTripMap(page);
  const metrics = await page.evaluate(() => {
    const workspace = document.querySelector<HTMLElement>('.trip-editor-workspace')?.getBoundingClientRect();
    const map = document.querySelector<HTMLElement>('.trip-editor-map')?.getBoundingClientRect();
    const drawer = document.querySelector<HTMLElement>('.trip-editor-sidebar--mobile-drawer')?.getBoundingClientRect();
    const toolbar = document.querySelector<HTMLElement>('.trip-editor-toolbar, .trip-editor-map-work-toolbar')?.getBoundingClientRect();
    const utilities = document.querySelector<HTMLElement>('.trip-editor-map-utilities')?.getBoundingClientRect();
    return {
      bodyWidth: document.body.scrollWidth,
      clientWidth: document.documentElement.clientWidth,
      drawerTop: drawer?.top ?? 0,
      mapBottom: map?.bottom ?? 0,
      mapHeight: map?.height ?? 0,
      mapTop: map?.top ?? 0,
      toolbarTop: toolbar?.top ?? 0,
      utilitiesBottom: utilities?.bottom ?? 0,
      viewportHeight: window.innerHeight,
      workspaceHeight: workspace?.height ?? 0,
      workspaceTop: workspace?.top ?? 0
    };
  });

  expect(metrics.bodyWidth, 'Phone layout should not create horizontal overflow.').toBeLessThanOrEqual(metrics.clientWidth + 1);
  expect(metrics.workspaceHeight, 'Phone workspace should be viewport-bounded.').toBeLessThanOrEqual(metrics.viewportHeight + 2);
  expect(metrics.workspaceTop + metrics.workspaceHeight, 'Phone workspace should end within its viewport-height allocation.').toBeLessThanOrEqual(metrics.workspaceTop + metrics.viewportHeight + 2);
  expect(metrics.mapTop, 'Map should begin at the top of the phone workspace.').toBeLessThanOrEqual(metrics.workspaceTop + 2);
  expect(metrics.mapHeight, 'Map should remain the primary phone workspace.').toBeGreaterThan(metrics.viewportHeight * 0.72);
  expect(metrics.drawerTop, 'Drawer should sit over the lower part of the map.').toBeGreaterThan(metrics.viewportHeight * 0.45);
  expect(metrics.mapBottom, 'Map should continue behind the drawer instead of being pushed below it.').toBeGreaterThan(metrics.drawerTop);
  expect(metrics.toolbarTop, 'Editor toolbar should start below the top-right map utilities.').toBeGreaterThanOrEqual(metrics.utilitiesBottom);
}

async function expectContainedSearch(page: Page): Promise<void> {
  const metrics = await page.locator('.trip-editor-map-search__results').evaluate(element => {
    const styles = getComputedStyle(element);
    return {
      maxHeight: styles.maxHeight,
      overflowY: styles.overflowY,
      right: element.getBoundingClientRect().right,
      viewportWidth: window.innerWidth
    };
  });
  expect(metrics.maxHeight).not.toBe('none');
  expect(['auto', 'scroll']).toContain(metrics.overflowY);
  expect(metrics.right).toBeLessThanOrEqual(metrics.viewportWidth + 1);
}

async function routeGeocode(page: Page): Promise<void> {
  await page.route(geocodePath, async (route: Route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        query: new URL(route.request().url()).searchParams.get('q') ?? '',
        attribution: 'Drawer search attribution',
        results: [{
          id: 'drawer:search-place',
          provider: 'drawer',
          name: 'Drawer Search Place',
          displayName: 'Drawer Search Place, Athens',
          address: 'Athens, Greece',
          category: 'tourism',
          type: 'attraction',
          latitude: 37.9715,
          longitude: 23.7257
        }]
      })
    });
  });
}

async function capture(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  await page.screenshot({ fullPage: true, path: testInfo.outputPath('screenshots', `${name}.png`) });
}
