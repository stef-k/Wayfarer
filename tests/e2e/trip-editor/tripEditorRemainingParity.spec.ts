import { expect, test, type Locator, type Page } from '@playwright/test';
import { absoluteUrl, editorApiPath, editorPath, expectMountedWorkspace, signIn, tripMap } from './tripEditorTestUtils';

test.describe.serial('Trip Editor remaining parity verification', () => {
  test('public trip and share-progress saves return success from editor endpoints', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    const publicTrip = page.getByRole('checkbox', { name: 'Public trip', exact: true });
    const shareProgress = page.getByLabel('Show visit progress on public trip');
    const initiallyPublic = await publicTrip.isChecked();
    const initiallyShared = await shareProgress.isChecked();

    try {
      if (initiallyPublic) {
        await setPublicState(page, false);
      }

      const metadataResponse = await setPublicState(page, true);
      await expect(metadataResponse).toMatchObject({ status: 200, method: 'PATCH' });
      await expect(shareProgress).toBeVisible();
      await expect(shareProgress).toBeEnabled();

      if (await shareProgress.isChecked()) {
        await setShareProgressState(page, false);
      }

      const shareResponse = await setShareProgressState(page, true);
      await expect(shareResponse).toMatchObject({ status: 200, method: 'PATCH' });
      await expect(page.locator('.trip-editor-form-error')).toHaveCount(0);
    } finally {
      if (!page.isClosed() && (await publicTrip.isChecked())) {
        await setShareProgressState(page, initiallyShared);
      }
      if (!page.isClosed()) {
        await setPublicState(page, initiallyPublic);
      }
    }
  });

  test('sidebar search stays near the top and filters without backend search', async ({ page }) => {
    const forbiddenRequests: string[] = [];
    page.on('request', request => {
      if (/nominatim|geosearch|search-add|searchadd|\/search(?:[/?#]|$)/i.test(request.url())) {
        forbiddenRequests.push(request.url());
      }
    });

    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    const searchPanel = page.locator('.trip-editor-sidebar-search');
    const metadataPanel = page.locator('#trip-editor-metadata-form');
    await expect(searchPanel).toBeVisible();
    await expect(searchPanel).toHaveCSS('position', 'sticky');
    await expect(searchPanel).toHaveCSS('opacity', '1');
    await expect(searchPanel).not.toHaveCSS('background-color', 'rgba(0, 0, 0, 0)');
    await expect(searchPanel).not.toHaveCSS('box-shadow', 'none');
    expect((await searchPanel.boundingBox())!.y).toBeLessThan((await metadataPanel.boundingBox())!.y);

    await page.getByLabel('Sidebar search').fill('not-a-real-trip-editor-match');
    await expect(searchPanel.locator('.trip-editor-empty-state')).toContainText('No matching regions, places, areas, or segments.');
    expect(forbiddenRequests).toEqual([]);
  });

  test('map utilities show zoom, measure distance, and copy the current map link', async ({ page, context }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: absoluteUrl('/') });
    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    const map = tripMap(page);
    const zoomStatus = page.locator('.trip-editor-map-utilities__zoom');
    await expect(zoomStatus).toHaveText(/Zoom: \d+/);
    const initialZoom = await zoomStatus.textContent();
    await map.locator('.leaflet-control-zoom-in').click();
    await expect(zoomStatus).not.toHaveText(initialZoom ?? '');

    await utilityButton(page, 'Measure distance').click();
    const box = await map.boundingBox();
    expect(box).not.toBeNull();
    await page.mouse.click(box!.x + box!.width * 0.45, box!.y + box!.height * 0.45);
    await page.mouse.click(box!.x + box!.width * 0.55, box!.y + box!.height * 0.55);
    await expect(page.locator('.trip-editor-map-distance-label')).toContainText(/km/);

    await utilityButton(page, 'Copy map link').click();
    const copyFeedback = page.locator('.trip-editor-map-copy-feedback');
    await expect(copyFeedback).toHaveText('Map link copied to clipboard');
    await expect(copyFeedback).toHaveCount(1);
    await expect(utilityButton(page, 'Map link copied')).toBeVisible();
    await utilityButton(page, 'Map link copied').click();
    await expect(copyFeedback).toHaveCount(1);
    await expect(copyFeedback).toBeVisible();
    await expect(utilityButton(page, 'Copy map link')).toBeVisible({ timeout: 2500 });
    await expect(copyFeedback).toHaveCount(0, { timeout: 2500 });

    const clipboardText = await page.evaluate(() => navigator.clipboard.readText());
    expect(clipboardText).toContain(editorPath);
    expect(clipboardText).toMatch(/[?&]lat=/);
    expect(clipboardText).toMatch(/[?&]lng=/);
    expect(clipboardText).toMatch(/[?&]zoom=/);
  });
});

async function waitForEditorMutation(page: Page, suffix: string, method: string): Promise<{ method: string; status: number; url: string; body: string }> {
  const response = await page.waitForResponse(response => response.url().includes(`${editorApiPath}${suffix}`) && response.request().method() === method);
  return {
    method: response.request().method(),
    status: response.status(),
    url: response.url(),
    body: await response.text()
  };
}

async function expectSaved(page: Page): Promise<void> {
  await expect(page.locator('.trip-editor-save-state').filter({ hasText: /Saved/i }).first()).toBeVisible();
}

function utilityButton(page: Page, name: string): Locator {
  return page.locator('.trip-editor-map-utilities').getByRole('button', { name });
}

async function setPublicState(page: Page, enabled: boolean): Promise<{ method: string; status: number; url: string; body: string } | null> {
  const publicTrip = page.getByRole('checkbox', { name: 'Public trip', exact: true });
  if ((await publicTrip.isChecked()) === enabled) {
    return null;
  }

  const response = waitForEditorMutation(page, '/metadata', 'PATCH');
  if (enabled) {
    await publicTrip.check();
  } else {
    await publicTrip.uncheck();
  }
  await page.getByRole('button', { name: 'Save & Continue' }).click();
  await expectSaved(page);
  return await response;
}

async function setShareProgressState(page: Page, enabled: boolean): Promise<{ method: string; status: number; url: string; body: string } | null> {
  const shareProgress = page.getByLabel('Show visit progress on public trip');
  if ((await shareProgress.isChecked()) === enabled) {
    return null;
  }

  const response = waitForEditorMutation(page, '/share-progress', 'PATCH');
  if (enabled) {
    await shareProgress.check();
  } else {
    await shareProgress.uncheck();
  }
  await page.getByRole('button', { name: 'Save & Continue' }).click();
  await expectSaved(page);
  return await response;
}
