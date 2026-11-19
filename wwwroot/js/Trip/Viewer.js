/* ----------------------\n    initLegendSearch();\n-------------------------------------
 *  Trip Viewer – read-only
 * -----------------------------------------------------------
 *  • region / place PNG markers
 *  • segment polylines  + visibility
 */

import {
    initLeaflet,
    addRegionMarker,
    addPlaceMarker,
    addSegmentFromRouteWkt,
    addAreaPolygon,
    setRegionVisible,
    setSegmentVisible,
    getPlaceMarker,
    canvasRenderer
} from './tripViewerHelpers.js';

import { resolveViewerCamera, writeViewerCamera } from './viewerCamera.js';
import { initViewerExports } from './viewerExports.js';
import { message, number } from '../localization/messages.js';
import { createViewerSidebar } from './viewerSidebar.js';
import {createViewerSegmentPresentationController} from './viewerSegmentPresentationController.js'; import {encodeFeatureText, featurePrecisionNotice} from '../util/feature-metadata.js';
import {
    generateWikipediaLinkHtml,
    initWikipediaPopovers,
} from '../util/wikipedia-utils.js';
import { initShareProgressToggle } from '../Areas/User/Trip/shareProgressToggle.js';

const $ = (sel, el = document) => el.querySelector(sel);
const $$ = (sel, el = document) => [...el.querySelectorAll(sel)];
let currentMarker = null;
const _areas = {};

// Sidebar legend search (regions, places, areas, segments)
const initLegendSearch = () => {
  const input = document.getElementById('legend-search');
  if (!input) return;
  const norm = s => (s||'').toString().normalize('NFD').replace(/\p{Diacritic}/gu,'').toLowerCase();
  const collect = () => {
    const items = [];
    // regions
    $$('#regions-accordion .accordion-item').forEach(r => {
      const id = r.id?.replace(/^region-/, '') || r.dataset.regionId;
      const name = r.dataset.regionName || r.querySelector('.accordion-button')?.textContent || '';
      items.push({ type:'region', id, label:name, el:r, collapseId:`reg-body-${id}` });
      // places
      $$('.place-list-item', r).forEach(p => items.push({ type:'place', id:p.dataset.placeId, regionId:id, label:p.dataset.placeName || p.textContent || '', el:p, collapseId:`reg-body-${id}` }));
      // areas
      $$('.area-list-item', r).forEach(a => items.push({ type:'area', id:a.dataset.areaId, regionId:id, label:$('.area-name', a)?.textContent || a.textContent || '', el:a, collapseId:`reg-body-${id}` }));
    });
    // segments
    $$('.segment-list-item').forEach(s => items.push({ type:'segment', id:s.dataset.segmentId, label:s.dataset.segmentName || s.textContent || '', el:s }));
    items.forEach(it => it.normal = norm(it.label));
    return items;
  };
  let index = collect();
  const refreshIndex = () => { index = collect(); };
  const clearHighlights = () => $$('.search-hit').forEach(e => e.classList.remove('search-hit'));
  const ensureExpanded = id => {
    if (!id) return; const el = document.getElementById(id);
    if (el && !el.classList.contains('show')) { try { bootstrap.Collapse.getOrCreateInstance(el, {toggle:false}).show(); } catch {} }
  };
  const doSearch = q => {
    clearHighlights(); if (!q) return; refreshIndex();
    const tokens = norm(q).split(/\s+/).filter(Boolean);
    const matches = index.filter(it => tokens.every(t => it.normal.includes(t)));
    matches.forEach(m => m.el.classList.add('search-hit'));
    if (matches.length) { const first = matches[0]; ensureExpanded(first.collapseId); requestAnimationFrame(()=> first.el.scrollIntoView({behavior:'smooth', block:'center', inline:'nearest'})); }
  };
  input.addEventListener('input', () => doSearch(input.value));
};

// Helper to check if notesHtml is "visibly empty"
// Returns true only if there's no text AND no media elements (images, videos, etc.)
const isHtmlEmpty = html => {
    if (!html) return true;
    const div = document.createElement('div');
    div.innerHTML = html;
    const text = div.textContent?.trim() ?? '';
    const hasMedia = div.querySelector('img, video, audio, iframe') !== null;
    return text === '' && !hasMedia;
};


/**
 * Returns the stored Trip lat, lon and zoom
 * @returns {(number|number)[]}
 */
const getDefaultView = () => {
    const root = document.getElementById('trip-view');
    return [Number(root.dataset.tripLat), Number(root.dataset.tripLon), Number(root.dataset.tripZoom) || 7];
};
/**
 * Build a Google Maps search query combining address and coordinates
 * for better disambiguation of short/common street names.
 *
 * - If address and coords exist: "<address> (<lat>,<lon>)"
 * - Else if coords exist:        "<lat>,<lon>"
 * - Else:                        "<address>"
 */
const gmQuery = (addr, lat, lon) => {
    const hasCoords = Number.isFinite(+lat) && Number.isFinite(+lon);
    if (addr && hasCoords) return `${addr} (${(+lat).toFixed(6)},${(+lon).toFixed(6)})`;
    if (hasCoords) return `${(+lat).toFixed(6)},${(+lon).toFixed(6)}`;
    return addr || '';
};

/**
 * Create an <a> element that opens Google Maps with the constructed query.
 * @param {string} addr
 * @param {number|string} lat
 * @param {number|string} lon
 */
const gmLinkWithCoords = (addr, lat, lon) => `<a href="https://www.google.com/maps/search/?api=1&query=${encodeURIComponent(gmQuery(addr, lat, lon))}"
      target="_blank" class="btn btn-outline-primary btn-sm"
      title="${encodeFeatureText(message('Viewer.OpenGoogleMaps', 'View in Google Maps'))}">
      <i class="bi bi-globe-europe-africa"></i> ${encodeFeatureText(message('Viewer.GoogleMaps', 'Maps'))}</a>`;
/* ========================================================= */
const init = () => {
    initLegendSearch();

    const root = document.getElementById('trip-view');
    const isEmbed        = root.dataset.embed === 'true';
    const fullscreenUrl  = root.dataset.fullscreenUrl;
    const params = new URLSearchParams(location.search);
    const isPrint = params.get('print') === '1';

    /* ────────── map bootstrap ────────── */
    const camera = resolveViewerCamera(location.search, getDefaultView());
    let [lat, lon, zoom] = camera.view;
    const recenterMap = $('#btn-trip-center');

    const highlightMarker = pid => {
        /* remove highlight from the previously selected marker */
        removeHighlightMarker();
        /* add highlight to the newly selected one */
        const m = getPlaceMarker(pid);
        if (m) {
            m.getElement()?.classList.add('selected-marker');
            currentMarker = m;
        }
    };

    const removeHighlightMarker = () => {
        if (currentMarker) {
            currentMarker.getElement()?.classList.remove('selected-marker');
            currentMarker = null;
        }
    }

    if (!Number.isFinite(lat) || !Number.isFinite(lon)) {
        const f = $('.accordion-item[data-center-lat]');
        lat = +f?.dataset.centerLat;
        lon = +f?.dataset.centerLon;
    }
    if (!Number.isFinite(lat) || !Number.isFinite(lon)) {
        lat = 20;
        lon = 0;
        zoom = 2;
    }

    const map = initLeaflet([lat, lon], zoom, isEmbed ? fullscreenUrl : null);
    const sidebar = createViewerSidebar(root, map, { isEmbed, isPrint });
    window.addEventListener('pagehide', event => { if (!event.persisted) sidebar.dispose(); });
    const segmentPresentation = createViewerSegmentPresentationController(map, root, {
        isPrint,
        preserveCamera: camera.complete,
        fitOptions: sidebar.fitOptions,
        reducedMotion: () => window.matchMedia('(prefers-reduced-motion: reduce)').matches
    });
    // if user clicks anywhere on map except from markers remove current marker highlight
    map.on('click', e => {
        removeHighlightMarker();
    });

    // update permalink URL
    map.on('moveend zoomend', () => writeViewerCamera(location.href, history, map.getCenter(), map.getZoom()));

    // recenter map
    recenterMap.addEventListener('click', e => {
        [lat, lon, zoom] = getDefaultView();
        map.flyTo(sidebar.centerFor([lat, lon], zoom), zoom, {
            animate: !window.matchMedia('(prefers-reduced-motion: reduce)').matches, duration: 1
        });
    });

    /* ────────── regions & places ────────── */
    $$('.accordion-item').forEach(ai => {
        const {regionId, regionName, centerLat, centerLon} = ai.dataset;
        if (centerLat && centerLon) addRegionMarker(map, regionId, [+centerLat, +centerLon], regionName);
    });

    $$('.place-list-item').forEach(li => {
        const d = li.dataset;
        // Get notes from hidden div
        const notesEl = li.querySelector('.place-notes');
        const notes = notesEl?.innerHTML || '';

        if (d.placeLat && d.placeLon) addPlaceMarker(map, d.placeId, [+d.placeLat, +d.placeLon], {
            name: d.placeName,
            icon: d.placeIcon,
            color: d.placeColor,
            region: d.regionId,
            regionName: d.regionName,
            address: d.placeAddress,
            resolvedFeatureName: d.placeResolvedFeatureName, resolvedFeatureType: d.placeResolvedFeatureType,
            notes,
            visitCount: parseInt(d.placeVisitCount, 10) || 0
        });
    });

    /* ────────── areas ────────── */
    // 1) Draw & store all area polygons with rich popups
    document.querySelectorAll('.area-list-item').forEach(li => {
        const areaId = li.dataset.areaId;
        const geom = JSON.parse(li.dataset.areaGeom || 'null');
        const fill = li.dataset.areaFill || '#3388ff';
        if (!geom?.coordinates) return;

        const coords = geom.coordinates[0].map(([lon, lat]) => [lat, lon]);
        const name = li.dataset.areaName || li.querySelector('.area-name')?.textContent.trim();
        const notes = li.dataset.areaNotes || '';

        const poly = addAreaPolygon(map, areaId, coords, {
            name,
            notes,
            fill
        });

        if (poly) _areas[areaId] = poly;
    });

    // 2) Wire each “.area-toggle” checkbox once
    document.querySelectorAll('.area-toggle').forEach(cb => {
        cb.addEventListener('change', e => {
            const id = e.target.dataset.areaId;
            const poly = _areas[id];
            if (!poly) return;
            if (e.target.checked) map.addLayer(poly);
            else map.removeLayer(poly);
        });
    });

    /* ────────── segments ────────── */
    $$('.segment-list-item').forEach(li => {
        const d = li.dataset;
        const distance = d.estimatedDistance ? message('Viewer.Kilometres', '{0} km', number(Number(d.estimatedDistance))) : '';
        const label = message('Viewer.SegmentLabel', 'From {0} to {1}, {2} by {3} in {4}',
            d.fromPlaceName, d.toPlaceName, distance, d.transportMode, d.estimatedDuration);
        addSegmentFromRouteWkt(map, d.segmentId, d.routeWkt, label, {
            anchors: JSON.parse(d.segmentAnchors || '[]'),
            orientation: d.routeOrientation || 'ambiguous',
            fromPlace: d.fromPlaceName,
            toPlace: d.toPlaceName,
            fromRegion: d.fromPlaceregionName,
            toRegion: d.toPlaceregionName,
            mode: d.transportMode,
            distance: distance || null,
            duration: d.estimatedDuration,
            notes: d.segmentNotes
        });
    });

    /* ───── visibility toggles ───── */
    $$('.segment-toggle').forEach(cb => cb.addEventListener('change', e => {
        const sid = e.target.closest('.segment-list-item').dataset.segmentId;
        setSegmentVisible(map, sid, e.target.checked);
        if (!e.target.checked && root.dataset.activeSegmentId === sid) segmentPresentation.select(null, false);
    }));

    const requestedSegmentId = params.get('seg');
    void segmentPresentation.initialize(requestedSegmentId);

    /* region check-boxes – stop accordion toggle */
    $$('.region-toggle').forEach(cb => {
        cb.addEventListener('click', e => e.stopPropagation());
        cb.addEventListener('change', e => {
            e.stopPropagation();
            const rid = e.target.closest('.accordion-item').dataset.regionId;
            setRegionVisible(map, rid, e.target.checked);
        });
    });

    /* Details content shares the layout owner's live width and accessible controls. */
    const pane = $('#sidebar-secondary');
    const detailsHtml = li => {
        const d = li.dataset;
        const iconUrl = `/icons/wayfarer-map-icons/dist/png/marker/${d.placeColor}/${d.placeIcon}.png`;
        const notesDiv = li.querySelector('.place-notes');
        const notesHtml = notesDiv?.innerHTML || '';

        return `
  <div class="d-flex align-items-center gap-2 border-bottom px-2 py-2 bg-body-tertiary">
    <button class="btn btn-outline-secondary btn-sm btn-back" title="${encodeFeatureText(message('Viewer.Back', 'Back'))}" aria-label="${encodeFeatureText(message('Viewer.BackToLegend', 'Back to legend'))}">
      <i class="bi bi-arrow-left"></i></button>
          <img src="${encodeFeatureText(iconUrl)}" width="24" height="38" alt="">
      <div><strong>${encodeFeatureText(d.placeName)}</strong><br>
           <small class="text-muted">${encodeFeatureText(d.regionName)}</small></div>
  </div>

  <div class="p-3">

    <p class="mb-1"><strong>${encodeFeatureText(message('Shell.LatitudeShort', 'Lat:'))}</strong> ${number(+d.placeLat, { maximumFractionDigits: 5 })}
       &nbsp;<strong>${encodeFeatureText(message('Shell.LongitudeShort', 'Lon:'))}</strong> ${number(+d.placeLon, { maximumFractionDigits: 5 })}</p>
    ${d.placeAddress ? `<p class="mb-1"><strong>${encodeFeatureText(message('Shell.AddressLabel', 'Address:'))}</strong> ${encodeFeatureText(d.placeAddress)}</p>` : ''}
    ${d.placeResolvedFeatureName ? `<p class="mb-1">${encodeFeatureText(message('Viewer.DetectedPlace', 'Detected place: {0}', d.placeResolvedFeatureName))}</p>` : ''}${featurePrecisionNotice(d.placeResolvedFeatureType) ? `<p class="mb-1" role="note">${featurePrecisionNotice(d.placeResolvedFeatureType)}</p>` : ''}

    ${!isHtmlEmpty(notesHtml) ? `
       <div class="border py-2 px-1 mt-3 rounded overflow-auto trip-notes rich-notes-content" >
         ${notesHtml}</div>` : ''}

    <div class="mt-3 d-flex flex-wrap gap-2">
      ${gmLinkWithCoords(d.placeAddress, d.placeLat, d.placeLon)}
      ${generateWikipediaLinkHtml(+d.placeLat, +d.placeLon, { query: d.placeName })}
    </div>
  </div>`;
    };

    /* open details */
    window.wayfarer.openPlaceDetails = pid => {
        const li = $(`.place-list-item[data-place-id="${pid}"]`);
        if (!li) return;
        // The hidden Razor notes already contain display proxies before either DOM insertion.
        sidebar.openDetails(detailsHtml(li));
        highlightMarker(pid);
        const m = getPlaceMarker(pid);
        if (m) {
            const tgt = m.getLatLng();
            // pick a zoom level that is at least 12 or keeps the current one if closer
            const z = Math.max(map.getZoom(), 12);
            map.flyTo(sidebar.centerFor(tgt, z), z, {
                animate: !window.matchMedia('(prefers-reduced-motion: reduce)').matches, duration: 1.2
            });
        }
        initWikipediaPopovers(pane);
    };

    /* close details */
    document.addEventListener('click', e => {
        if (e.target.closest('.btn-back')) {
            sidebar.closeDetails();
            removeHighlightMarker();      // un-highlight only on close
        }
    });

    /* list-click mirrors marker-click */
    $$('.place-list-item').forEach(li => {
        li.addEventListener('click', () => {
            window.wayfarer.openPlaceDetails(li.dataset.placeId);
        });

    });

    // Journey Place controls reuse the canonical Place details and marker selection path.
    $$('.segment-journey-place').forEach(button => {
        button.addEventListener('click', event => {
            event.stopPropagation();
            window.wayfarer.openPlaceDetails(button.dataset.placeId);
            highlightMarker(button.dataset.placeId);
        });
    });

    /* Row, button and route-hit selection share the same bounded navigation owner. */
    $$('.segment-list-item[data-route-wkt]').forEach(li => {
        li.addEventListener('click', e => {
            // ignore clicks coming from the visibility checkbox
            if (e.target.closest('.segment-toggle, .segment-journey-place')) return;

            segmentPresentation.select(li.dataset.segmentId);
        });
        li.querySelector('.segment-selection-button')?.addEventListener('click', e => {
            e.stopPropagation();
            segmentPresentation.select(li.dataset.segmentId);
        });
    });

    initViewerExports();

    // Handle copy-url clicks for Share button
    document.addEventListener('click', async (e) => {
        const el = e.target.closest('a.copy-url');
        if (!el) return;

        e.preventDefault();

        const url = el.dataset.url;
        try {
            // URL is already absolute from server-side Url.Action with scheme
            await navigator.clipboard.writeText(url);
            // Use toast instead of alert to avoid viewport jumps
            if (wayfarer.showToast) {
                wayfarer.showToast('success', message('Shell.UrlCopied', 'URL copied to clipboard!'));
            } else {
                wayfarer.showAlert('success', message('Shell.UrlCopied', 'URL copied to clipboard!'));
            }
        } catch (err) {
            console.error('Failed to copy URL:', err);
            if (wayfarer.showToast) {
                wayfarer.showToast('danger', message('Shell.UrlCopyFailed', 'Failed to copy URL.'));
            } else {
                wayfarer.showAlert('danger', message('Shell.UrlCopyFailed', 'Failed to copy URL.'));
            }
        }
    });

    /* ----- auto-focus place from URL parameter ----- */
    const focusPlaceId = params.get('placeId');
    if (focusPlaceId) {
        const placeEl = document.querySelector(`.place-list-item[data-place-id="${focusPlaceId}"]`);
        if (placeEl) {
            const regionId = placeEl.dataset.regionId;
            if (regionId) {
                // Expand the region accordion
                const collapseEl = document.getElementById(`reg-body-${regionId}`);
                if (collapseEl && !collapseEl.classList.contains('show')) {
                    try {
                        bootstrap.Collapse.getOrCreateInstance(collapseEl, { toggle: false }).show();
                    } catch {}
                }
                // Wait for accordion animation, then highlight the place and open details
                setTimeout(() => {
                    // Highlight marker on map
                    highlightMarker(focusPlaceId);
                    // Open the place details pane
                    if (window.wayfarer?.openPlaceDetails) {
                        window.wayfarer.openPlaceDetails(focusPlaceId);
                    }
                }, 350);
            }
        }
    }

    /* ----- share progress toggle (owner only) ----- */
    initShareProgressToggle();

};

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
} else {                       // DOMContentLoaded has already fired
    init();                    // run immediately (print mode / Puppeteer)
}


