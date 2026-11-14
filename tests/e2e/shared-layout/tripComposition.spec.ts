import { expect, test, type Page } from '@playwright/test';
import { appendFile, mkdir, writeFile } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** One integrated journey at the established managed owner/account/database seam. State/race matrices remain client tests. */
test('Batch 4 Layout B start copy edit return resume and Finish', async ({ page }, testInfo) => {
  test.setTimeout(300_000);
  page.setDefaultTimeout(15_000);
  const credentials = loadSharedLayoutConfig();
  const a = process.env.WAYFARER_E2E_TRIP_ID!;
  const evidence = process.env.WAYFARER_COMPOSITION_EVIDENCE ?? testInfo.outputPath('captures');
  await mkdir(evidence, { recursive: true });
  const record = async (step: string, detail: unknown = {}) => {
    await appendFile(`${evidence}/observations.jsonl`, JSON.stringify({ at: new Date().toISOString(), step, detail }) + '\n');
  };
  const signIn = async () => {
    await page.goto('/Identity/Account/Login');
    await page.getByLabel('Username').fill(credentials.username); await page.getByLabel('Password').fill(credentials.password);
    await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')), page.getByRole('button', { name: 'Log in' }).click()]);
  };
  await signIn(); await page.setViewportSize({ width: 1536, height: 1024 });
  await record('Signed in to managed disposable account');
  page.on('pageerror', error => { void record('Browser pageerror', error.message); });
  page.on('console', message => { if (['error', 'warning'].includes(message.type())) void record(`Browser console ${message.type()}`, message.text()); });
  page.on('response', response => {
    const path = new URL(response.url()).pathname;
    if (path.startsWith('/api/')) void record('Passive API response', { path, method: response.request().method(), status: response.status() });
    if (response.status() >= 400 && !path.includes('/Identity/')) void response.text().then(body => record('HTTP failure body', { path, status: response.status(), body })).catch(() => {});
  });
  page.on('requestfailed', request => { void record('Browser requestfailed', { path: new URL(request.url()).pathname, method: request.method(), failure: request.failure() }); });
  await record('Preparing A/B fixture through ordinary APIs');
  await page.goto(`/User/Trip/Edit/${a}`);
  await expect(page.locator('.trip-editor-workspace')).toBeVisible();
  const token = await page.locator('#trip-editor-antiforgery input').inputValue();
  const post = async (url: string, data: unknown) => {
    const response = await page.request.post(url, { headers: { RequestVerificationToken: token }, data });
    expect(response.ok(), await response.text()).toBeTruthy(); return await response.json();
  };
  // Prepare a distinct owned B through ordinary production construction/editing APIs.
  const version = await post('/api/trip-compositions/versions', { sourceA: a, name: 'Coastal ideas B' });
  const b = version.tripId;
  const endpoint = `/api/trips/${b}/editor`;
  const initial = await (await page.request.get(endpoint)).json();
  const region = initial.regionOrder[0];
  const from = initial.placeOrderByRegionId[region][0];
  const to = (await post(`${endpoint}/regions/${region}/places/conditional`, {
    expected: { regionId: region, places: initial.placeOrderByRegionId[region].map((id: string) => ({ id, displayOrder: initial.placesById[id].originalValues.displayOrder })) },
    value: { name: 'Coastal viewpoint', notesHtml: '<p>Captured coastal idea</p>', location: { latitude: 38.05, longitude: 24 }, iconName: 'marker', markerColor: 'bg-blue', address: '', reverseGeocode: false }
  })).data;
  const route = (await post(`${endpoint}/segments`, { title: 'Coastal alternative', fromPlaceId: from, toPlaceId: to.id, waypointPlaceIds: [], waypointRouteVertexIndices: [],
    mode: initial.options.transportModes.find((m: { value: string }) => m.value === 'walk')?.value ?? initial.options.transportModes[0].value,
    estimatedDistanceKm: null, estimatedDurationMinutes: null, estimatedDurationSource: 'Automatic', notesHtml: '<p>Full custom route</p>', aggregateConcurrencyToken: null,
    route: { type: 'LineString', coordinates: [[23.73, 37.98], [23.77, 38.1], [24, 38.05]] } })).data;
  await post(`${endpoint}/regions/${region}/areas`, { name: 'Coastal Area with hole', notesHtml: '', fillHex: '#ecb663', geometry: {
    type: 'Polygon', coordinates: [[[23.7, 37.95], [24.02, 37.95], [24.02, 38.12], [23.7, 38.12], [23.7, 37.95]],
      [[23.8, 38], [23.8, 38.03], [23.84, 38.03], [23.84, 38], [23.8, 38]]]
  } });
  await record('1 Starting independent C from prepared A/B', { a, b, route: route.id });
  await page.goto('/User/Trip/Compose');
  await page.getByLabel('Find eligible B', { exact: true }).fill('Coastal ideas B');
  await page.getByRole('button', { name: 'Search sources / refresh unfinished work', exact: true }).click();
  await expect(page.getByLabel('Source B', { exact: true }).locator(`option[value="${b}"]`)).toHaveCount(1);
  await page.getByLabel('Source A', { exact: true }).selectOption(a);
  await page.getByLabel('Source B', { exact: true }).selectOption(b);
  await page.getByLabel('New Trip name (optional)').fill('Coastal comparison C');
  const creation = page.waitForResponse(r => r.url().endsWith('/api/trip-compositions') && r.request().method() === 'POST');
  // Observe the new page's own read; a separate GET would compete with the UI and change the observation.
  const workspaceRead = page.waitForResponse(r => r.request().method() === 'GET'
    && /^\/api\/trip-compositions\/[0-9a-f-]+$/.test(new URL(r.url()).pathname));
  await page.getByRole('button', { name: 'Start New composition', exact: true }).click();
  expect((await creation).ok()).toBeTruthy();
  await expect(page).toHaveURL(/\/User\/Trip\/Compose\/[0-9a-f-]+$/);
  // Read the destination document's workspace response after navigation.
  const composition = new URL(page.url()).pathname.split('/').at(-1)!;
  const response = await workspaceRead;
  expect(new URL(response.url()).pathname).toBe(`/api/trip-compositions/${composition}`);
  expect(response.ok(), await response.text()).toBeTruthy();
  const created = await response.json(); const c = created.tripId;
  expect(created.compositionId).toBe(composition); expect(c).not.toBe(a); expect(c).not.toBe(b);
  const inspector = page.getByRole('region', { name: 'Contextual comparison inspector' });
  const bTree = page.getByRole('region', { name: 'Source B hierarchy' });
  // The unavailable shell also has .composition-workspace; qualify populated map, trees and item controls.
  await test.step('Independent C workspace rendered', async () => {
    const map = page.getByRole('region', { name: 'Shared A B C Trip map' });
    // Lazy composition navigation must load Leaflet CSS itself, including tile containment.
    await expect(map).toHaveCSS('overflow', 'hidden');
    await expect(map.locator('.leaflet-pane').first()).toHaveCSS('position', 'absolute');
    await expect(map.locator('.leaflet-overlay-pane path').first()).toBeVisible();
    await expect(page.getByRole('region', { name: 'Source A hierarchy' }).getByRole('button', { name: /Trip details/ })).toBeVisible();
    await expect(bTree.getByRole('heading', { name: /Coastal ideas B/ })).toBeVisible();
    await bTree.getByText('Browser region', { exact: true }).click();
    await bTree.getByRole('button', { name: /Place · Coastal viewpoint/ }).click();
    await expect(inspector.getByRole('heading', { name: 'Coastal viewpoint', exact: true })).toBeVisible();
    await expect(inspector.getByLabel('Destination Region', { exact: true })).toBeVisible();
    await expect(page.locator('.composition-feedback [role="alert"]')).toHaveCount(0);
  });
  await record('1 Independent C populated map, A/B hierarchies and inspector rendered', { a, b, c, composition });
  await page.screenshot({ path: `${evidence}/01-populated-desktop.png`, fullPage: true });
  const cState = await (await page.request.get(`/api/trips/${c}/editor`)).json();
  await inspector.getByLabel('Destination Region', { exact: true }).selectOption(cState.regionOrder[0]);
  await record('3 Begin actual Tiptap item/block draft editing');
  // Observe the real Tiptap owner as the same source item moves between its independent item/block drafts.
  await inspector.getByLabel('Notes action', { exact: true }).selectOption('edit');
  const notes = inspector.locator('.tiptap[contenteditable="true"]');
  await expect(notes).toBeVisible(); await notes.fill('Independent item Notes draft');
  await inspector.getByRole('button', { name: 'Add this reviewed item to block', exact: true }).click();
  await inspector.getByRole('button', { name: 'Review block · 1 items', exact: true }).click();
  await expect(notes).toHaveText('Independent item Notes draft'); await notes.fill('Applied block Notes draft');
  await inspector.getByRole('button', { name: 'Return to selected item', exact: true }).click();
  await expect(notes).toHaveText('Independent item Notes draft');
  await inspector.getByRole('button', { name: 'Review block · 1 items', exact: true }).click();
  await expect(notes).toHaveText('Applied block Notes draft');
  await record('3 Tiptap item/block ownership and independent drafts observed');
  await page.screenshot({ path: `${evidence}/02-block-notes.png`, fullPage: true });
  await record('2 Begin reviewed block Apply');
  const submittedBlock = page.waitForRequest(r => r.url().endsWith(`/trip-compositions/${composition}/actions`) && r.method() === 'POST');
  let saved = page.waitForResponse(r => r.url().endsWith(`/trip-compositions/${composition}/actions`));
  await inspector.getByRole('button', { name: 'Apply reviewed action to C', exact: true }).click();
  expect((await saved).ok()).toBeTruthy(); await expect(page.locator('.composition-feedback')).toContainText('Saved to C.');
  const blockBody = (await submittedBlock).postDataJSON();
  await record('2 Place block Apply submitted', blockBody);
  expect(JSON.stringify(blockBody.items[0].fields.notes)).toContain('Applied block Notes draft');
  const afterBlock = await (await page.request.get(`/api/trips/${c}/editor`)).json();
  const copiedPlace = Object.values(afterBlock.placesById).find((p: any) => p.name === 'Coastal viewpoint') as any;
  expect(copiedPlace.notesHtml).toContain('Applied block Notes draft');
  await expect(notes).toHaveText('Independent item Notes draft');
  await bTree.getByText('↝ Trip-level Segments', { exact: true }).click();
  await bTree.getByRole('button', { name: /Coastal alternative/ }).click();
  const current = await (await page.request.get(`/api/trips/${c}/editor`)).json();
  const fromC = Object.values(current.placesById).find((p: any) => p.name === 'Browser place') as any;
  const anchors = inspector.getByRole('group', { name: /From · Browser place/ });
  await anchors.getByLabel('Approved current C Place').selectOption(fromC.id);
  saved = page.waitForResponse(r => r.url().endsWith(`/trip-compositions/${composition}/actions`));
  await inspector.getByRole('button', { name: 'Apply reviewed action to C', exact: true }).click();
  expect((await saved).ok()).toBeTruthy(); await expect(page.locator('.composition-feedback')).toContainText('Saved to C.');
  await record('2 Route copied with reviewed existing From and mapped To dependency');
  await record('4 Begin complete route/anchor geometry and overlay inspection');
  // Rendered SVG bounds prove the route fits the visible map, including after keyboard resizing.
  await expectRouteInsideMap(page);
  await observeGeometry(page, evidence, 'desktop-route');
  await observeGeometry(page, evidence, 'desktop-route-anchors', 'path[stroke="#bf6300"][stroke-width="3"]');
  const handle = page.getByRole('separator', { name: 'Resize comparison drawer' });
  await handle.focus(); const before = Number(await handle.getAttribute('aria-valuenow'));
  await page.keyboard.press('ArrowUp'); await expect(handle).toHaveAttribute('aria-valuenow', String(before + 3));
  await expectRouteInsideMap(page);
  await page.keyboard.press('Tab');
  const focused = await page.locator(':focus').evaluate(el => ({ text: el.textContent, outline: getComputedStyle(el).outline, visibleFocus: el.matches(':focus-visible') }));
  await record('4 Keyboard resize and Tab focus', focused);
  await page.screenshot({ path: `${evidence}/03-keyboard-focus.png`, fullPage: true });
  await handle.focus(); const pointerBox = await handle.boundingBox();
  if (!pointerBox) throw new Error('Resize handle unavailable');
  await page.mouse.move(pointerBox.x + 12, pointerBox.y + pointerBox.height / 2); await page.mouse.down();
  await page.mouse.move(pointerBox.x + 12, pointerBox.y - 18); await page.mouse.up();
  await record('4 Pointer drawer resize', { value: await handle.getAttribute('aria-valuenow') });
  await page.getByRole('button', { name: 'Collapse comparison', exact: true }).click();
  await expect(inspector).toBeHidden();
  await page.getByRole('button', { name: 'Show comparison', exact: true }).click();
  await expect(inspector).toBeVisible();
  const layers = page.getByRole('group', { name: 'Map layers', exact: true });
  await layers.getByRole('checkbox', { name: 'A · Read-only source', exact: true }).uncheck();
  await layers.getByRole('checkbox', { name: 'A · Read-only source', exact: true }).check();
  await layers.getByRole('button', { name: 'Focus selection', exact: true }).click();
  await observeGeometry(page, evidence, 'desktop-resized-route');
  await bTree.getByRole('button', { name: /Area · Coastal Area with hole/ }).click();
  await expect(inspector.getByRole('heading', { name: 'Coastal Area with hole', exact: true })).toBeVisible();
  await expect.poll(() => page.locator('.composition-map path[stroke-width="5"]').first().getAttribute('d')).toMatch(/M.*[zZ].*M.*[zZ]/);
  await observeGeometry(page, evidence, 'desktop-area-with-hole', 'path[stroke-width="5"]');
  await page.screenshot({ path: `${evidence}/04-area-hole.png`, fullPage: true });
  await bTree.getByRole('button', { name: /Region · Browser region/ }).click();
  await expect(inspector.getByRole('heading', { name: 'Browser region', exact: true })).toBeVisible();
  await observeGeometry(page, evidence, 'desktop-region-children', 'path[stroke="#bf6300"]:not([stroke-width="3"])');
  await record('4 Source hierarchy Region selection and child geography inspected');
  await bTree.getByRole('button', { name: /Coastal alternative/ }).click();
  await inspector.evaluate(el => { el.scrollTop = 0; }); await page.screenshot({ path: `${evidence}/trip-composition-desktop.png`, fullPage: true });
  await page.setViewportSize({ width: 390, height: 900 });
  await page.getByRole('button', { name: 'Inspector', exact: true }).click();
  await expectRouteInsideMap(page);
  await observeGeometry(page, evidence, 'narrow-route');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.screenshot({ path: `${evidence}/trip-composition-narrow.png`, fullPage: true });
  await page.getByRole('button', { name: 'Source B', exact: true }).click();
  await expect(bTree).toBeVisible();
  await page.screenshot({ path: `${evidence}/trip-composition-narrow-source.png`, fullPage: true });
  await page.getByRole('button', { name: 'Inspector', exact: true }).click();
  await inspector.getByRole('button', { name: 'Apply reviewed action to C', exact: true }).scrollIntoViewIfNeeded();
  await record('4 Narrow inspector scrolling reaches Apply', await inspector.evaluate(el => ({ scrollTop: el.scrollTop, scrollHeight: el.scrollHeight, clientHeight: el.clientHeight })));
  await page.setViewportSize({ width: 1536, height: 1024 });
  // Native X11 keys go to the run-owned Chromium window, never CSS/page-scale/device emulation.
  const zoomBefore = await page.evaluate(() => ({ innerWidth, outerWidth, dpr: devicePixelRatio, scale: visualViewport?.scale, cssZoom: getComputedStyle(document.documentElement).zoom }));
  const nativeTool = process.env.WAYFARER_NATIVE_KEYBOARD_TOOL ?? 'xdotool';
  let nativeWindow = '';
  let zoomDiagnostic = '';
  try {
    const windows = execFileSync(nativeTool, ['search', '--onlyvisible', '--class', 'chrome|chromium'], { encoding: 'utf8' }).trim().split(/\s+/);
    for (const candidate of windows) {
      let pid = Number(execFileSync(nativeTool, ['getwindowpid', candidate], { encoding: 'utf8' }).trim());
      for (let depth = 0; pid > 1 && depth < 20; depth++) {
        if (pid === process.pid) { nativeWindow = candidate; break; }
        const stat = readFileSync(`/proc/${pid}/stat`, 'utf8');
        pid = Number(stat.slice(stat.lastIndexOf(')') + 2).split(' ')[1]);
      }
      if (nativeWindow) break;
    }
    if (!nativeWindow) throw new Error('No visible Chromium window owned by this worker');
    execFileSync(nativeTool, ['windowfocus', '--sync', nativeWindow]);
    execFileSync(nativeTool, ['key', '--window', nativeWindow, '--clearmodifiers', 'ctrl+0']);
    execFileSync(nativeTool, ['key', '--window', nativeWindow, '--clearmodifiers', '--repeat', '5', '--delay', '120', 'ctrl+plus']);
  } catch (error) { zoomDiagnostic = String(error); }
  if (!zoomDiagnostic) await expect.poll(() => page.evaluate(() => devicePixelRatio), { message: 'Native keyboard zoom reaches 200 percent' }).toBe(zoomBefore.dpr * 2);
  const zoomAfter = await page.evaluate(() => ({ innerWidth, outerWidth, dpr: devicePixelRatio, scale: visualViewport?.scale, cssZoom: getComputedStyle(document.documentElement).zoom }));
  const native200 = zoomAfter.dpr / zoomBefore.dpr === 2 && zoomBefore.innerWidth / zoomAfter.innerWidth === 2 && zoomAfter.cssZoom === zoomBefore.cssZoom && zoomAfter.scale === zoomBefore.scale;
  await record('Native browser 200 percent zoom observation', { before: zoomBefore, after: zoomAfter, nativeWindow, native200, zoomDiagnostic });
  await page.screenshot({ path: `${evidence}/05-native-zoom.png`, fullPage: true });
  expect(native200, `Genuine native 200 percent zoom: ${zoomDiagnostic}`).toBeTruthy();
  if (native200) {
    await observeGeometry(page, evidence, 'native-200-route');
    await record('Native 200 percent layout', await page.evaluate(() => ({ innerWidth, scrollWidth: document.documentElement.scrollWidth, overflow: document.documentElement.scrollWidth > innerWidth })));
  }
  if (nativeWindow) execFileSync(nativeTool, ['key', '--window', nativeWindow, '--clearmodifiers', 'ctrl+0']);
  await inspector.getByLabel('Notes action', { exact: true }).selectOption('edit');
  await notes.fill('Retained route Notes draft through ordinary editing');
  await bTree.getByRole('button', { name: /Trip details/ }).click();
  await inspector.getByLabel('Notes action', { exact: true }).selectOption('edit');
  await notes.fill('Retained Trip Notes review draft');
  await record('5 Begin ordinary C metadata/Notes edit with retained review choices');
  await page.getByRole('link', { name: 'Edit C / publish' }).click();
  const metadata = page.locator('#trip-editor-metadata-form'); await expect(metadata).toBeVisible();
  await metadata.getByLabel('Name', { exact: true }).fill('Edited current C');
  await metadata.locator('.tiptap[contenteditable="true"]').fill('Actually saved current C Trip Notes');
  await metadata.getByLabel('Public trip', { exact: true }).check();
  await expect(metadata.getByLabel('Publication policy', { exact: true })).toHaveValue('CC-BY-4.0');
  const editSave = page.waitForResponse(r => r.url().endsWith('/metadata/conditional') && r.request().method() === 'PATCH');
  await page.getByRole('button', { name: 'Save settings & Continue', exact: true }).click();
  const publish = page.getByRole('dialog', { name: 'Publish Trip?', exact: true });
  await expect(publish).toContainText('CC-BY-4.0');
  await publish.getByRole('button', { name: 'Publish', exact: true }).click();
  expect((await editSave).ok()).toBeTruthy();
  await page.getByRole('link', { name: 'Return to saved comparison', exact: true }).click();
  await expect(page.locator('.composition-header')).toContainText('Edited current C');
  await expect(page.locator('.composition-header')).toContainText('Public · saved changes update the same Trip');
  await record('5 C published through ordinary confirmation while comparison remains active');
  await expect(inspector.getByRole('heading', { name: 'Coastal ideas B', exact: true })).toBeVisible();
  await expect(notes).toHaveText('Retained Trip Notes review draft');
  const savedCurrentNotes = inspector.locator('.composition-differences > div').filter({ has: page.getByText(/^Current C ·/) }).filter({ has: page.locator('.composition-note') });
  await expect(savedCurrentNotes).toContainText('Actually saved current C Trip Notes');
  const reviewedNotes = inspector.locator('.composition-differences > div').filter({ has: page.getByText(/^Reviewed original C ·/) }).filter({ has: page.locator('.composition-note') });
  await expect(reviewedNotes).not.toContainText('Actually saved current C Trip Notes');
  await expect(inspector).toContainText('Current C changed since this review.');
  await page.screenshot({ path: `${evidence}/06-current-c-versus-reviewed-notes.png`, fullPage: true });
  await bTree.getByRole('button', { name: /Place · Coastal viewpoint/ }).click();
  await expect(notes).toHaveText('Independent item Notes draft');
  await bTree.getByRole('button', { name: /Coastal alternative/ }).click();
  await expect(notes).toHaveText('Retained route Notes draft through ordinary editing');
  await record('5 Ordinary C save and return preserve selection, current C and independent Notes drafts');
  await expectRouteInsideMap(page);
  await page.goto('/User/Trip');
  const continuation = page.waitForResponse(r => r.url().endsWith(`/api/trip-compositions/${composition}`) && r.request().method() === 'GET');
  await page.locator(`#unfinished-compositions a[href="/User/Trip/Compose/${composition}"]`).click();
  await expect(page).toHaveURL(new RegExp(composition));
  const continued = await continuation; expect(continued.ok(), await continued.text()).toBeTruthy();
  expect((await continued.json()).tripId).toBe(c);
  await expect(page.locator('.composition-header')).toContainText('Edited current C');
  const resumeRead = page.waitForResponse(r => r.url().endsWith(`/api/trip-compositions/${composition}`) && r.request().method() === 'GET');
  await page.reload();
  const resumeResponse = await resumeRead; expect(resumeResponse.ok(), await resumeResponse.text()).toBeTruthy();
  expect((await resumeResponse.json()).tripId).toBe(c);
  await expect(page.locator('.composition-header')).toContainText('Edited current C');
  await expect(notes).toHaveText('Retained route Notes draft through ordinary editing');
  await record('6 My Trips and reload resume the same C with retained selected Notes draft');
  const finishButton = page.getByRole('button', { name: 'Finish comparison', exact: true });
  await finishButton.focus(); await page.keyboard.press('Enter');
  const focusDialog = page.getByRole('dialog', { name: 'Finish comparison?' });
  await expect(focusDialog.getByRole('button', { name: 'Continue comparison', exact: true })).toBeFocused();
  await page.keyboard.press('Shift+Tab'); await expect(focusDialog.getByRole('button', { name: 'Finish comparison', exact: true })).toBeFocused();
  await page.keyboard.press('Tab'); await expect(focusDialog.getByRole('button', { name: 'Continue comparison', exact: true })).toBeFocused();
  await page.screenshot({ path: `${evidence}/06-finish-dialog.png`, fullPage: true });
  await page.keyboard.press('Escape'); await expect(finishButton).toBeFocused();
  await record('7 Finish dialog keyboard initial focus, trap, Escape and focus return');
  const completion = page.waitForResponse(r => r.url().endsWith(`/trip-compositions/${composition}/finish`));
  await page.getByRole('button', { name: 'Finish comparison', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Finish comparison?' }); await expect(dialog).toContainText('historical comparison cannot reopen');
  await dialog.getByRole('button', { name: 'Finish comparison', exact: true }).click(); expect((await completion).ok()).toBeTruthy();
  await expect(page.getByRole('heading', { name: 'Comparison finished', exact: true })).toBeVisible();
  const finalC = await (await page.request.get(`/api/trips/${c}/editor`)).json();
  expect(finalC.metadata.name).toBe('Edited current C'); expect(finalC.metadata.isPublic).toBe(true); expect(Object.values(finalC.segmentsById).some((s: any) => s.title === 'Coastal alternative')).toBeTruthy();
  expect((await page.request.get(`/api/trip-compositions/${composition}`)).status()).toBe(409);
  expect(Object.values(finalC.placesById).some((p: any) => p.notesHtml?.includes('Applied block Notes draft'))).toBeTruthy();
  await record('7 Finish confirmed, C/content retained and comparison closed', { c, composition });
  await page.screenshot({ path: `${evidence}/07-finished.png`, fullPage: true });
  await page.getByRole('link', { name: 'Continue ordinary editing of C', exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/User/Trip/Edit/${c}$`));
  await expect(page.locator('.trip-editor-workspace')).toBeVisible();
  await record('7 Actual ordinary C edit destination reached');
  // The managed account cleanup owns all descendants. No human/shared Trip is mutated.
  expect(route.id).toBeTruthy();
});

/** Preserve the reached page after the sole attempt; credential-bearing login pages are excluded. */
test.afterEach(async ({ page }, testInfo) => {
  const evidence = process.env.WAYFARER_COMPOSITION_EVIDENCE;
  if (!evidence) return;
  await writeFile(`${evidence}/attempt-result.json`, JSON.stringify({ status: testInfo.status, expectedStatus: testInfo.expectedStatus, title: testInfo.title, retry: testInfo.retry, duration: testInfo.duration, errors: testInfo.errors }, null, 2));
  if (testInfo.status === testInfo.expectedStatus) return;
  await writeFile(`${evidence}/failure.json`, JSON.stringify({ status: testInfo.status, url: page.url(), errors: testInfo.errors }, null, 2));
  if (await page.locator('.composition-workspace').count()) {
    await page.screenshot({ path: `${evidence}/failure-workspace.png`, fullPage: true });
    await writeFile(`${evidence}/failure-workspace.txt`, await page.locator('.composition-workspace').innerText());
    await writeFile(`${evidence}/failure-browser-state.json`, JSON.stringify(await page.evaluate(() => ({ title: document.title, url: location.href, viewport: { width: innerWidth, height: innerHeight, dpr: devicePixelRatio }, focus: document.activeElement?.outerHTML, notes: Array.from(document.querySelectorAll('.tiptap')).map(el => ({ text: el.textContent, html: el.innerHTML })) })), null, 2));
  }
});

/** Sample the actual SVG outline against viewport, map and visible overlay rectangles, including stroke width. */
async function observeGeometry(page: Page, evidence: string, name: string, selector = 'path[stroke-width="7"]'): Promise<void> {
  await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));
  const result = await page.evaluate(({ selector }) => {
    const map = document.querySelector('.composition-map')!.getBoundingClientRect();
    const overlays = Array.from(document.querySelectorAll('.composition-layers, .leaflet-control, .composition-map-status, .composition-drawer, .composition-header'))
      .map(el => ({ name: el.className, rect: el.getBoundingClientRect() })).filter(x => x.rect.width && x.rect.height);
    const paths = Array.from(document.querySelectorAll<SVGPathElement>(`.composition-map ${selector}`));
    const failures: unknown[] = [];
    let samples = 0;
    for (const path of paths) {
      const matrix = path.getScreenCTM(); if (!matrix) continue;
      const length = path.getTotalLength(); const stroke = Number(path.getAttribute('stroke-width')) / 2;
      for (let i = 0; i <= 160; i++) {
        const point = path.getPointAtLength(length * i / 160).matrixTransform(matrix); samples++;
        if (point.x - stroke < Math.max(map.left, 0) || point.x + stroke > Math.min(map.right, innerWidth)
          || point.y - stroke < Math.max(map.top, 0) || point.y + stroke > Math.min(map.bottom, innerHeight)) failures.push({ outside: true, x: point.x, y: point.y });
        for (const o of overlays) if (point.x + stroke > o.rect.left && point.x - stroke < o.rect.right && point.y + stroke > o.rect.top && point.y - stroke < o.rect.bottom)
          failures.push({ overlay: o.name, x: point.x, y: point.y });
      }
    }
    return { map: map.toJSON(), viewport: { width: innerWidth, height: innerHeight }, overlays: overlays.map(x => ({ name: x.name, rect: x.rect.toJSON() })), paths: paths.length, samples, failures };
  }, { selector });
  await writeFile(`${evidence}/${name}-geometry.json`, JSON.stringify(result, null, 2));
  await page.screenshot({ path: `${evidence}/${name}-geometry.png`, fullPage: true });
  expect(result.paths, `${name} selected geometry exists`).toBeGreaterThan(0);
  expect(result.failures, `${name} complete selected outline fits without overlay occlusion`).toEqual([]);
}

/** Pixel bounds of the selected route must sit inside the actual rendered map rather than behind a drawer. */
async function expectRouteInsideMap(page: Page): Promise<void> {
  const map = page.getByRole('region', { name: 'Shared A B C Trip map' });
  const selected = map.locator('path[stroke-width="7"]').first(); await expect(selected).toBeVisible();
  await expect.poll(async () => {
    const outer = await map.boundingBox(); const route = await selected.boundingBox();
    return Boolean(outer && route && route.x >= outer.x && route.y >= outer.y && route.x + route.width <= outer.x + outer.width && route.y + route.height <= outer.y + outer.height);
  }).toBeTruthy();
}
