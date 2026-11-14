import { expect, test } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** One rendered copy journey; concurrency, authority and fidelity matrices stay at client/relational seams. */
test('Batch 5 copy selected block into existing populated Trip', async ({ page }, testInfo) => {
  test.setTimeout(180_000); page.setDefaultTimeout(15_000);
  const credentials = loadSharedLayoutConfig(); const destination = process.env.WAYFARER_E2E_TRIP_ID!;
  const evidence = process.env.WAYFARER_COMPOSITION_EVIDENCE ?? testInfo.outputPath('captures');
  await mkdir(evidence, { recursive: true });
  await page.goto('/Identity/Account/Login');
  await page.getByLabel('Username').fill(credentials.username); await page.getByLabel('Password').fill(credentials.password);
  await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')), page.getByRole('button', { name: 'Log in' }).click()]);
  await page.goto(`/User/Trip/Edit/${destination}`);
  await expect(page.locator('.trip-editor-workspace')).toBeVisible();
  const token = await page.locator('#trip-editor-antiforgery input').inputValue();
  const post = async (url: string, data: unknown) => {
    const response = await page.request.post(url, { headers: { RequestVerificationToken: token }, data });
    expect(response.ok(), await response.text()).toBeTruthy(); return await response.json();
  };
  const source = (await post('/api/trip-compositions/versions', { sourceA: destination, name: 'Batch 5 coastal ideas' })).tripId;
  const endpoint = `/api/trips/${source}/editor`;
  for (const [name, longitude] of [['Beach', 24], ['Castle', 24.1]] as const) {
    const current = await (await page.request.get(endpoint)).json(); const region = current.regionOrder[0];
    await post(`${endpoint}/regions/${region}/places/conditional`, {
      expected: { regionId: region, places: current.placeOrderByRegionId[region].map((id: string) => ({ id, displayOrder: current.placesById[id].originalValues.displayOrder })) },
      value: { name, notesHtml: `<p>${name} idea</p>`, location: { latitude: 38, longitude }, iconName: 'marker', markerColor: 'bg-blue', address: '', reverseGeocode: false }
    });
  }
  const beforeSource = await (await page.request.get(endpoint)).json();
  const before = await (await page.request.get(`/api/trips/${destination}/editor`)).json();
  const region = before.regionOrder[0]; const reference = before.placeOrderByRegionId[region][0];
  await page.goto('/User/Trip');
  await page.getByRole('link', { name: 'Copy to existing Trip', exact: true }).click();
  await page.getByLabel('Find source Trip', { exact: true }).fill('Batch 5 coastal ideas');
  await page.getByRole('button', { name: 'Search sources', exact: true }).click();
  await expect(page.getByLabel('Source Trip', { exact: true }).locator(`option[value="${source}"]`)).toHaveCount(1);
  await page.getByLabel('Source Trip', { exact: true }).selectOption(source);
  await page.getByLabel('Destination Trip', { exact: true }).selectOption(destination);
  await page.getByRole('button', { name: 'Preview copy choices', exact: true }).click();
  const tree = page.getByRole('region', { name: 'Source B hierarchy' });
  const choices = page.getByRole('region', { name: 'Reviewed copy choices' });
  const map = page.getByRole('region', { name: 'Source and existing destination Trip map' });
  await expect(map.locator('.leaflet-overlay-pane path').first()).toBeVisible();
  await page.setViewportSize({ width: 1440, height: 1000 });
  for (const name of ['Beach', 'Castle']) {
    await tree.getByRole('button', { name: new RegExp(`Place · ${name}`) }).click();
    const item = choices.locator('fieldset.composition-choice').filter({ has: page.locator('legend', { hasText: `place · ${name}` }) });
    await item.getByLabel('Destination Region', { exact: true }).selectOption(region);
    await item.getByLabel('Insertion position (independent of Region)').selectOption('after');
    await item.getByLabel('After this current destination place').selectOption(reference);
    await expect(item.locator('.composition-result')).toContainText('After');
  }
  await page.screenshot({ path: `${evidence}/01-copy-review-desktop.png`, fullPage: true });
  await page.setViewportSize({ width: 430, height: 932 });
  const apply = page.getByRole('button', { name: 'Apply reviewed copy to destination', exact: true });
  await apply.focus(); await expect(apply).toBeFocused();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBeTruthy();
  await page.screenshot({ path: `${evidence}/02-copy-review-narrow-focus.png`, fullPage: true });
  const saving = page.waitForResponse(r => r.url().endsWith('/api/trip-compositions/copy') && r.request().method() === 'POST');
  await page.keyboard.press('Enter');
  const response = await saving; expect(response.ok(), await response.text()).toBeTruthy();
  await expect(page.getByText('Saved to destination Trip.', { exact: true })).toBeVisible();
  const saved = await (await page.request.get(`/api/trips/${destination}/editor`)).json();
  expect(saved.tripId).toBe(destination);
  const names = saved.placeOrderByRegionId[region].map((id: string) => saved.placesById[id].name);
  expect(names).toEqual([before.placesById[reference].name, 'Beach', 'Castle', ...before.placeOrderByRegionId[region].slice(1).map((id: string) => before.placesById[id].name)]);
  expect((await (await page.request.get(endpoint)).json()).placeOrderByRegionId).toEqual(beforeSource.placeOrderByRegionId);
  await page.screenshot({ path: `${evidence}/03-copy-confirmed.png`, fullPage: true });
  await page.getByRole('link', { name: 'Edit / inspect destination Trip', exact: true }).click();
  await expect(page.locator('.trip-editor-workspace')).toBeVisible(); await expect(page).toHaveURL(new RegExp(`/Edit/${destination}$`));
  await writeFile(`${evidence}/copy-observation.json`, JSON.stringify({ source, destination, names, savedStatus: response.status(), narrowWidth: 430,
    keyboardActivation: true, sourceOrderUnchanged: true, destinationEditingLink: true, scope: 'Development owned-source representative copy; no native zoom or full comparison campaign' }, null, 2));
});
