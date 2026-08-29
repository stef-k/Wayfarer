/* tripViewerHelpers.js – read-only viewer helpers
 * -----------------------------------------------
 *  • Leaflet bootstrap
 *  • region & place PNG markers
 *  • segment polylines + visibility
 *  • Rich popups with Google Maps & Wikipedia links
 */

import {addZoomLevelControl} from '../map-utils.js';
import {createTileLayer} from '../retryTileLayer.js';
import {
    buildPlacePopup,
    buildSegmentPopup,
    buildAreaPopup
} from './tripPopupBuilder.js';
import {placeViewerChevrons, presentViewerCoordinates, projectChevronArm, resolveViewerAnchors} from './segmentPresentation.js';
import {createViewerSegmentBadgeRenderer} from './viewerSegmentBadgeRenderer.js';

/* ---------- Wayfarer PNG marker URL ---------- */
const png = (icon, bg) => `/icons/wayfarer-map-icons/dist/png/marker/${bg}/${icon}.png`;

/* ---------- marker sizing ---------- */
const WF_WIDTH = 28;
const WF_HEIGHT = 45;
const WF_ANCHOR = [14, 45];
export const getPlaceMarker = pid => _places[pid]?.marker ?? null;
export const getSegmentPolyline = sid => _segments[sid]?.line ?? null;
export const canvasRenderer = L.canvas();

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
    window.__leafletImageUrl = null;
    window.__segmentPresentationReady = !isPrint;

    /* ─── create map ─── */
    const map = L.map('mapContainer', {
        zoomAnimation: !isPrint, fadeAnimation: !isPrint, zoomControl: false
    }).setView(center, zoom);

    /* keep a handle to the tile layer so we can attach events */
    const tiles = createTileLayer().addTo(map);
    map.attributionControl.setPrefix('&copy; <a href="https://wayfarer.stefk.me" title="Powered by Wayfarer, made by Stef" target="_blank" rel="noopener">Wayfarer</a> | <a href="https://stefk.me" title="Check my blog" target="_blank" rel="noopener">Stef K</a> | &copy; <a href="https://leafletjs.com/" target="_blank" rel="noopener">Leaflet</a>');
    // Match the Trip Editor's accessible attribution-control contract.
    map.attributionControl.getContainer()?.setAttribute('aria-label', 'Map attribution');
    map.attributionControl.getContainer()?.setAttribute('title', 'Map attribution');
    L.control.zoom({position: 'bottomright'}).addTo(map);
    addZoomLevelControl(map);                 /* ← your existing util */

    if (isPrint) {
        console.log('[print] leaflet-image bootstrap…');

        map.whenReady(() => {                       // map ready = has centre + zoom
            console.log('[print] map ready');

            // Wait until *all* visible tiles are decoded
            tiles.once('load', async () => {                // fires exactly once per page
                console.log('[print] tile layer loaded');
                window.__leafletTilesOk = true;

                const presentationReady = await waitForPresentationReady();
                if (!presentationReady) {
                    console.error('[print] segment presentation did not become ready');
                    return;
                }

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
let _activeSegmentId = null;
let _badgeRenderer = null;

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

    // Canonical Place identity is replace-only across repeated viewer renders.
    const existing = _places[id]?.marker;
    if (existing) {
        existing.off();
        map.removeLayer(existing);
        delete _places[id];
    }

    const iconUrl = png(icon(opts.icon), bg(opts.color));

    // Use divIcon with visited badge when place has been visited
    let leafletIcon;
    const visitCount = opts.visitCount || 0;
    if (visitCount > 0) {
        // Show checkmark for 1 visit, show count for multiple visits
        const badgeContent = visitCount === 1 ? '✓' : visitCount;
        const badgeTitle = visitCount === 1 ? 'Visited' : `Visited ${visitCount} times`;
        leafletIcon = L.divIcon({
            className: 'place-marker-wrapper',
            html: `<div class="place-marker visited">
                     <img src="${iconUrl}" width="${WF_WIDTH}" height="${WF_HEIGHT}" alt="">
                     <span class="visit-badge" title="${badgeTitle}">${badgeContent}</span>
                   </div>`,
            iconSize: [WF_WIDTH, WF_HEIGHT],
            iconAnchor: WF_ANCHOR
        });
    } else {
        leafletIcon = L.icon({
            iconUrl, iconSize: [WF_WIDTH, WF_HEIGHT], iconAnchor: WF_ANCHOR, className: 'map-icon'
        });
    }

    // Build rich tooltip content for hover
    const tooltipContent = buildPlacePopup({
        name: opts.name,
        lat,
        lon,
        address: opts.address,
        resolvedFeatureName: opts.resolvedFeatureName,
        resolvedFeatureType: opts.resolvedFeatureType,
        notes: opts.notes,
        regionName: opts.regionName
    });

    const m = L.marker([lat, lon], {icon: leafletIcon})
        .bindTooltip(tooltipContent, {
            direction: 'right',
            className: 'trip-rich-tooltip',
            permanent: false
        })
        .addTo(map);

    m.on('click', () => window.wayfarer?.openPlaceDetails?.(id));
    _places[id] = {marker: m, regionId: opts.region ?? null};
};

/* ---------- segment poly-line ---------- */
export const addSegment = (map, id, coords = [], label = '', opts = {}) => {
    if (!Array.isArray(coords) || coords.length < 2) return;
    const isPrint = location.search.includes('print=1');
    const isolatedId = new URLSearchParams(location.search).get('seg');
    if (isPrint && (!isolatedId || isolatedId !== id)) return null;

    // A Segment registry entry owns one line; rerender replaces and detaches the prior layer.
    const existing = _segments[id];
    if (existing) {
        removeSegmentEntry(existing);
        delete _segments[id];
    }

    _badgeRenderer ??= createViewerSegmentBadgeRenderer(map);
    const orientation = opts.orientation ?? 'forward';
    const presentation = resolveViewerAnchors(opts.anchors ?? []);
    const presentedCoords = presentViewerCoordinates(coords, orientation);
    const active = id === _activeSegmentId || isPrint && isolatedId === id;
    if (active) _activeSegmentId = id;

    // Build rich popup content if segment data provided
    let popupContent = null;
    if (opts.fromPlace || opts.toPlace) {
        popupContent = buildSegmentPopup({
            fromPlace: opts.fromPlace || 'Start',
            toPlace: opts.toPlace || 'End',
            fromRegion: opts.fromRegion,
            toRegion: opts.toRegion,
            mode: opts.mode,
            distance: opts.distance,
            duration: opts.duration,
            notes: opts.notes,
            fromLat: presentedCoords[0]?.[0],
            fromLon: presentedCoords[0]?.[1],
            toLat: presentedCoords[presentedCoords.length - 1]?.[0],
            toLon: presentedCoords[presentedCoords.length - 1]?.[1]
        });
    }

    const group = L.layerGroup().addTo(map);
    const pl = L.polyline(presentedCoords, {
        color: orientation === 'ambiguous' ? '#6c757d' : '#0d6efd', weight: active ? 5 : 3,
        opacity: active ? 1 : 0.72, className: 'segment-line', renderer: isPrint ? canvasRenderer : undefined,
        interactive: false
    });

    // Bind rich tooltip for hover if we have segment data
    if (popupContent) {
        pl.bindTooltip(popupContent, {
            sticky: true,
            direction: 'top',
            className: 'trip-rich-tooltip'
        });
    } else {
        // Fallback to simple label tooltip
        pl.bindTooltip(label, {sticky: true, direction: 'top'});
    }

    pl.addTo(group);
    const hit = isPrint ? null : L.polyline(presentedCoords, {opacity: 0, weight: 16, className: 'segment-route-hit'})
        .bindTooltip(orientation === 'ambiguous' ? 'Route direction unavailable' : presentation.tooltip,
            {sticky: true, direction: 'top', className: 'trip-rich-tooltip'})
        .on('click', () => window.wayfarer?.selectSegment?.(id))
        .addTo(group);
    const entry = {id, group, line: pl, hit, chevrons: [], presentation, orientation, coords: presentedCoords, visible: true, active};
    _segments[id] = entry;
    renderSegmentDecorations(map, entry);
    if (active && orientation !== 'ambiguous') renderActiveBadges(map, entry);
    return pl;
};

/** Waits for route registry, raster badge assets, and the issue-required two frames. */
const waitForPresentationReady = async () => {
    const deadline = performance.now() + 10000;
    while (!window.__segmentPresentationReady && performance.now() < deadline) {
        await new Promise(resolve => requestAnimationFrame(resolve));
    }
    return window.__segmentPresentationReady === true;
};

/* ---------- area polygon ---------- */
export const addAreaPolygon = (map, id, coords = [], opts = {}) => {
    if (!Array.isArray(coords) || coords.length < 3) return null;

    const fill = opts.fill || '#3388ff';
    const poly = L.polygon(coords, {
        color: fill,
        fillColor: fill,
        weight: 1,
        opacity: 0.7,
        fillOpacity: 0.1,
        renderer: location.search.includes('print=1') ? canvasRenderer : undefined
    });

    // Build rich tooltip content for hover if area data provided
    if (opts.name) {
        const tooltipContent = buildAreaPopup({
            name: opts.name,
            notes: opts.notes
        });

        poly.bindTooltip(tooltipContent, {
            direction: 'right',
            className: 'trip-rich-tooltip'
        });
    }

    poly.addTo(map);
    return poly;
};

/* ---------- visibility helpers ---------- */
export const setRegionVisible = (map, rid, visible) => {
    if (_regions[rid]) visible ? map.addLayer(_regions[rid]) : map.removeLayer(_regions[rid]);

    Object.values(_places).forEach(p => {
        if (p.regionId === rid) visible ? map.addLayer(p.marker) : map.removeLayer(p.marker);
    });
};

export const setSegmentVisible = (map, sid, visible) => {
    const entry = _segments[sid];
    if (!entry) return;
    entry.visible = visible;
    visible ? entry.group.addTo(map) : entry.group.remove();
    renderSegmentDecorations(map, entry);
    if (!visible && _activeSegmentId === sid) setActiveSegment(map, null);
};

/** Transfers active emphasis and badges without changing either Segment aggregate. */
export const setActiveSegment = (map, sid) => {
    _activeSegmentId = sid && _segments[sid]?.visible ? sid : null;
    _badgeRenderer?.clear();
    Object.values(_segments).forEach(entry => {
        entry.active = entry.id === _activeSegmentId;
        entry.line.setStyle({weight: entry.active ? 5 : 3, opacity: entry.active ? 1 : 0.72});
        renderSegmentDecorations(map, entry);
    });
    const active = _activeSegmentId ? _segments[_activeSegmentId] : null;
    if (active) renderActiveBadges(map, active);
};

/** Exposes bounded serializable registry evidence without private text. */
export const getSegmentPresentationSnapshot = () => ({
    segments: Object.values(_segments).map(entry => ({
        id: entry.id, source: 'S', visible: entry.visible, active: entry.active, orientation: entry.orientation,
        lineCount: 1, hitLayerCount: entry.hit ? 1 : 0, chevronCount: entry.chevrons.length,
        anchorLabels: entry.presentation.anchors.map(anchor => anchor.label)
    })),
    routeBadgeCount: _badgeRenderer?.count() ?? 0
});

/** Removes one complete registry owner including tooltips and listeners. */
const removeSegmentEntry = entry => {
    entry.hit?.unbindTooltip();
    entry.hit?.off();
    entry.line.unbindTooltip();
    entry.line.off();
    entry.group.clearLayers();
    entry.group.remove();
};

/** Replaces projected chevrons after selection or zoom changes. */
const renderSegmentDecorations = (map, entry) => {
    entry.chevrons.forEach(layer => entry.group.removeLayer(layer));
    entry.chevrons = [];
    if (!entry.visible || entry.orientation === 'ambiguous') return;
    const projected = entry.coords.map(([latitude, longitude]) => {
        const point = map.latLngToLayerPoint([latitude, longitude]);
        return [point.x, point.y];
    });
    entry.chevrons = placeViewerChevrons(projected, entry.active).map(cue => {
        const points = projectChevronArm(cue, entry.active).map(point => map.layerPointToLatLng(point));
        return L.polyline(points, {color: '#852D10', weight: entry.active ? 3 : 2, opacity: entry.active ? 1 : 0.72,
            interactive: false, renderer: location.search.includes('print=1') ? canvasRenderer : undefined}).addTo(entry.group);
    });
};

/** Replaces the active-only badge channel with separate decorative L.Icon layers. */
const renderActiveBadges = (map, entry) => {
    _badgeRenderer?.render(entry.presentation.badges);
};

/** Waits for the current production badge elements and rejects decode/load failures. */
export const waitForCurrentBadgeImages = () => _badgeRenderer?.waitForCurrent() ?? Promise.resolve(0);

/** Reprojects cues deterministically while panning leaves their count unchanged. */
export const refreshSegmentPresentation = map => {
    Object.values(_segments).forEach(entry => renderSegmentDecorations(map, entry));
    const active = _activeSegmentId ? _segments[_activeSegmentId] : null;
    if (active && active.orientation !== 'ambiguous') renderActiveBadges(map, active);
};

/** Removes every Segment-owned layer and global registry entry. */
export const disposeSegmentPresentation = () => {
    Object.values(_segments).forEach(removeSegmentEntry);
    Object.keys(_segments).forEach(id => delete _segments[id]);
    _badgeRenderer?.dispose();
    _badgeRenderer = null;
    _activeSegmentId = null;
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

/** Returns drawable Segment coordinates only when authoritative route WKT supplies a line. */
export const segmentRouteCoords = routeWkt => {
    const coords = wktToCoords(routeWkt);
    return coords.length >= 2 ? coords : [];
};

/** Adds a Segment layer only from resolver-approved route WKT. */
export const addSegmentFromRouteWkt = (map, sid, routeWkt, label = null, opts = {}) => {
    const coords = segmentRouteCoords(routeWkt);
    if (coords.length === 0) return null;
    return addSegment(map, sid, coords, label, opts);
};
