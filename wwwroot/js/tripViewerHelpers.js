/* tripViewerHelpers.js – read-only viewer helpers
 * -----------------------------------------------
 *  • Leaflet bootstrap
 *  • region & place PNG markers
 *  • segment polylines + visibility
 */

import {addZoomLevelControl} from './map-utils.js';

/* ---------- Wayfarer PNG marker URL ---------- */
const png = (icon, bg) => `/icons/wayfarer-map-icons/dist/png/marker/${bg}/${icon}.png`;

/* ---------- marker sizing ---------- */
const WF_WIDTH = 28;
const WF_HEIGHT = 45;
const WF_ANCHOR = [14, 45];
export const getPlaceMarker = pid => _places[pid]?.marker ?? null;
export const getSegmentPolyline = sid => _segments[sid] ?? null;
const canvasRenderer = L.canvas();

/* ---------- map bootstrap ---------- */
/* tripViewerHelpers.js
 * -----------------------------------------------------------
 * initLeaflet – single source of truth for the “print-mode” flag.
 * It raises window.__leafletTilesOk **once** when all tiles are decoded.
 */
export const initLeaflet = (center = [20, 0], zoom = 3) => {
    /* ─── detect exporter’s &print=1 ─── */
    const isPrint = location.search.includes('print=1');

    /* exporter / Puppeteer waits for this flag */
    window.__leafletTilesOk = false;

    /* ─── create map ─── */
    const map = L.map('mapContainer', {
        zoomAnimation: !isPrint, fadeAnimation: !isPrint, zoomControl: false
    }).setView(center, zoom);

    /* keep a handle to the tile layer so we can attach events */
    const tiles = L.tileLayer(`${location.origin}/Public/tiles/{z}/{x}/{y}.png`, {
        maxZoom: 19, attribution: '© OpenStreetMap contributors'
    }).addTo(map);
    map.attributionControl.setPrefix('&copy; <a href="https://wayfarer.stefk.me" title="Powered by Wayfarer, made by Stef" target="_blank">Wayfarer</a> | <a href="https://stefk.me" title="Check my blog" target="_blank">Stef K</a> | &copy; <a href="https://leafletjs.com/" target="_blank">Leaflet</a>');
    L.control.zoom({position: 'bottomright'}).addTo(map);
    addZoomLevelControl(map);                 /* ← your existing util */

    if (isPrint) {
        console.log('[print] leaflet-image bootstrap…');

        map.whenReady(() => {                       // map ready = has centre + zoom
            console.log('[print] map ready');

            // Wait until *all* visible tiles are decoded
            tiles.once('load', () => {                // fires exactly once per page
                console.log('[print] tile layer loaded');

                if (!window.leafletImage) {
                    console.error('[print] leafletImage() missing – script not loaded!');
                    return;
                }

                window.leafletImage(map, (err, canvas) => {
                    if (err) {
                        console.error('[print] leaflet-image error', err);
                        return;
                    }

                    window.__leafletImageUrl = canvas.toDataURL('image/png');
                    console.log('[print] snapshot ready, length =', window.__leafletImageUrl.length);
                });
            });
        });
    }

    /* keep the existing “resize → invalidateSize()” behaviour */
    window.addEventListener('resize', () => map.invalidateSize());

    return map;
};


/* ---------- helpers ---------- */
const num = v => {
    const n = Number(v);
    return Number.isFinite(n) ? n : null;
};
const icon = s => (s ?? '').trim() || 'marker';
const bg = s => (s ?? '').trim() || 'bg-blue';

/* ---------- registries ---------- */
const _regions = {};             // regionId → centroid marker
const _places = {};             // placeId  → {marker, regionId}
const _segments = {};             // segmentId → polyline

/* ---------- region centroid ---------- */
export const addRegionMarker = (map, id, [lat, lon], name = '') => {
    lat = num(lat);
    lon = num(lon);
    if (lat === null || lon === null) return;

    const iconUrl = png('map', 'bg-red');
    const leafletIcon = L.icon({
        iconUrl, iconSize: [WF_WIDTH, WF_HEIGHT], iconAnchor: WF_ANCHOR, className: 'map-icon'
    });

    const m = L.marker([lat, lon], {icon: leafletIcon})
        .bindTooltip(name, {direction: 'right'})
        .addTo(map);

    _regions[id] = m;
};

/* ---------- place pin ---------- */
export const addPlaceMarker = (map, id, [lat, lon], opts = {}) => {
    lat = num(lat);
    lon = num(lon);
    if (lat === null || lon === null) return;

    const iconUrl = png(icon(opts.icon), bg(opts.color));
    const leafletIcon = L.icon({
        iconUrl, iconSize: [WF_WIDTH, WF_HEIGHT], iconAnchor: WF_ANCHOR, className: 'map-icon'
    });

    const m = L.marker([lat, lon], {icon: leafletIcon})
        .bindTooltip(opts.name || '', {direction: 'right'})
        .addTo(map);

    m.on('click', () => window.wayfarer?.openPlaceDetails?.(id));
    _places[id] = {marker: m, regionId: opts.region ?? null};
};

/* ---------- segment poly-line ---------- */
export const addSegment = (map, id, coords = [], label = '') => {
    if (!Array.isArray(coords) || coords.length < 2) return;

    const pl = L.polyline(coords, {
        color: '#0d6efd', weight: 3, className: 'segment-line', // Canvas renderer is needed for leaflet-image
        renderer: location.search.includes('print=1') ? canvasRenderer : undefined
    }).bindTooltip(label, {sticky: true, direction: 'top'});

    pl.addTo(map);
    _segments[id] = pl;
    /* ─── PRINT-MODE visibility filter ────────────────────────
     * TripExportService now appends “…&seg=<guid>” only for a
     * “segment” snapshot.  Anything else (overview, region, place)
     * carries no “seg=” param, so we want *zero* polylines there.
     */
    if (location.search.includes('print=1')) {
        const segParam = new URLSearchParams(location.search).get('seg');

        //  no "seg="   → hide every poly-line
        //  mismatch ID → hide this one
        if (!segParam || segParam !== id) {
            map.removeLayer(pl);          // invisible but still in _segments
        }
    }
};

/* ---------- visibility helpers ---------- */
export const setRegionVisible = (map, rid, visible) => {
    if (_regions[rid]) visible ? map.addLayer(_regions[rid]) : map.removeLayer(_regions[rid]);

    Object.values(_places).forEach(p => {
        if (p.regionId === rid) visible ? map.addLayer(p.marker) : map.removeLayer(p.marker);
    });
};

export const setSegmentVisible = (map, sid, visible) => {
    if (_segments[sid]) visible ? map.addLayer(_segments[sid]) : map.removeLayer(_segments[sid]);
};

/* ---------- tiny WKT LINESTRING → [lat,lon][] ---------- */
export const wktToCoords = wkt => {
    const m = /^LINESTRING\s*\(\s*(.*?)\s*\)$/i.exec(wkt ?? '');
    if (!m) return [];
    return m[1]
        .split(',')
        .map(p => p.trim().split(/\s+/).map(Number))
        .filter(a => a.length === 2 && !isNaN(a[0]) && !isNaN(a[1]))
        .map(([lon, lat]) => [lat, lon]);      // leaflet order is [lat,lon]
};
