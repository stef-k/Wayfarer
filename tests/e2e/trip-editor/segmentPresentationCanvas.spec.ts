import { expect, test, type Page } from '@playwright/test';
import path from 'node:path';

const repositoryPath = (...parts: string[]): string => path.resolve(process.cwd(), ...parts);

/** Loads Leaflet assets while retaining the configured application origin for production module imports. */
async function prepareProductionMap(page: Page, search: string): Promise<void> {
  await page.goto('/');
  await page.setContent('<div id="map" style="width:320px;height:240px"></div>');
  await page.evaluate(query => history.replaceState(null, '', `${location.pathname}${query}`), search);
  await page.addStyleTag({ path: repositoryPath('wwwroot/lib/leaflet/leaflet-1.9.4.css') });
  await page.addScriptTag({ path: repositoryPath('wwwroot/lib/leaflet/leaflet-1.9.4.js') });
  await page.addScriptTag({ path: repositoryPath('wwwroot/lib/leaflet-image/leaflet-image.js') });
}

/** Invokes the production Segment renderer and readiness gate before decoding its leaflet-image output. */
test('isolated production snapshot includes route, chevron, badge shape, and badge text', async ({ page }) => {
  await prepareProductionMap(page, '?print=1&seg=canvas-segment');

  const proof = await page.evaluate(async () => {
    const helpers = await import('/js/Trip/tripViewerHelpers.js');
    const controllerModule = await import('/js/Trip/viewerSegmentPresentationController.js');
    const leaflet = (window as any).L;
    const map = leaflet.map('map', { zoomControl: false, attributionControl: false, zoomAnimation: false }).setView([0, 0], 3);
    helpers.addSegment(map, 'canvas-segment', [[0, -10], [0, 10]], '', {
      orientation: 'forward',
      anchors: [
        { position: 0, placeId: 'start', name: 'Start', role: 'Start', longitude: -10, latitude: 0 },
        { position: 1, placeId: 'end', name: 'End', role: 'End', longitude: 10, latitude: 0 }
      ]
    });
    const controller = controllerModule.createViewerSegmentPresentationController(map, document.body, { isPrint: true, paddingX: () => 60 });
    const ready = await controller.initialize('canvas-segment');
    const canvas = await new Promise<HTMLCanvasElement>((resolve, reject) => (window as any).leafletImage(map,
      (error: Error | null, result: HTMLCanvasElement) => error ? reject(error) : resolve(result)));
    (window as any).__leafletImageUrl = canvas.toDataURL('image/png');
    const pixels = canvas.getContext('2d')!.getImageData(0, 0, canvas.width, canvas.height).data;
    let routeBlue = 0;
    let cueBlue = 0;
    let badgeBlue = 0;
    let badgeWhite = 0;
    for (let index = 0; index < pixels.length; index += 4) {
      const [red, green, blue, alpha] = [pixels[index], pixels[index + 1], pixels[index + 2], pixels[index + 3]];
      if (alpha > 180 && blue > 180 && red < 80 && green > 60 && green < 180) routeBlue += 1;
      if (alpha > 180 && blue > 90 && red < 40 && green > 70 && green < 150) cueBlue += 1;
      if (alpha > 180 && blue > 120 && red < 40 && green > 50 && green < 140) badgeBlue += 1;
      if (alpha > 180 && red > 220 && green > 220 && blue > 220) badgeWhite += 1;
    }
    const snapshot = (window as any).__segmentPresentationSnapshot;
    map.remove();
    return { ready, dataUrl: (window as any).__leafletImageUrl, routeBlue, cueBlue, badgeBlue, badgeWhite, snapshot };
  });

  expect(proof.ready).toBe(true);
  expect(proof.dataUrl).toMatch(/^data:image\/png;base64,/);
  expect(proof.snapshot).toMatchObject({ routeBadgeCount: 2, segments: [{ id: 'canvas-segment', active: true, lineCount: 1 }] });
  expect(proof.snapshot.segments[0].chevronCount).toBeGreaterThan(0);
  expect(proof.routeBlue).toBeGreaterThan(100);
  expect(proof.cueBlue).toBeGreaterThan(10);
  expect(proof.badgeBlue).toBeGreaterThan(100);
  expect(proof.badgeWhite).toBeGreaterThan(10);
});

/** Proves overview readiness describes a genuinely empty production Segment registry. */
test('overview production snapshot contains no Segment-owned presentation', async ({ page }) => {
  await prepareProductionMap(page, '?print=1');

  const result = await page.evaluate(async () => {
    const helpers = await import('/js/Trip/tripViewerHelpers.js');
    const controllerModule = await import('/js/Trip/viewerSegmentPresentationController.js');
    const leaflet = (window as any).L;
    const map = leaflet.map('map', { zoomControl: false, attributionControl: false, zoomAnimation: false }).setView([0, 0], 3);
    helpers.addSegment(map, 'filtered-segment', [[0, -10], [0, 10]], '', { orientation: 'forward', anchors: [] });
    const controller = controllerModule.createViewerSegmentPresentationController(map, document.body, { isPrint: true, paddingX: () => 60 });
    const ready = await controller.initialize(null);
    const snapshot = (window as any).__segmentPresentationSnapshot;
    map.remove();
    return { ready, snapshot };
  });

  expect(result.ready).toBe(true);
  expect(result.snapshot).toEqual({ segments: [], routeBadgeCount: 0 });
});

/** Observes an ordinary URL-requested Segment through the mounted production viewer controller. */
test('normal mounted viewer activates and fits exactly the requested registered Segment', async ({ page }) => {
  await prepareProductionMap(page, '?seg=normal-one');

  const result = await page.evaluate(async () => {
    document.body.insertAdjacentHTML('beforeend', `
      <div id="trip-view">
        <div class="segment-list-item" data-segment-id="normal-one"><button class="segment-selection-button">One</button></div>
        <div class="segment-list-item" data-segment-id="normal-two"><button class="segment-selection-button">Two</button></div>
      </div>`);
    const helpers = await import('/js/Trip/tripViewerHelpers.js');
    const controllerModule = await import('/js/Trip/viewerSegmentPresentationController.js');
    const leaflet = (window as any).L;
    const map = leaflet.map('map', { zoomControl: false, attributionControl: false, zoomAnimation: false }).setView([0, 0], 3);
    helpers.addSegment(map, 'normal-one', [[0, -10], [0, 10]], '', { orientation: 'forward', anchors: [
      { position: 0, placeId: 'a', name: 'A', role: 'Start', longitude: -10, latitude: 0 },
      { position: 1, placeId: 'b', name: 'B', role: 'End', longitude: 10, latitude: 0 }
    ] });
    helpers.addSegment(map, 'normal-two', [[10, -10], [10, 10]], '', { orientation: 'forward', anchors: [
      { position: 0, placeId: 'c', name: 'C', role: 'Start', longitude: -10, latitude: 10 },
      { position: 1, placeId: 'd', name: 'D', role: 'End', longitude: 10, latitude: 10 }
    ] });
    let fitted: any = null;
    (map as any).flyToBounds = (bounds: any, options: any) => { fitted = { bounds: bounds.toBBoxString(), options }; return map; };
    const root = document.querySelector<HTMLElement>('#trip-view')!;
    const controller = controllerModule.createViewerSegmentPresentationController(map, root, { isPrint: false, paddingX: () => 75 });
    await controller.initialize(new URLSearchParams(location.search).get('seg'));
    const snapshot = helpers.getSegmentPresentationSnapshot();
    const aria = [...document.querySelectorAll('.segment-selection-button')].map(button => button.getAttribute('aria-current'));
    const requestedBounds = helpers.getSegmentPolyline('normal-one').getBounds().toBBoxString();
    map.remove();
    return { snapshot, aria, activeId: root.dataset.activeSegmentId, fitted, requestedBounds };
  });

  expect(result.snapshot.segments).toHaveLength(2);
  expect(result.snapshot.segments.find((segment: any) => segment.id === 'normal-one')).toMatchObject({ active: true, lineCount: 1 });
  expect(result.snapshot.segments.find((segment: any) => segment.id === 'normal-two')).toMatchObject({ active: false, lineCount: 1 });
  expect(result.snapshot.routeBadgeCount).toBe(2);
  expect(result.aria).toEqual(['true', null]);
  expect(result.activeId).toBe('normal-one');
  expect(result.fitted).toEqual({ bounds: result.requestedBounds, options: { animate: true, duration: 1.2, padding: [75, 60] } });
});
