import { expect, test } from '@playwright/test';
import { absoluteUrl, expectMountedWorkspace, signIn, tripMap, uniqueName } from './tripEditorTestUtils';
import { dragViewport, expectViewportFields, expectViewportUrl, readViewport } from './tripEditorMapViewportAssertions';

/** Real persistence on a uniquely named owned Trip; no metadata response is fulfilled or intercepted. */
test('drag and zoom capture the draft, real Save persists it, and clean reload uses the saved default', async ({ page }, testInfo) => {
  test.setTimeout(60_000);
  await signIn(page);
  await page.goto(absoluteUrl('/User/Trip/Create'));
  await page.getByLabel('Name', { exact: true }).fill(uniqueName('PW 592 viewport'));
  await page.getByRole('button', { name: 'Create & Edit' }).click();
  await page.waitForURL(/\/User\/Trip\/Edit\/[a-f0-9-]+$/i);
  const path = new URL(page.url()).pathname;
  const id = path.split('/').at(-1)!;
  const endpoint = `/api/trips/${id}/editor`;
  let token = '';
  let primaryFailure: unknown;
  try {
    await expectMountedWorkspace(page);
    token = await page.locator('#trip-editor-antiforgery input').inputValue();
    // Establish a known saved view using the existing manual metadata fields and real endpoint.
    await page.getByLabel('Center Latitude').fill('37.9838');
    await page.getByLabel('Center Longitude').fill('23.7275');
    await page.getByRole('spinbutton', { name: 'Zoom' }).fill('9');
    const seeded = page.waitForResponse(response => response.url().toLowerCase().endsWith(`${endpoint}/metadata`) && response.request().method() === 'PATCH', { timeout: 10_000 });
    await page.getByRole('button', { name: 'Save & Continue' }).click();
    expect((await seeded).ok()).toBeTruthy();
    await page.goto(absoluteUrl(`${path}?context=viewport-proof#settings`));
    await expectMountedWorkspace(page);
    expect(new URL(page.url()).searchParams.has('lat')).toBeFalsy();
    await page.evaluate(() => history.replaceState({ retained: '592' }, '', location.href));
    const historyLength = await page.evaluate(() => history.length);
    const before = await readViewport(page);
    const patches: unknown[] = [];
    page.on('request', request => {
      if (request.url().toLowerCase().endsWith(`${endpoint}/metadata`) && request.method() === 'PATCH') patches.push(request.postDataJSON());
    });

    // Capture while Settings is closed, then show the draft owner and save through its actual UI.
    await page.locator('.trip-editor-surface--docked').getByRole('button', { name: 'Close', exact: true }).click();
    await dragViewport(page);
    await tripMap(page).getByRole('button', { name: 'Zoom in', exact: true }).click();
    await expect.poll(async () => (await readViewport(page)).zoom).toBe(String(Number(before.zoom) + 1));
    await expectViewportUrl(page);
    const captured = await readViewport(page);
    await page.getByRole('button', { name: 'Edit Trip', exact: true }).click();
    await expectViewportFields(page, captured);
    await expect(page.locator('.trip-editor-surface--docked .trip-editor-save-state')).toHaveText('Unsaved changes');
    expect(patches).toHaveLength(0);
    expect(new URL(page.url()).searchParams.get('context')).toBe('viewport-proof');
    expect(new URL(page.url()).hash).toBe('#settings');
    expect(await page.evaluate(() => history.state)).toEqual({ retained: '592' });
    expect(await page.evaluate(() => history.length)).toBe(historyLength);
    await page.screenshot({ fullPage: true, path: testInfo.outputPath('screenshots', 'drag-zoom-dirty.png') });

    const saved = page.waitForResponse(response => response.url().toLowerCase().endsWith(`${endpoint}/metadata`) && response.request().method() === 'PATCH', { timeout: 10_000 });
    await page.getByRole('button', { name: 'Save & Continue' }).click();
    const response = await saved;
    expect(response.ok()).toBeTruthy();
    expect(patches).toHaveLength(1);
    expect(patches[0]).toMatchObject({ center: { latitude: Number(captured.latitude), longitude: Number(captured.longitude) }, zoom: Number(captured.zoom) });
    await expect(page.locator('.trip-editor-surface--docked .trip-editor-save-state')).toContainText('Saved');
    const reread = await page.request.get(absoluteUrl(endpoint));
    expect(reread.ok()).toBeTruthy();
    expect((await reread.json()).metadata).toMatchObject({ center: { latitude: Number(captured.latitude), longitude: Number(captured.longitude) }, zoom: Number(captured.zoom) });
    await page.goto(absoluteUrl(path));
    await expectMountedWorkspace(page);
    await expect.poll(() => readViewport(page)).toEqual(captured);
    expect(new URL(page.url()).search).toBe('');
    await page.screenshot({ fullPage: true, path: testInfo.outputPath('screenshots', 'saved-default-reload.png') });
    await testInfo.attach('real-metadata-persistence', { contentType: 'application/json', body: JSON.stringify({ tripId: id, captured, request: patches[0], status: response.status(), reloadedUrl: page.url() }, null, 2) });
  } catch (error) {
    primaryFailure = error;
    throw error;
  } finally {
    // Delete only this test's exact Trip via the authenticated application cleanup boundary.
    try {
      if (!token) token = await page.locator('input[name="__RequestVerificationToken"]').first().inputValue();
      const deleted = await page.request.post(absoluteUrl(`/User/Trip/Delete/${id}`), { form: { __RequestVerificationToken: token } });
      expect(deleted.ok(), 'Owned Trip cleanup must succeed').toBeTruthy();
      expect((await page.request.get(absoluteUrl(endpoint))).status()).toBe(404);
    } catch (cleanupError) {
      if (!primaryFailure) throw cleanupError;
      await testInfo.attach('cleanup-failure', { body: `Owned Trip ${id}: ${String(cleanupError)}`, contentType: 'text/plain' });
    }
  }
});
