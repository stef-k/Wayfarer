import { expect, test, type Page } from '@playwright/test';
import path from 'node:path';

const repositoryPath = (...parts: string[]): string => path.resolve(process.cwd(), ...parts);

/** Finds the blue body and counts glyph light only three pixels inside it, beyond the complete two-pixel outline fringe. */
const badgeGlyphPixelCount = (pixels: Uint8ClampedArray, width: number, height: number): number => {
  const bodyPoints: { x: number; y: number }[] = [];
  for (let index = 0; index < pixels.length; index += 4) {
    const pixel = index / 4;
    if (pixels[index + 3] > 180 && pixels[index + 2] > 140
      && pixels[index + 1] > 50 && pixels[index + 1] < 120 && pixels[index] < 30) {
      bodyPoints.push({ x: pixel % width, y: Math.floor(pixel / width) });
    }
  }
  if (!bodyPoints.length) return 0;
  const inset = 3;
  const left = Math.min(...bodyPoints.map(point => point.x)) + inset;
  const top = Math.min(...bodyPoints.map(point => point.y)) + inset;
  const right = Math.max(...bodyPoints.map(point => point.x)) - inset;
  const bottom = Math.max(...bodyPoints.map(point => point.y)) - inset;
  let light = 0;
  for (let y = top; y <= bottom && y < height; y += 1) {
    for (let x = left; x <= right && x < width; x += 1) {
      const index = (y * width + x) * 4;
      if (pixels[index + 3] > 180 && pixels[index] > 220
        && pixels[index + 1] > 220 && pixels[index + 2] > 220) light += 1;
    }
  }
  return light;
};

/** Proves a white outline without a glyph is not acceptable decoded glyph evidence. */
test('route badge glyph classifier rejects an outline-only negative control', () => {
  const size = 24;
  const pixels = new Uint8ClampedArray(size * size * 4);
  for (let y = 0; y < size; y += 1) {
    for (let x = 0; x < size; x += 1) {
      const index = (y * size + x) * 4;
      const outline = x < 2 || x >= size - 2 || y < 2 || y >= size - 2;
      pixels.set(outline ? [255, 255, 255, 255] : [0, 87, 184, 255], index);
    }
  }

  expect(badgeGlyphPixelCount(pixels, size, size)).toBe(0);
});

/** Loads Leaflet assets while retaining the configured application origin for production module imports. */
async function prepareProductionMap(page: Page, search: string): Promise<void> {
  await page.goto('/');
  await page.setContent('<div id="map" style="width:320px;height:240px"></div>');
  await page.evaluate(query => history.replaceState(null, '', `${location.pathname}${query}`), search);
  await page.addStyleTag({ path: repositoryPath('wwwroot/lib/leaflet/leaflet-1.9.4.css') });
  await page.addScriptTag({ path: repositoryPath('wwwroot/lib/leaflet/leaflet-1.9.4.js') });
  await page.addScriptTag({ path: repositoryPath('wwwroot/lib/leaflet-image/leaflet-image.js') });
}

/** Invokes the production renderer and inspects only a fresh decode of its published snapshot URL. */
test('isolated production snapshot includes route, chevron, badge shape, and badge text', async ({ page }) => {
  await page.route('**/test-segment-tile/**', route => route.fulfill({
    path: repositoryPath('wwwroot/logo-transparent.png'), contentType: 'image/png'
  }));
  await prepareProductionMap(page, '?print=1&seg=canvas-segment');

  const proof = await page.evaluate(async () => {
    (window as any).wayfarerTileConfig = { tilesUrl: '/test-segment-tile/{z}/{x}/{y}.png', attribution: '' };
    const helpers = await import('/js/Trip/tripViewerHelpers.js');
    const controllerModule = await import('/js/Trip/viewerSegmentPresentationController.js');
    document.querySelector('#map')!.id = 'mapContainer';
    const map = helpers.initLeaflet([0, 0], 3);
    helpers.addSegment(map, 'canvas-segment', [[0, -10], [0, 10]], '', {
      orientation: 'forward',
      anchors: [
        { position: 0, placeId: 'start', name: 'Start', role: 'Start', longitude: -10, latitude: 0 },
        { position: 1, placeId: 'end', name: 'End', role: 'End', longitude: 10, latitude: 0 }
      ]
    });
    const controller = controllerModule.createViewerSegmentPresentationController(map, document.body, { isPrint: true, paddingX: () => 60 });
    const ready = await controller.initialize('canvas-segment');
    const dataUrl = await new Promise<string>((resolve, reject) => {
      const deadline = performance.now() + 5000;
      const poll = () => {
        if ((window as any).__leafletImageUrl) resolve((window as any).__leafletImageUrl);
        else if (performance.now() >= deadline) reject(new Error('Production snapshot URL was not published.'));
        else requestAnimationFrame(poll);
      };
      poll();
    });
    const decoded = new Image();
    decoded.src = dataUrl;
    await decoded.decode();
    const inspection = document.createElement('canvas');
    inspection.width = decoded.naturalWidth;
    inspection.height = decoded.naturalHeight;
    const context = inspection.getContext('2d')!;
    context.drawImage(decoded, 0, 0);
    const count = (left: number, top: number, width: number, height: number,
      predicate: (red: number, green: number, blue: number, alpha: number) => boolean): number => {
      const pixels = context.getImageData(left, top, width, height).data;
      let total = 0;
      for (let index = 0; index < pixels.length; index += 4) {
        if (predicate(pixels[index], pixels[index + 1], pixels[index + 2], pixels[index + 3])) total += 1;
      }
      return total;
    };
    const midpoint = map.latLngToContainerPoint([0, 0]);
    // Disjoint fixture regions keep the cyan line, dark cue, blue badge, and inner white glyph independent.
    const routeBlue = count(midpoint.x - 45, midpoint.y - 3, 30, 6,
      (red, green, blue, alpha) => alpha > 180 && blue > 170 && green > 90 && red < 40);
    const cueBlue = count(midpoint.x - 12, midpoint.y - 12, 24, 24,
      (red, green, blue, alpha) => alpha > 180 && blue > 90 && blue < 170 && green > 60 && green < 140 && red < 30);
    const allPixels = context.getImageData(0, 0, inspection.width, inspection.height).data;
    const badgePoints: { x: number; y: number }[] = [];
    for (let index = 0; index < allPixels.length; index += 4) {
      const pixel = index / 4;
      const [red, green, blue, alpha] = [allPixels[index], allPixels[index + 1], allPixels[index + 2], allPixels[index + 3]];
      if (pixel % inspection.width < inspection.width / 2
        && alpha > 180 && blue > 140 && green > 50 && green < 120 && red < 30) {
        badgePoints.push({ x: pixel % inspection.width, y: Math.floor(pixel / inspection.width) });
      }
    }
    const badgeBox = {
      left: Math.min(...badgePoints.map(point => point.x)), top: Math.min(...badgePoints.map(point => point.y)),
      right: Math.max(...badgePoints.map(point => point.x)), bottom: Math.max(...badgePoints.map(point => point.y))
    };
    const badgeBlue = badgePoints.length;
    // Three pixels inside the production-blue body excludes its complete two-pixel white outline and antialiasing fringe.
    const glyphInset = 3;
    const badgeGlyphLight = count(badgeBox.left + glyphInset, badgeBox.top + glyphInset,
      badgeBox.right - badgeBox.left + 1 - glyphInset * 2, badgeBox.bottom - badgeBox.top + 1 - glyphInset * 2,
      (red, green, blue, alpha) => alpha > 180 && red > 220 && green > 220 && blue > 220);
    const snapshot = (window as any).__segmentPresentationSnapshot;
    map.remove();
    return { ready, dataUrl, decodedWidth: inspection.width, routeBlue, cueBlue, badgeBlue, badgeGlyphLight, snapshot };
  });

  expect(proof.ready).toBe(true);
  expect(proof.dataUrl).toMatch(/^data:image\/png;base64,/);
  expect(proof.decodedWidth).toBeGreaterThan(0);
  expect(proof.snapshot).toMatchObject({ routeBadgeCount: 2, segments: [{ id: 'canvas-segment', active: true, lineCount: 1 }] });
  expect(proof.snapshot.segments[0].chevronCount).toBeGreaterThan(0);
  expect(proof.routeBlue).toBeGreaterThan(40);
  expect(proof.cueBlue).toBeGreaterThan(8);
  expect(proof.badgeBlue).toBeGreaterThan(90);
  // A production 12 px bold "A" contributes well above eight interior light pixels; eight leaves margin without accepting fringe noise.
  expect(proof.badgeGlyphLight).toBeGreaterThan(8);
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
