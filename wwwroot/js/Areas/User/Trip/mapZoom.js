// mapZoomLevels.js – centralized zoom defaults for region/place/segment
export const MAP_ZOOM = {
  region: 11,
  place: 14,
  segment: 'fit' // 'fit' = use polyline bounds, or set to fixed number (e.g., 12)
};

/**
 * Focus map on given coordinates using smart zoom:
 * - Never zooms out
 * - Uses MAP_ZOOM[type]
 *
 * @param {'place'|'region'|'segment'} type
 * @param {[number, number]} coords – [lat, lon]
 * @param {L.Map} map – Leaflet map instance
 */
export const focusMapView = (type, coords, map) => {
  if (!map || !Array.isArray(coords) || coords.length !== 2) return;

  const currentZoom = map.getZoom();
  const desiredZoom = MAP_ZOOM[type];
  if (typeof desiredZoom !== 'number') return;

  const zoom = Math.max(currentZoom, desiredZoom);
  map.setView(coords, zoom);
};
