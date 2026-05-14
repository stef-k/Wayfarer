import { expect, test } from '@playwright/test';
import {
  absoluteUrl,
  closeDraftWithDiscard,
  collectForbiddenSidebarSearchRequests,
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

    await placeRegion.getByText(fixture.place.name).locator('xpath=ancestor::li[contains(@class, "trip-editor-place-row")]').getByRole('button', { name: 'Edit', exact: true }).click();
    await expect(page.getByRole('heading', { name: new RegExp(`Edit Place - ${escapeRegex(fixture.place.name)}`) })).toBeVisible();
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
    await expect(shadowCard.getByText(fixture!.childName, { exact: true })).toBeVisible();
    await expect(shadowCard.getByRole('button', { name: 'Add Place' })).toHaveCount(0);
    await expect(shadowCard.getByRole('button', { name: /add area/i })).toHaveCount(0);
    await expect(regionEditButton(shadowCard)).toHaveCount(0);
    await expect(shadowCard.getByRole('button', { name: /drag to reorder/i })).toHaveCount(0);
  });
});
