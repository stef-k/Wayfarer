import { expect, test, type Locator, type Page } from '@playwright/test';
import { absoluteUrl, editorApiPath, editorPath, expectMountedWorkspace, signIn } from './tripEditorTestUtils';

test.describe.serial('Trip Editor remaining parity verification', () => {
  test('public trip and share-progress saves use editor endpoints successfully', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    const metadataResponse = waitForEditorMutation(page, '/metadata', 'PATCH');
    const publicTrip = page.getByRole('checkbox', { name: 'Public trip', exact: true });
    if (await publicTrip.isChecked()) {
      await publicTrip.uncheck();
      await page.getByRole('button', { name: 'Save & Continue' }).click();
      await expectSaved(page);
    }

    await publicTrip.check();
    await page.getByRole('button', { name: 'Save & Continue' }).click();
    await expectSaved(page);
    await expect(await metadataResponse).toMatchObject({ status: 200, method: 'PATCH' });

    const shareResponse = waitForEditorMutation(page, '/share-progress', 'PATCH');
    const shareProgress = page.getByLabel('Show visit progress on public trip');
    if (await shareProgress.isChecked()) {
      await shareProgress.uncheck();
      await page.getByRole('button', { name: 'Save & Continue' }).click();
      await expectSaved(page);
    }

    await shareProgress.check();
    await page.getByRole('button', { name: 'Save & Continue' }).click();
    await expectSaved(page);
    await expect(await shareResponse).toMatchObject({ status: 200, method: 'PATCH' });
    await expect(page.locator('.trip-editor-form-error')).toHaveCount(0);
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
    expect((await searchPanel.boundingBox())!.y).toBeLessThan((await metadataPanel.boundingBox())!.y);

    await page.getByLabel('Sidebar search').fill('not-a-real-trip-editor-match');
    await expect(page.locator('.trip-editor-empty-state')).toContainText('No matching regions, places, areas, or segments.');
    expect(forbiddenRequests).toEqual([]);
  });

  test('map utilities show zoom, measure distance, and copy the current map link', async ({ page, context }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: absoluteUrl('/') });
    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    const map = page.getByLabel('Read-only trip map');
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
