import { expect, test, type Locator, type Page } from '@playwright/test';
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

/** Assert the rendered shared contract and record footer/control geometry without changing provider settings. */
async function observeMap(page: Page, map: Locator) {
  const footer = map.locator('.leaflet-control-attribution');
  await expect(footer).toBeVisible();
  await expect(footer).toHaveAttribute('aria-label', 'Map attribution');
  await expect(footer).toHaveAttribute('title', 'Map attribution');
  for (const [name, href] of [['Wayfarer', 'https://wayfarer.stefk.me'], ['Stef K', 'https://stefk.me'], ['Leaflet', 'https://leafletjs.com/']]) {
    const link = footer.getByRole('link', { name, exact: true });
    await expect(link).toHaveAttribute('href', href); await expect(link).toHaveAttribute('target', '_blank');
    await expect(link).toHaveAttribute('rel', /noopener/);
  }
  const observation = await map.evaluate(element => {
    const footer = element.querySelector('.leaflet-control-attribution')!;
    const expected = document.createElement('div'); expected.innerHTML = (window as any).wayfarerTileConfig?.attribution ?? '';
    const rect = (node: Element) => { const r = node.getBoundingClientRect(); return { left: r.left, top: r.top, right: r.right, bottom: r.bottom }; };
    const box = rect(element), credit = rect(footer);
    const controls = [...element.querySelectorAll('.leaflet-control-zoom'), ...element.parentElement!.querySelectorAll('.composition-layers, .composition-map-status')].map(rect);
    const selected = [...element.querySelectorAll('path[stroke-width="7"], circle[r="11"]')].map(rect);
    const overlaps = (a: typeof box, b: typeof box) => a.left < b.right && a.right > b.left && a.top < b.bottom && a.bottom > b.top;
    return { text: footer.textContent, html: footer.innerHTML, expected: expected.textContent, box, credit, controls, selected,
      providerLinks: [...expected.querySelectorAll('a')].map(link => ({ href: link.href, present: [...footer.querySelectorAll('a')].some(actual => actual.href === link.href) })),
      inside: credit.left >= box.left && credit.right <= box.right + 1 && credit.top >= box.top && credit.bottom <= box.bottom + 1,
      unobscured: controls.every(control => !overlaps(control, credit)) && selected.every(path => !overlaps(path, credit)) };
  });
  expect(observation.text).toContain(observation.expected);
  expect(observation.providerLinks.every(link => link.present)).toBeTruthy();
  expect(observation.inside).toBeTruthy(); expect(observation.unobscured).toBeTruthy();
  return observation;
}

/** Focused assembled-workspace correction proof; exhaustive draft/race permutations remain component tests. */
test('Final 911 map conformance and G1 saved effect with route focus', async ({ page }, testInfo) => {
  test.setTimeout(180_000); page.setDefaultTimeout(15_000);
  const credentials = loadSharedLayoutConfig(); const a = process.env.WAYFARER_E2E_TRIP_ID!;
  const evidence = process.env.WAYFARER_COMPOSITION_EVIDENCE ?? testInfo.outputPath('captures');
  await mkdir(evidence, { recursive: true });
  const observations: Record<string, unknown> = {};
  const capture = (name: string) => page.screenshot({ path: `${evidence}/${name}.png` });
  await page.setViewportSize({ width: 1536, height: 1100 });
  await page.goto('/Identity/Account/Login');
  await page.getByLabel('Username').fill(credentials.username); await page.getByLabel('Password').fill(credentials.password);
  await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')), page.getByRole('button', { name: 'Log in' }).click()]);
  await page.goto(`/User/Trip/Edit/${a}`); await expect(page.locator('.trip-editor-workspace')).toBeVisible();
  observations.editorMap = await observeMap(page, page.locator('.trip-editor-map'));
  await capture('M1-ordinary-editor-shared-attribution');
  const token = await page.locator('#trip-editor-antiforgery input').inputValue();
  const post = async (url: string, data: unknown) => {
    const response = await page.request.post(url, { headers: { RequestVerificationToken: token }, data });
    expect(response.ok(), await response.text()).toBeTruthy(); return response.json();
  };
  const read = async (id: string) => (await page.request.get(`/api/trips/${id}/editor`)).json();
  const b = (await post('/api/trip-compositions/versions', { sourceA: a, name: 'Focused correction ideas B' })).tripId;
  // Ordinary persisted graphs retain the original 5-minute/35-minute specimen without a special host/fixture.
  for (const [id, title, minutes, name] of [[a, 'Own harbour route', 5, 'Own quay'], [b, 'B harbour route', 35, 'B quay']] as const) {
    const state = await read(id); const region = state.regionOrder[0];
    const anchor = (await post(`/api/trips/${id}/editor/regions/${region}/places/conditional`, {
      expected: { regionId: region, places: state.placeOrderByRegionId[region].map((placeId: string) => ({ id: placeId, displayOrder: state.placesById[placeId].originalValues.displayOrder })) },
      value: { name, notesHtml: '', location: { latitude: 37.99, longitude: 23.74 }, iconName: 'marker', markerColor: 'bg-blue', address: '', reverseGeocode: false }
    })).data;
    await post(`/api/trips/${id}/editor/segments`, { title, fromPlaceId: state.placeOrderByRegionId[region][0], toPlaceId: anchor.id,
      waypointPlaceIds: [], waypointRouteVertexIndices: [], mode: 'walk', estimatedDistanceKm: null, estimatedDurationMinutes: minutes,
      estimatedDurationSource: 'Manual', notesHtml: '', aggregateConcurrencyToken: null, route: null });
  }
  const started = await post('/api/trip-compositions', { sourceA: a, sourceB: b, name: 'Focused correction C' });
  const composition = started.compositionId; const c = started.tripId;
  // The normal preselected-source/destination entry loads its preview on mount.
  await page.goto(`/User/Trip/Copy?source=${b}&destination=${a}`);
  const copyMap = page.getByRole('region', { name: 'Source and existing destination Trip map' });
  await expect(copyMap.locator('.leaflet-overlay-pane path').first()).toBeVisible();
  observations.copyMap = await observeMap(page, copyMap); await capture('M2-lightweight-copy-attribution');
  await page.goto(`/User/Trip/Compose/${composition}`);
  const inspector = page.getByRole('region', { name: 'Contextual comparison inspector' });
  const tree = page.getByRole('region', { name: 'Source B hierarchy' });
  const workspace = await (await page.request.get(`/api/trip-compositions/${composition}`)).json();
  const region = workspace.current.regionOrder[0];
  const sourcePlace = workspace.sourceB.graph.regions[0].places.find((p: any) => p.name === 'Browser place').id;
  try {
    await tree.getByText('↝ Trip-level Segments', { exact: true }).click();
    const routeRow = tree.getByRole('button', { name: /B harbour route/ });
    await routeRow.focus(); await page.keyboard.press('Enter');
    await expect(inspector.getByRole('heading', { name: 'B harbour route', exact: true })).toBeVisible();
    observations.compositionMap = await observeMap(page, page.locator('.composition-map'));
    await capture('M3-full-composition-desktop-attribution');
    await expect(routeRow).toBeFocused(); // Desktop source remains visible and owns its focus.
    await inspector.getByLabel('Action for C', { exact: true }).selectOption('reuse');
    await inspector.getByLabel('Current C counterpart', { exact: true }).selectOption(workspace.current.graph.segments[0].id);
    const routeField = inspector.getByRole('group', { name: 'Complete route', exact: true });
    await page.getByRole('separator', { name: 'Resize comparison drawer' }).focus(); await page.keyboard.press('End');
    const differences = routeField.locator('.composition-differences > div');
    await expect(differences.nth(0)).toContainText('35 min · Manual');
    await expect(differences.nth(1)).toContainText('5 min · Manual'); await expect(differences.nth(2)).toContainText('5 min · Manual');
    await expect(differences.nth(1)).toContainText('Different saved route facts: duration');
    await routeField.scrollIntoViewIfNeeded(); await capture('F1-saved-35-versus-5-minutes');
    observations.route = { rendered: await routeField.innerText(), source: workspace.sourceB.graph.segments[0], current: workspace.current.graph.segments[0] };

    await page.setViewportSize({ width: 430, height: 932 });
    await page.getByRole('button', { name: 'Source B', exact: true }).click();
    // Hold unrelated candidates until after focus assertions to establish that focus is independent of their reply.
    let releaseCandidates!: () => void;
    const candidatesHeld = new Promise<void>(resolve => { releaseCandidates = resolve; });
    const candidateRoute = `**/api/trip-compositions/${composition}/candidates?**`;
    await page.route(candidateRoute, async route => { await candidatesHeld; await route.continue(); });
    try {
      await routeRow.focus(); await page.keyboard.press('Enter');
      const heading = inspector.getByRole('heading', { name: 'B harbour route', exact: true });
      await expect(heading).toBeFocused(); await expect(heading).toBeInViewport();
      await expect(inspector.locator('#composition-selection-context')).toHaveText('Source B · segment · Read-only captured idea');
      expect(await inspector.evaluate(element => element.scrollTop)).toBe(0);
      observations.focus = await heading.evaluate(element => ({ tag: document.activeElement?.tagName, focused: element === document.activeElement,
        outline: getComputedStyle(element).outlineStyle, context: element.nextElementSibling?.textContent }));
      observations.narrowMap = await observeMap(page, page.locator('.composition-map'));
      await capture('M4-narrow-footer-controls-and-selected-context');
    } finally { releaseCandidates(); await page.unrouteAll({ behavior: 'wait' }); }
    await page.getByRole('button', { name: 'Source B', exact: true }).click();
    await expect(routeRow).toBeVisible(); // Source-return path and expansion survive.
    await routeRow.focus(); await page.keyboard.press('Space');
    await expect(inspector.getByRole('heading', { name: 'B harbour route', exact: true })).toBeFocused();
    await expect(inspector.getByLabel('Current C counterpart', { exact: true })).toHaveValue(workspace.current.graph.segments[0].id);

    await page.setViewportSize({ width: 1536, height: 1100 });
    await tree.getByText('Browser region', { exact: true }).click();
    const itemRow = tree.getByRole('button', { name: /Place · Browser place/ });
    await itemRow.click();
    await inspector.getByLabel('Destination Region', { exact: true }).selectOption(region);
    await inspector.getByLabel('Use a custom name', { exact: true }).check();
    await inspector.getByLabel('Custom name', { exact: true }).fill('Reviewed lookout');
    await inspector.getByLabel('Keep separate even if the same name or another copy exists', { exact: true }).check();
    await inspector.getByLabel('Notes action', { exact: true }).selectOption('edit');
    const notes = inspector.locator('.tiptap[contenteditable="true"]');
    await notes.fill('Independent selected-item Notes');
    await inspector.getByRole('button', { name: 'Add this reviewed item to block', exact: true }).click();
    await inspector.getByRole('button', { name: 'Review block · 1 items', exact: true }).click();
    await expect(notes).toHaveText('Independent selected-item Notes'); await notes.fill('Authored block Notes');
    await inspector.getByRole('button', { name: 'Return to selected item', exact: true }).click();
    await expect(notes).toHaveText('Independent selected-item Notes');
    await tree.getByRole('button', { name: /Region · Browser region/ }).click();
    await inspector.getByLabel('Action for C', { exact: true }).selectOption('reuse');
    await inspector.getByLabel('Current C counterpart', { exact: true }).selectOption(region);
    await inspector.getByLabel('Region and direct Places/Areas (otherwise Region only)', { exact: true }).check();
    const child = inspector.getByRole('group', { name: 'place · Browser place', exact: true });
    const regionNotesField = inspector.getByRole('group', { name: 'Notes', exact: true }).last();
    await regionNotesField.getByLabel('Notes action', { exact: true }).selectOption('edit');
    await regionNotesField.locator('.tiptap').fill('Independent Region Notes');
    await inspector.getByRole('button', { name: 'Add this reviewed item to block', exact: true }).click();
    await inspector.getByRole('button', { name: 'Review block · 1 items', exact: true }).click();
    await expect(child.locator('.tiptap')).toHaveText('Authored block Notes');
    await expect(child.getByLabel('Custom name', { exact: true })).toHaveValue('Reviewed lookout');
    await child.scrollIntoViewIfNeeded(); await capture('G1-standalone-child-folded-into-region-block');
    // The independent item conflicts with the authored folded block child; neither draft may win silently.
    await itemRow.click(); await expect(notes).toHaveText('Independent selected-item Notes');
    await inspector.getByRole('button', { name: 'Add this reviewed item to block', exact: true }).click();
    await expect(page.locator('.composition-feedback [role="alert"]')).toContainText('Conflicting choices for Browser place');
    await expect(notes).toHaveText('Independent selected-item Notes');
    await capture('G1-conflicting-choices-retained');
    // Remove the owning block Region, then deliberately collect the retained item and Region again.
    await inspector.getByRole('button', { name: 'Review block · 1 items', exact: true }).click();
    await expect(child.locator('.tiptap')).toHaveText('Authored block Notes');
    await inspector.getByRole('button', { name: 'Remove from local block', exact: true }).click();
    await itemRow.click(); await expect(notes).toHaveText('Independent selected-item Notes');
    await inspector.getByRole('button', { name: 'Add this reviewed item to block', exact: true }).click();
    await tree.getByRole('button', { name: /Region · Browser region/ }).click();
    await expect(regionNotesField.locator('.tiptap')).toHaveText('Independent Region Notes');
    await inspector.getByRole('button', { name: 'Add this reviewed item to block', exact: true }).click();
    await inspector.getByRole('button', { name: 'Review block · 1 items', exact: true }).click();
    await child.locator('.tiptap').fill('Applied block child Notes');
    await expect(child.locator('.composition-result')).toContainText('Browser region · Append');
    await capture('G1-region-block-final-reviewed-effect');
    const submitted = page.waitForRequest(r => r.url().endsWith(`/trip-compositions/${composition}/actions`) && r.method() === 'POST');
    const reply = page.waitForResponse(r => r.url().endsWith(`/trip-compositions/${composition}/actions`) && r.request().method() === 'POST');
    await inspector.getByRole('button', { name: 'Apply reviewed action to C', exact: true }).click();
    const response = await reply; expect(response.ok(), await response.text()).toBeTruthy();
    const body = (await submitted).postDataJSON();
    const identities = body.items.flatMap((item: any) => [item, ...(item.children ?? [])]);
    expect(identities.filter((item: any) => item.sourceId === sourcePlace)).toHaveLength(1);
    await expect(page.locator('.composition-feedback')).toContainText('Saved to C.');
    await expect(regionNotesField.locator('.tiptap')).toHaveText('Independent Region Notes');
    const saved = await read(c); const copied = Object.values(saved.placesById).filter((p: any) => p.name === 'Reviewed lookout') as any[];
    expect(copied).toHaveLength(1); expect(copied[0].notesHtml).toContain('Applied block child Notes');
    observations.block = { submitted: body, status: response.status(), result: await response.json(), copied: copied[0], independentRegion: 'Independent Region Notes' };
    await itemRow.click(); await expect(notes).toHaveText('Independent selected-item Notes');
    await expect(inspector.getByLabel('Custom name', { exact: true })).toHaveValue('Reviewed lookout');
    // The independent item intentionally retains its old originals; explicitly review C before selecting a newly saved identity.
    await inspector.getByRole('button', { name: 'Review current C / refresh originals', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Review current originals', exact: true }).click();
    await expect(inspector.getByLabel('Custom name', { exact: true })).toHaveValue('Reviewed lookout');
    await expect(notes).toHaveText('Independent selected-item Notes');
    await inspector.getByLabel('Action for C', { exact: true }).selectOption('reuse');
    await expect(inspector.getByLabel('Current C counterpart', { exact: true }).locator(`option[value="${copied[0].id}"]`)).toHaveCount(1);
    await inspector.getByLabel('Current C counterpart', { exact: true }).selectOption(copied[0].id);
    const notesField = inspector.getByRole('group', { name: 'Notes', exact: true });
    await expect(notesField.locator('.composition-differences > div').nth(2)).toContainText('Applied block child Notes');
    await expect(notes).toHaveText('Independent selected-item Notes');
    await notesField.scrollIntoViewIfNeeded(); await capture('G1-saved-effect-and-independent-item-draft');
    const displayed = await notesField.innerText(); const independentItem = await notes.innerText();
    await tree.getByRole('button', { name: /Region · Browser region/ }).click();
    await expect(regionNotesField.locator('.tiptap')).toHaveText('Independent Region Notes');
    await capture('G1-retained-independent-region-draft');
    observations.block = { ...observations.block as object, explicitReview: true, counterpartAvailable: copied[0].id,
      displayed, independentItem, independentRegion: 'Independent Region Notes' };
  } finally { await writeFile(`${evidence}/correction-observations.json`, JSON.stringify(observations, null, 2)); }
});
