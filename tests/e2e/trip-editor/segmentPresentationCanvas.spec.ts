import { expect, test } from '@playwright/test';
import path from 'node:path';

const repositoryPath = (...parts: string[]): string => path.resolve(process.cwd(), ...parts);

/** Proves the owned primitives survive the existing decoded leaflet-image canvas path. */
test('canvas snapshot includes route, chevron, badge shape, and badge text', async ({ page }) => {
  await page.setContent('<div id="map" style="width:320px;height:240px"></div>');
  await page.addStyleTag({ path: repositoryPath('wwwroot/lib/leaflet/leaflet-1.9.4.css') });
  await page.addScriptTag({ path: repositoryPath('wwwroot/lib/leaflet/leaflet-1.9.4.js') });
  await page.addScriptTag({ path: repositoryPath('wwwroot/lib/leaflet-image/leaflet-image.js') });

  const proof = await page.evaluate(async () => {
    const leaflet = (window as typeof window & { L: typeof L; leafletImage: Function }).L;
    const leafletImage = (window as typeof window & { leafletImage: Function }).leafletImage;
    const map = leaflet.map('map', { zoomControl: false, attributionControl: false, zoomAnimation: false }).setView([0, 0], 3);
    const renderer = leaflet.canvas({ padding: 0 });
    leaflet.polyline([[0, -10], [0, 10]], { color: '#0d6efd', weight: 6, renderer }).addTo(map);
    leaflet.polyline([[1, -1], [0, 0], [-1, -1]], { color: '#ff00ff', weight: 5, renderer }).addTo(map);

    const badge = document.createElement('canvas');
    badge.width = 48;
    badge.height = 28;
    const badgeContext = badge.getContext('2d')!;
    badgeContext.fillStyle = '#0057b8';
    badgeContext.roundRect(0, 0, 48, 28, 14);
    badgeContext.fill();
    badgeContext.fillStyle = '#ffffff';
    badgeContext.font = 'bold 16px sans-serif';
    badgeContext.textAlign = 'center';
    badgeContext.textBaseline = 'middle';
    badgeContext.fillText('A/C', 24, 14);
    leaflet.marker([0, 6], {
      icon: leaflet.icon({ iconUrl: badge.toDataURL('image/png'), iconSize: [48, 28], iconAnchor: [0, 28] }),
      interactive: false,
      keyboard: false,
      alt: ''
    }).addTo(map);

    await new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
    const canvas = await new Promise<HTMLCanvasElement>((resolve, reject) => leafletImage(map, (error: Error | null, result: HTMLCanvasElement) => error ? reject(error) : resolve(result)));
    const context = canvas.getContext('2d')!;
    const pixels = context.getImageData(0, 0, canvas.width, canvas.height).data;
    let routeBlue = 0;
    let chevronMagenta = 0;
    let badgeBlue = 0;
    let badgeWhite = 0;
    for (let index = 0; index < pixels.length; index += 4) {
      const [red, green, blue, alpha] = [pixels[index], pixels[index + 1], pixels[index + 2], pixels[index + 3]];
      if (alpha > 180 && blue > 180 && red < 80 && green > 60 && green < 180) routeBlue += 1;
      if (alpha > 180 && red > 180 && blue > 180 && green < 80) chevronMagenta += 1;
      if (alpha > 180 && blue > 120 && red < 40 && green > 50 && green < 140) badgeBlue += 1;
      if (alpha > 180 && red > 220 && green > 220 && blue > 220) badgeWhite += 1;
    }
    map.remove();
    return { width: canvas.width, height: canvas.height, dataUrl: canvas.toDataURL('image/png'), routeBlue, chevronMagenta, badgeBlue, badgeWhite };
  });

  expect(proof.dataUrl).toMatch(/^data:image\/png;base64,/);
  expect(proof).toMatchObject({ width: 320, height: 240 });
  expect(proof.routeBlue).toBeGreaterThan(100);
  expect(proof.chevronMagenta).toBeGreaterThan(10);
  expect(proof.badgeBlue).toBeGreaterThan(200);
  expect(proof.badgeWhite).toBeGreaterThan(10);
});
