import { expect, test, type Page, type Route } from '@playwright/test';
import {
  absoluteUrl,
  closeDraftWithDiscard,
  config,
  editorApiPath,
  expectMountedWorkspace,
  signIn,
  workspacePath
} from './tripEditorTestUtils';

const geocodePath = /\/api\/trips\/[^/]+\/editor\/geocode\/search/i;
const externalProvider = /nominatim\.openstreetmap\.org|photon\.komoot\.io|api\.mapbox\.com|maps\.googleapis\.com/i;

test.describe('Trip Editor map geocode search', () => {
  test('map search is separate from sidebar search and uses explicit proxy triggers', async ({ page }) => {
    await signIn(page);
    const externalCalls = collectExternalProviderCalls(page);
    let proxyCalls = 0;
    await routeGeocode(page, async route => {
      proxyCalls += 1;
      await fulfillGeocode(route, [result('Mock Acropolis')]);
    });

    await page.goto(absoluteUrl(workspacePath));
    await expectMountedWorkspace(page);

    await page.getByLabel('Sidebar search').fill('ath');
    await expect(page.getByRole('searchbox', { name: 'Map search' })).toBeVisible();
    expect(proxyCalls, 'Sidebar search and map-search typing should not call the geocode proxy.').toBe(0);

    const mapSearch = page.getByRole('region', { name: 'Map search' });
    await expect(mapSearch.getByRole('button', { name: 'Search' })).toBeDisabled();
    await page.getByRole('searchbox', { name: 'Map search' }).fill('ac');
    await expect(mapSearch.getByRole('button', { name: 'Search' })).toBeDisabled();
    await page.getByRole('searchbox', { name: 'Map search' }).fill('acropolis');
    expect(proxyCalls, 'Typing alone should not call the geocode proxy.').toBe(0);

    await mapSearch.getByRole('button', { name: 'Search' }).click();
    await expect(mapSearch.getByRole('button', { name: 'Mock Acropolis' })).toBeVisible();
    expect(proxyCalls).toBe(1);

    await page.getByRole('searchbox', { name: 'Map search' }).fill('parthenon');
    await page.getByRole('searchbox', { name: 'Map search' }).press('Enter');
    await expect(mapSearch.getByRole('button', { name: 'Mock Acropolis' })).toBeVisible();
    expect(proxyCalls).toBe(2);
    expect(externalCalls()).toEqual([]);
  });

  test('map search renders loading, no-results, rate-limit, unavailable, and stale-response states', async ({ page }) => {
    await signIn(page);
    let mode: 'slow' | 'empty' | 'rate' | 'unavailable' = 'slow';
    await routeGeocode(page, async route => {
      if (mode === 'slow') {
        await new Promise(resolve => setTimeout(resolve, 250));
        await fulfillGeocode(route, [result('Stale Result')], 'first query');
        return;
      }

      if (mode === 'empty') {
        await fulfillGeocode(route, []);
        return;
      }

      await route.fulfill({ status: mode === 'rate' ? 429 : 503, contentType: 'application/json', body: '{}' });
    });

    await page.goto(absoluteUrl(workspacePath));
    await expectMountedWorkspace(page);
    const mapSearch = page.getByRole('region', { name: 'Map search' });

    await page.getByRole('searchbox', { name: 'Map search' }).fill('first query');
    await mapSearch.getByRole('button', { name: 'Search' }).click();
    await expect(mapSearch).toContainText('Searching map...');
    await expect(mapSearch.getByRole('button', { name: 'Search' })).toBeDisabled();
    mode = 'empty';
    await page.getByRole('searchbox', { name: 'Map search' }).fill('second query');
    await expect(mapSearch.getByText('Stale Result')).toHaveCount(0);
    await expect(mapSearch.getByRole('button', { name: 'Search' })).toBeEnabled();
    await page.getByRole('searchbox', { name: 'Map search' }).press('Enter');
    await expect(mapSearch).toContainText('No map search results.');
    await expect(mapSearch.getByText('Stale Result')).toHaveCount(0);

    mode = 'rate';
    await page.getByRole('searchbox', { name: 'Map search' }).fill('rate limit');
    await page.getByRole('searchbox', { name: 'Map search' }).press('Enter');
    await expect(mapSearch).toContainText('Map search is rate limited.');

    mode = 'unavailable';
    await page.getByRole('searchbox', { name: 'Map search' }).fill('provider unavailable');
    await page.getByRole('searchbox', { name: 'Map search' }).press('Enter');
    await expect(mapSearch).toContainText('Map search provider is unavailable.');
  });

  test('in-flight map search blocks duplicate Search and Enter submits until completion', async ({ page }) => {
    await signIn(page);
    let proxyCalls = 0;
    let releaseFirstRequest: (() => void) | null = null;
    let resolveFirstRequest: () => void = () => undefined;
    const firstRequest = new Promise<void>(resolve => {
      resolveFirstRequest = resolve;
    });
    await routeGeocode(page, async route => {
      proxyCalls += 1;
      const query = new URL(route.request().url()).searchParams.get('q') ?? '';
      if (proxyCalls === 1) {
        resolveFirstRequest();
        await new Promise<void>(release => {
          releaseFirstRequest = release;
        });
      }

      await fulfillGeocode(route, [result(`Result for ${query}`)], query);
    });

    await page.goto(absoluteUrl(workspacePath));
    await expectMountedWorkspace(page);
    const mapSearch = page.getByRole('region', { name: 'Map search' });
    const searchInput = page.getByRole('searchbox', { name: 'Map search' });
    const searchButton = mapSearch.getByRole('button', { name: 'Search' });

    await searchInput.fill('first pending query');
    await searchButton.click();
    await firstRequest;
    await expect(searchButton).toBeDisabled();

    await searchInput.press('Enter');
    await searchButton.click({ force: true });
    await page.waitForTimeout(100);
    expect(proxyCalls, 'In-flight Search and Enter submits must not issue another proxy request.').toBe(1);

    releaseFirstRequest?.();
    await expect(mapSearch.getByRole('button', { name: 'Result for first pending query' })).toBeVisible();
    await expect(searchButton).toBeEnabled();

    await searchInput.fill('second completed query');
    await searchInput.press('Enter');
    await expect(mapSearch.getByRole('button', { name: 'Result for second completed query' })).toBeVisible();
    expect(proxyCalls).toBe(2);
  });

  test('clearing a pending map search aborts without surfacing stale errors', async ({ page }) => {
    await signIn(page);
    const pageErrors = collectPageErrors(page);
    let releaseOldRequest: ((status: number) => void) | null = null;
    let resolveOldRequest: () => void = () => undefined;
    const oldRequest = new Promise<void>(resolve => {
      resolveOldRequest = resolve;
    });
    await routeGeocode(page, async route => {
      resolveOldRequest();
      const status = await new Promise<number>(release => {
        releaseOldRequest = release;
      });
      await route.fulfill({ status, contentType: 'application/json', body: '{}' });
    });

    await page.goto(absoluteUrl(workspacePath));
    await expectMountedWorkspace(page);
    const mapSearch = page.getByRole('region', { name: 'Map search' });

    await runSearch(page, 'aborted clear search');
    await oldRequest;
    await expect(mapSearch).toContainText('Searching map...');

    await page.getByRole('searchbox', { name: 'Map search' }).fill('');
    releaseOldRequest?.(503);
    await page.waitForTimeout(100);

    await expect(mapSearch).not.toContainText('Map search provider is unavailable.');
    await expect(mapSearch).not.toContainText('Map search failed.');
    await expect(mapSearch.getByText('Stale Clear Result')).toHaveCount(0);
    expect(pageErrors()).toEqual([]);
  });

  test('query changes during a pending map search ignore stale older results before the next search', async ({ page }) => {
    await signIn(page);
    const pageErrors = collectPageErrors(page);
    let requestCount = 0;
    let releaseOldRequest: (() => void) | null = null;
    let resolveOldRequest: () => void = () => undefined;
    const oldRequest = new Promise<void>(resolve => {
      resolveOldRequest = resolve;
    });
    await routeGeocode(page, async route => {
      requestCount += 1;
      if (requestCount === 1) {
        resolveOldRequest();
        await new Promise<void>(release => {
          releaseOldRequest = release;
        });
        await fulfillGeocode(route, [result('Stale Older Result')], 'older pending search');
        return;
      }

      await fulfillGeocode(route, [result('Current Newer Result')], 'newer replacement search');
    });

    await page.goto(absoluteUrl(workspacePath));
    await expectMountedWorkspace(page);
    const mapSearch = page.getByRole('region', { name: 'Map search' });

    await runSearch(page, 'older pending search');
    await oldRequest;
    await expect(mapSearch).toContainText('Searching map...');
    await expect(mapSearch.getByRole('button', { name: 'Search' })).toBeDisabled();

    await page.getByRole('searchbox', { name: 'Map search' }).fill('newer replacement search');
    await page.getByRole('searchbox', { name: 'Map search' }).press('Enter');
    expect(requestCount, 'Enter must not start a replacement request while the first search is pending.').toBe(1);

    releaseOldRequest?.();
    await expect(mapSearch.getByRole('button', { name: 'Search' })).toBeEnabled();
    await page.waitForTimeout(100);
    await expect(mapSearch.getByText('Stale Older Result')).toHaveCount(0);
    await expect(mapSearch).not.toContainText('Map search failed.');

    await page.getByRole('searchbox', { name: 'Map search' }).press('Enter');
    await expect(mapSearch.getByRole('button', { name: 'Current Newer Result' })).toBeVisible();
    expect(requestCount).toBe(2);
    expect(pageErrors()).toEqual([]);
  });

  test('map search accepts normalized response query echoes without dropping current results', async ({ page }) => {
    await signIn(page);
    await routeGeocode(page, async route => {
      const query = new URL(route.request().url()).searchParams.get('q') ?? '';
      const normalized = query.trim().split(/\s+/u).join(' ').toLowerCase();
      await fulfillGeocode(route, [result(`Result for ${normalized}`)], normalized);
    });

    await page.goto(absoluteUrl(workspacePath));
    await expectMountedWorkspace(page);
    const mapSearch = page.getByRole('region', { name: 'Map search' });

    await runSearch(page, 'Athens');
    await expect(mapSearch.getByRole('button', { name: 'Result for athens' })).toBeVisible();

    await runSearch(page, 'athens   acropolis');
    await expect(mapSearch.getByRole('button', { name: 'Result for athens acropolis' })).toBeVisible();
  });

  test('result preview marker appears, clears, and search-add opens the existing Add Place draft without saving', async ({ page }) => {
    await signIn(page);
    let saveCalls = 0;
    await page.route('**/api/trips/*/editor/regions/*/places', async route => {
      saveCalls += 1;
      await route.fallback();
    });
    await routeGeocode(page, async route => fulfillGeocode(route, [result('Preview Place')]));

    await page.goto(absoluteUrl(workspacePath));
    await expectMountedWorkspace(page);
    await runSearch(page, 'preview place');
    await page.getByRole('button', { name: 'Preview Place' }).click();
    await expect(page.locator('img[alt="Search result preview: Preview Place"]')).toBeVisible();

    await page.getByRole('searchbox', { name: 'Map search' }).fill('');
    await expect(page.locator('img[alt="Search result preview: Preview Place"]')).toHaveCount(0);

    await runSearch(page, 'preview place');
    await page.getByRole('button', { name: 'Preview Place' }).click();
    await addSelectedResult(page);
    await expect(page.getByRole('heading', { name: 'Add Place' })).toBeVisible();
    await expect(page.getByLabel('Name')).toHaveValue('Preview Place');
    await expect(page.getByLabel('Address')).toHaveValue('Athens, Greece');
    await expect(page.getByLabel('Latitude')).toHaveValue('37.9715');
    await expect(page.getByLabel('Longitude')).toHaveValue('23.7257');
    await expect(page.getByLabel('Reverse geocode this location on save')).not.toBeChecked();
    await expect(page.locator('img[alt="Search result preview: Preview Place"]')).toHaveCount(0);
    expect(saveCalls, 'Search-add should only open a draft; Save Place owns persistence.').toBe(0);
    await closeDraftWithDiscard(page);
  });

  test('target region selector excludes shadow regions and handles eligibility defaults', async ({ page }) => {
    await signIn(page);
    const baseState = await loadEditorState(page);
    await routeEditorState(page, withOneEligibleRegion(baseState));
    await routeGeocode(page, async route => fulfillGeocode(route, [result('Eligible Place')]));

    await page.goto(absoluteUrl(workspacePath));
    await expectMountedWorkspace(page);
    await runSearch(page, 'eligible place');
    await page.getByRole('button', { name: 'Eligible Place' }).click();
    const select = page.getByLabel('Target region');
    await expect(select).toBeDisabled();
    await expect(select).not.toContainText('Unassigned Places');

    await routeEditorState(page, withNoEligibleRegions(baseState));
    await page.reload();
    await expectMountedWorkspace(page);
    await runSearch(page, 'eligible place');
    await page.getByRole('button', { name: 'Eligible Place' }).click();
    await expect(page.getByText('No editable region is available for this search result.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Add as place' })).toBeDisabled();

    await routeEditorState(page, withTwoEligibleRegions(baseState));
    await page.reload();
    await expectMountedWorkspace(page);
    await runSearch(page, 'eligible place');
    await page.getByRole('button', { name: 'Eligible Place' }).click();
    await expect(page.getByLabel('Target region')).toBeEnabled();
    await expect(page.getByLabel('Target region')).toHaveValue('');
    await expect(page.getByRole('button', { name: 'Add as place' })).toBeDisabled();
  });

  test('dirty active draft prompts before search-add opens Add Place', async ({ page }) => {
    await signIn(page);
    await routeGeocode(page, async route => fulfillGeocode(route, [result('Prompt Place')]));
    await page.goto(absoluteUrl(workspacePath));
    await expectMountedWorkspace(page);

    await page.getByRole('button', { name: 'Add Region' }).click();
    await page.getByLabel('Name').fill('Dirty draft before search add');
    await runSearch(page, 'prompt place');
    await page.getByRole('button', { name: 'Prompt Place' }).click();
    await addSelectedResult(page);

    const dialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(dialog).toBeVisible();
    await dialog.getByRole('button', { name: 'Discard' }).click();
    await expect(page.getByRole('heading', { name: 'Add Place' })).toBeVisible();
    await expect(page.getByLabel('Name')).toHaveValue('Prompt Place');
    await closeDraftWithDiscard(page);
  });
});

async function runSearch(page: Page, query: string): Promise<void> {
  await page.getByRole('searchbox', { name: 'Map search' }).fill(query);
  await page.getByRole('region', { name: 'Map search' }).getByRole('button', { name: 'Search' }).click();
}

async function addSelectedResult(page: Page): Promise<void> {
  const select = page.getByLabel('Target region');
  if (await select.isEnabled()) {
    const options = await select.locator('option').evaluateAll(nodes => nodes.map(option => ({ value: (option as HTMLOptionElement).value, text: option.textContent ?? '' })));
    const normal = options.find(option => option.value);
    if (normal) {
      await select.selectOption(normal.value);
    }
  }

  await page.getByRole('button', { name: 'Add as place' }).click();
}

async function routeGeocode(page: Page, handler: (route: Route) => Promise<void>): Promise<void> {
  await page.route(geocodePath, handler);
}

async function fulfillGeocode(route: Route, results: unknown[], query?: string): Promise<void> {
  const echoedQuery = query ?? new URL(route.request().url()).searchParams.get('q') ?? '';
  await route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      query: echoedQuery,
      attribution: 'Data source attribution',
      results
    })
  });
}

function result(name: string): unknown {
  return {
    id: `nominatim:${name}`,
    provider: 'nominatim',
    name,
    displayName: `${name}, Athens, Greece`,
    address: 'Athens, Greece',
    category: 'tourism',
    type: 'attraction',
    latitude: 37.9715,
    longitude: 23.7257
  };
}

function collectExternalProviderCalls(page: Page): () => string[] {
  const urls: string[] = [];
  page.on('request', request => {
    if (externalProvider.test(request.url())) {
      urls.push(request.url());
    }
  });
  return () => urls;
}

function collectPageErrors(page: Page): () => string[] {
  const errors: string[] = [];
  page.on('pageerror', error => {
    errors.push(error.message);
  });
  return () => errors;
}

async function loadEditorState(page: Page): Promise<any> {
  const response = await page.request.get(absoluteUrl(editorApiPath), { headers: { Accept: 'application/json' } });
  expect(response.ok()).toBeTruthy();
  return await response.json();
}

async function routeEditorState(page: Page, state: any): Promise<void> {
  await page.unroute(`**${editorApiPath}`).catch(() => undefined);
  await page.route(`**${editorApiPath}`, async route => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
  });
}

function withNoEligibleRegions(state: any): any {
  const clone = structuredClone(state);
  for (const region of Object.values<any>(clone.regionsById)) {
    region.capabilities.canAddChildren = false;
    region.capabilities.canTargetForSearchAdd = false;
  }
  return clone;
}

function withOneEligibleRegion(state: any): any {
  const clone = withNoEligibleRegions(state);
  const region = Object.values<any>(clone.regionsById).find(region => !region.isShadow);
  region.capabilities.canAddChildren = true;
  region.capabilities.canTargetForSearchAdd = true;
  return clone;
}

function withTwoEligibleRegions(state: any): any {
  const clone = withOneEligibleRegion(state);
  const first = Object.values<any>(clone.regionsById).find(region => !region.isShadow);
  const second = { ...first, id: '11111111-1111-4111-8111-111111111111', name: 'Second Search Target', displayOrder: 999 };
  clone.regionsById[second.id] = second;
  clone.regionOrder = [...clone.regionOrder, second.id];
  clone.placeOrderByRegionId[second.id] = [];
  clone.areaOrderByRegionId[second.id] = [];
  return clone;
}
