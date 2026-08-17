import { expect, test } from '@playwright/test';
import {
  absoluteUrl,
  activeEditorCancelButton,
  closeDraftWithDiscard,
  collectForbiddenSidebarSearchRequests,
  dragFromVisibleHandle,
  escapeRegex,
  expectMountedWorkspace,
  expectNoSearchAddUi,
  loadEditorStateFixture,
  regionCard,
  regionEditButton,
  shadowChildFixture,
  sidebarSearchFixture,
  signIn,
  uniqueName,
  editorPath
} from './tripEditorTestUtils';

test.describe.serial('Trip Editor sidebar search verification', () => {
  test('filters regions and places without network search or draft loss', async ({ page }) => {
    await signIn(page);
    const state = await loadEditorStateFixture(page);
    const fixture = sidebarSearchFixture(state);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    const fixtureRegion = Object.values(state.regionsById).find(region => region.name === fixture.region.name)!;
    const placeRegionEntity = Object.values(state.regionsById).find(region => region.name === fixture.place.regionName)!;
    const fixturePlace = Object.values(state.placesById).find(place => place.name === fixture.place.name)!;
    const regionOrdinal = state.regionOrder.filter(id => !state.regionsById[id]?.isShadow).indexOf(fixtureRegion.id) + 1;
    const placeOrdinal = state.placeOrderByRegionId[placeRegionEntity.id].indexOf(fixturePlace.id) + 1;
    await expect(regionCard(page, fixture.region.name).getByRole('heading')).toHaveText(`${regionOrdinal}-${fixture.region.name}`);
    await expect(regionCard(page, fixture.place.regionName).getByText(`${placeOrdinal}-${fixture.place.name}`, { exact: true })).toBeVisible();

    const requests = collectForbiddenSidebarSearchRequests(page);
    const search = page.getByLabel('Sidebar search');
    await expect(search).toBeVisible();

    const tagsPanel = page.getByRole('heading', { name: 'Tags' }).locator('xpath=ancestor::section[contains(@class, "trip-editor-panel")]');
    const tagsText = (await tagsPanel.count()) > 0 && await tagsPanel.isVisible() ? await tagsPanel.innerText() : null;

    await search.fill(fixture.region.name);
    await expect(regionCard(page, fixture.region.name)).toBeVisible();
    await expect(tagsPanel).toHaveCount(tagsText ? 1 : 0);
    if (tagsText) {
      expect(await tagsPanel.innerText()).toBe(tagsText);
    }

    const placeRegion = regionCard(page, fixture.place.regionName);
    await search.fill(fixture.place.name);
    await expect(placeRegion).toBeVisible();
    await expect(placeRegion).toContainText(fixture.place.name);
    await expect(placeRegion.getByText(`${placeOrdinal}-${fixture.place.name}`, { exact: true })).toBeVisible();
    await expectNoSearchAddUi(page);
    expect(requests(), 'Sidebar search should not call Nominatim, geosearch, search-add, or search endpoints.').toEqual([]);

    const children = placeRegion.locator('ul');
    await search.fill('');
    await expect(regionCard(page, fixture.region.name)).toBeVisible();
    await expect(children).toBeVisible();

    await regionEditButton(regionCard(page, fixture.region.name)).click();
    await expect(page.getByRole('heading', { name: new RegExp(`Edit Region - ${escapeRegex(fixture.region.name)}`) })).toBeVisible();
    await search.fill(uniqueName('no matching region draft query'));
    await expect(regionCard(page, fixture.region.name)).toBeVisible();
    await expect(page.getByRole('heading', { name: new RegExp(`Edit Region - ${escapeRegex(fixture.region.name)}`) })).toBeVisible();
    await closeDraftWithDiscard(page);
    await search.fill('');

    await placeRegion.locator('.trip-editor-place-row').filter({ hasText: fixture.place.name }).getByRole('button', { name: 'Edit', exact: true }).click();
    await expect(page.getByRole('heading', { name: new RegExp(`Edit Place - ${escapeRegex(fixture.place.name)}`) })).toBeVisible();
    await expect(page.locator('#trip-editor-place-form').getByLabel('Name')).toHaveValue(fixture.place.name);
    await search.fill(uniqueName('no matching place draft query'));
    await expect(placeRegion).toBeVisible();
    await expect(placeRegion).toContainText(fixture.place.name);
    await expect(page.getByRole('heading', { name: new RegExp(`Edit Place - ${escapeRegex(fixture.place.name)}`) })).toBeVisible();
    await closeDraftWithDiscard(page);
  });

  test('restores collapsed hierarchy after clear', async ({ page }) => {
    await signIn(page);
    const state = await loadEditorStateFixture(page);
    const fixture = sidebarSearchFixture(state);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    const card = regionCard(page, fixture.place.regionName);
    const children = card.locator('ul');
    const toggle = card.getByRole('button', { name: 'Collapse' });
    await expect(children).toBeVisible();
    await toggle.click();
    await expect(children).toBeHidden();

    await page.getByLabel('Sidebar search').fill(fixture.place.name);
    await expect(children).toBeVisible();
    await expect(card.getByRole('button', { name: 'Collapse' })).toBeDisabled();
    await page.getByLabel('Sidebar search').fill(`${fixture.place.name} unmatched suffix`);
    await expect(children).toBeHidden();
    await page.getByLabel('Sidebar search').fill(fixture.place.name);
    await expect(children).toBeVisible();

    await page.getByLabel('Sidebar search').fill('');
    await expect(children).toBeHidden();
    await expect(card.getByRole('button', { name: 'Expand' })).toBeEnabled();
  });

  test('disables Collapse while an editor forces its Region open without changing ordinary collapse state', async ({ page }) => {
    test.setTimeout(60_000);
    await signIn(page);
    const state = await loadEditorStateFixture(page);
    const fixture = sidebarSearchFixture(state);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    const card = regionCard(page, fixture.region.name);
    const children = card.locator('ul');
    await card.getByRole('button', { name: 'Collapse' }).click();
    await expect(children).toBeHidden();

    await regionEditButton(card).click();
    const forcedCollapse = card.getByRole('button', { name: 'Collapse' });
    await expect(children).toBeVisible();
    await expect(forcedCollapse).toHaveAttribute('aria-expanded', 'true');
    await expect(forcedCollapse).toBeDisabled();
    const editorExplanationId = await forcedCollapse.getAttribute('aria-describedby');
    await expect(page.locator(`#${editorExplanationId}`)).toHaveText('Collapse is unavailable while a Region, Place, or Area editor in this Region is open. Close the editor first.');

    const forcedBox = await forcedCollapse.boundingBox();
    expect(forcedBox).not.toBeNull();
    await page.mouse.click(forcedBox!.x + forcedBox!.width / 2, forcedBox!.y + forcedBox!.height / 2);
    await forcedCollapse.press('Enter');
    await forcedCollapse.press('Space');
    await expect(children).toBeVisible();

    const form = page.locator('#trip-editor-region-form');
    await form.getByLabel('Name').fill(`${fixture.region.name} unsaved`);
    await activeEditorCancelButton(page).click();
    const discard = page.getByRole('dialog', { name: 'Discard changes?' });
    await discard.getByRole('button', { name: 'Keep editing' }).click();
    await expect(form).toBeVisible();
    await expect(forcedCollapse).toBeDisabled();

    await activeEditorCancelButton(page).click();
    await page.getByRole('dialog', { name: 'Discard changes?' }).getByRole('button', { name: 'Discard' }).click();
    await expect(children).toBeHidden();
    await expect(card.getByRole('button', { name: 'Expand' })).toBeEnabled();

    const placeRegion = regionCard(page, fixture.place.regionName);
    const placeChildren = placeRegion.locator('ul');
    const placeToggle = placeRegion.getByRole('button', { name: /Collapse|Expand/ });
    if (await placeToggle.getAttribute('aria-expanded') === 'false') {
      await placeToggle.click();
    }
    await placeRegion.locator('.trip-editor-place-row').filter({ hasText: fixture.place.name }).click();
    const selectedCollapse = placeRegion.getByRole('button', { name: 'Collapse' });
    await expect(selectedCollapse).toBeDisabled();
    const selectionExplanationId = await selectedCollapse.getAttribute('aria-describedby');
    await expect(page.locator(`#${selectionExplanationId}`)).toHaveText('Collapse is unavailable while a Place in this Region is selected. Clear the selected Place first.');
    await page.getByRole('button', { name: 'Clear Selection' }).click();
    await expect(selectedCollapse).toBeEnabled();

    await selectedCollapse.click();
    await expect(placeChildren).toBeHidden();
    const search = page.getByLabel('Sidebar search');
    await search.fill(fixture.place.name);
    const searchCollapse = placeRegion.getByRole('button', { name: 'Collapse' });
    await expect(searchCollapse).toBeDisabled();
    const searchExplanationId = await searchCollapse.getAttribute('aria-describedby');
    await expect(page.locator(`#${searchExplanationId}`)).toHaveText('Collapse is unavailable while sidebar search is active. Clear the search to restore your previous Region layout.');
    await search.fill(`${fixture.place.name} unmatched suffix`);
    await expect(placeRegion).toBeHidden();
    await search.fill(fixture.place.name);
    await expect(placeChildren).toBeVisible();
    await search.fill('');
    await expect(placeChildren).toBeHidden();

    const normalRegions = state.regionOrder.map(id => state.regionsById[id]).filter(region => region && !region.isShadow);
    expect(normalRegions.length).toBeGreaterThan(1);
    const reorderTarget = normalRegions.find(region => region.id !== normalRegions[0].id)!;
    const collapsedReorderCard = regionCard(page, reorderTarget.name);
    const reorderChildren = collapsedReorderCard.locator('ul');
    const reorderToggle = collapsedReorderCard.getByRole('button', { name: /Collapse|Expand/ });
    if (await reorderToggle.getAttribute('aria-expanded') === 'true') {
      await reorderToggle.click();
    }
    await expect(reorderChildren).toBeHidden();
    await page.route(/\/editor\/regions\/order$/, route => route.fulfill({ status: 500, body: 'forced reorder recovery' }), { times: 1 });
    await dragFromVisibleHandle(collapsedReorderCard, regionCard(page, normalRegions[0].name), 'Drag to reorder region');
    await expect(page.locator('.trip-editor-form-error')).toBeVisible();
    await expect(reorderChildren).toBeHidden();

  });

  test('reattaches Region and nested Sortables after rejected dirty reorder recovery', async ({ page }) => {
    test.setTimeout(60_000);
    await signIn(page);
    const state = await loadEditorStateFixture(page);
    const normalRegions = state.regionOrder.map(id => state.regionsById[id]).filter(region => region && !region.isShadow);
    expect(normalRegions.length).toBeGreaterThan(1);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    const collapsedCard = regionCard(page, normalRegions[0].name);
    const editorCard = regionCard(page, normalRegions[1].name);
    const collapsedChildren = collapsedCard.locator('ul');
    await collapsedCard.getByRole('button', { name: 'Collapse' }).click();
    await expect(collapsedChildren).toBeHidden();

    await regionEditButton(editorCard).click();
    const editorForm = page.locator('#trip-editor-region-form');
    await editorForm.getByLabel('Name').fill(`${normalRegions[1].name} unsaved`);
    const forcedCollapse = editorCard.getByRole('button', { name: 'Collapse' });
    await expect(forcedCollapse).toBeDisabled();

    const authoritativeIds = normalRegions.map(region => region.id);
    const editorHeader = editorCard.locator('.trip-editor-region-card__header');
    await dragFromVisibleHandle(collapsedCard, editorHeader, 'Drag to reorder region');
    const firstDiscard = page.getByRole('dialog', { name: 'Discard changes?' });
    await firstDiscard.getByRole('button', { name: 'Keep editing' }).click();
    await expect(firstDiscard).toHaveCount(0);
    expect(await page.locator('.trip-editor-region-card--normal').evaluateAll(cards => cards.map(card => (card as HTMLElement).dataset.regionId))).toEqual(authoritativeIds);
    await expect(collapsedChildren).toBeHidden();
    await expect(editorForm).toBeVisible();
    await expect(forcedCollapse).toBeDisabled();

    // A second pointer drag must reach the production Sortable callback on the replacement DOM.
    await dragFromVisibleHandle(collapsedCard, editorHeader, 'Drag to reorder region');
    const secondDiscard = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(secondDiscard).toBeVisible();
    await secondDiscard.getByRole('button', { name: 'Keep editing' }).click();
    await expect(secondDiscard).toHaveCount(0);
    expect(await page.locator('.trip-editor-region-card--normal').evaluateAll(cards => cards.map(card => (card as HTMLElement).dataset.regionId))).toEqual(authoritativeIds);

    const nestedRegion = normalRegions.find(region => (state.placeOrderByRegionId[region.id]?.length ?? 0) > 1);
    if (nestedRegion) {
      const nestedCard = regionCard(page, nestedRegion.name);
      const nestedToggle = nestedCard.getByRole('button', { name: /Collapse|Expand/ });
      if (await nestedToggle.getAttribute('aria-expanded') === 'false') {
        await nestedToggle.click();
      }
      const placeIds = state.placeOrderByRegionId[nestedRegion.id];
      const firstPlace = nestedCard.locator(`[data-place-id="${placeIds[0]}"]`);
      const secondPlace = nestedCard.locator(`[data-place-id="${placeIds[1]}"]`);
      await dragFromVisibleHandle(secondPlace, firstPlace, 'Drag to reorder place');
      const nestedDiscard = page.getByRole('dialog', { name: 'Discard changes?' });
      await expect(nestedDiscard).toBeVisible();
      await nestedDiscard.getByRole('button', { name: 'Keep editing' }).click();
      await expect(nestedDiscard).toHaveCount(0);
      expect(await nestedCard.locator('.trip-editor-place-row').evaluateAll(rows => rows.map(row => (row as HTMLElement).dataset.placeId))).toEqual(placeIds);
    } else {
      test.info().annotations.push({ type: 'fixture limitation', description: 'Configured trip has no normal Region with two Places for nested Sortable interaction.' });
    }
  });

  test('filters areas when the configured trip has area fixture data', async ({ page }) => {
    await signIn(page);
    const state = await loadEditorStateFixture(page);
    const fixture = sidebarSearchFixture(state);
    test.skip(!fixture.area, 'Configured Trip Editor fixture has no loaded area rows to verify sidebar area search.');
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    await page.getByLabel('Sidebar search').fill(fixture.area!.name);
    const card = regionCard(page, fixture.area!.regionName);
    await expect(card).toBeVisible();
    await expect(card).toContainText(fixture.area!.name);
  });

  test('filters segments when the configured trip has segment fixture data', async ({ page }) => {
    await signIn(page);
    const state = await loadEditorStateFixture(page);
    const fixture = sidebarSearchFixture(state);
    test.skip(!fixture.segment, 'Configured Trip Editor fixture has no loaded segment rows to verify sidebar segment search.');
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    await page.getByLabel('Sidebar search').fill(fixture.segment!.query);
    await expect(page.getByRole('heading', { name: 'Segments' })).toBeVisible();
    await expect(page.locator('.trip-editor-segments')).toContainText(fixture.segment!.label);
  });

  test('shows shadow parents for matching shadow children without mutation controls', async ({ page }) => {
    await signIn(page);
    const state = await loadEditorStateFixture(page);
    const fixture = shadowChildFixture(state);
    test.skip(!fixture, 'Configured Trip Editor fixture has no loaded shadow-region child row to verify shadow search behavior.');
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    await page.getByLabel('Sidebar search').fill(fixture!.childName);
    const shadowCard = regionCard(page, fixture!.regionName);
    await expect(shadowCard).toBeVisible();
    await expect(shadowCard.getByRole('heading')).toHaveText('0-Unassigned Places');
    if (fixture!.childKind === 'place') {
      const shadow = Object.values(state.regionsById).find(region => region.name === fixture!.regionName)!;
      const placeId = Object.values(state.placesById).find(place => place.name === fixture!.childName)!.id;
      const ordinal = state.placeOrderByRegionId[shadow.id].indexOf(placeId) + 1;
      await expect(shadowCard.getByText(`${ordinal}-${fixture!.childName}`, { exact: true })).toBeVisible();
    } else {
      await expect(shadowCard.getByText(fixture!.childName, { exact: true })).toBeVisible();
    }
    await expect(shadowCard.getByRole('button', { name: 'Add Place' })).toHaveCount(0);
    await expect(shadowCard.getByRole('button', { name: /add area/i })).toHaveCount(0);
    await expect(regionEditButton(shadowCard)).toHaveCount(0);
    await expect(shadowCard.getByRole('button', { name: /drag to reorder/i })).toHaveCount(0);
  });
});
