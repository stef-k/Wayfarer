/* -----------------------------------------------------------
 *  Trip Viewer – read-only
 * -----------------------------------------------------------
 *  • region / place PNG markers
 *  • segment polylines  + visibility
 *  • sliding legend + sliding “details” pane
 *  • Google-Maps & Wikipedia helpers
 *  • auto-centering that respects legend width
 */

import {
    initLeaflet,
    addRegionMarker, addPlaceMarker, addSegment,
    setRegionVisible, setSegmentVisible,
    wktToCoords, getPlaceMarker
} from './tripViewerHelpers.js';

const $ = (sel, el = document) => el.querySelector(sel);
const $$ = (sel, el = document) => [...el.querySelectorAll(sel)];
let currentMarker = null;

// --- Permalink Support --- 
/**
 * Read a numeric query-param or return NaN if missing
 * @param {string} key  e.g. "lat", "lon", "zoom"
 * @returns {number}
 */
const getUrlParam = key => {
    const params = new URLSearchParams(window.location.search);
    const raw = params.get(key);
    if (raw === null || raw.trim() === '') return NaN;   // no key or blank => fallback
    const n = Number(raw);
    return Number.isFinite(n) ? n : NaN;                 // non-numeric => fallback
};

/**
 * Decide initial [lat, lon, zoom] by
 * preferring ?lat/?lon/?zoom over data-* defaults
 * @returns {[number, number, number]}
 */
const resolveInitialView = () => {
    const root = document.getElementById('trip-view');
    const defLat = Number(root.dataset.tripLat);
    const defLon = Number(root.dataset.tripLon);
    const defZoom = Number(root.dataset.tripZoom) || 6;

    const urlLat = getUrlParam('lat');
    const urlLon = getUrlParam('lon');
    const urlZoom = getUrlParam('zoom');

    return [
        Number.isFinite(urlLat) ? urlLat : defLat,
        Number.isFinite(urlLon) ? urlLon : defLon,
        Number.isFinite(urlZoom) ? urlZoom : defZoom
    ];
};
/**
 * Returns the stored Trip lat, lon and zoom
 * @returns {(number|number)[]}
 */
const getDefaultView = () => {
    const root = document.getElementById('trip-view');
    return [
        Number(root.dataset.tripLat),
        Number(root.dataset.tripLon),
        Number(root.dataset.tripZoom) || 7
    ];
};
/* ─── Google Maps + Wikipedia helpers ────────────────────── */
const gmLink = addr =>
    `<a href="https://www.google.com/maps/search/?api=1&query=${encodeURIComponent(addr)}"
      target="_blank" class="btn btn-outline-primary btn-sm"
      title="View in Google Maps">
      <i class="bi bi-globe-europe-africa"></i> Maps</a>`;

const wikiLink = (lat, lon) =>
    `<a href="#" class="btn btn-outline-primary btn-sm wikipedia-link"
      data-lat="${lat}" data-lon="${lon}">
      <i class="bi bi-wikipedia"></i> Wiki</a>`;

const initWikiPopovers = root => {
    root.querySelectorAll('.wikipedia-link').forEach(a => {
        tippy(a, {
            appendTo: () => document.body,
            placement: 'right',
            interactive: true,
            allowHTML: true,
            hideOnClick: false,
            content: 'Loading…',
            onShow: async tip => {
                if (tip._loaded) return;          /* only once */
                tip._loaded = true;
                try {
                    const {lat, lon} = a.dataset;

                    /* 1) GeoSearch */
                    const geoURL = `https://en.wikipedia.org/w/api.php?` + new URLSearchParams({
                        action: 'query', list: 'geosearch', gscoord: `${lat}|${lon}`,
                        gsradius: 100, gslimit: 1, format: 'json', origin: '*'
                    });
                    const first = (await (await fetch(geoURL)).json()).query.geosearch[0];
                    if (!first) {
                        tip.setContent('<em>No nearby article.</em>');
                        return;
                    }

                    /* 2) Summary */
                    const sumURL = `https://en.wikipedia.org/api/rest_v1/page/summary/${encodeURIComponent(first.title)}`;
                    const j = await (await fetch(sumURL)).json();

                    tip.setContent(`<div style="max-width:240px">
                            <strong>${j.title}</strong><p>${j.extract}</p>
                            <a href="${j.content_urls.desktop.page}" target="_blank">Read&nbsp;more&nbsp;»</a>
                          </div>`);
                } catch {
                    tip.setContent('<em>Could not load article.</em>');
                }
            }
        });
    });
};

/* ========================================================= */
const init = () => {

    const isPrint = location.search.includes('print=1');
    
    /* ────────── map bootstrap ────────── */
    let [lat, lon, zoom] = resolveInitialView();
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

    const map = initLeaflet([lat, lon], zoom);
    // if user clicks anywhere on map except from markers remove current marker highlight
    map.on('click', e => {
        removeHighlightMarker();
    });

    // update permalink URL
    map.on('moveend zoomend', () => {
        const ctr = map.getCenter();
        const z = map.getZoom();
        const params = new URLSearchParams(window.location.search);
        params.set('lat', ctr.lat.toFixed(6));
        params.set('lon', ctr.lng.toFixed(6));
        params.set('zoom', z);
        history.replaceState(null, '', `${window.location.pathname}?${params}`);
    });

    // recenter map
    recenterMap.addEventListener('click', e => {
        [lat, lon, zoom] = getDefaultView();
        map.flyTo([lat, lon], zoom, {
            animate: true,
            duration: 1
        })
        // offset based on sidebar
        map.once('moveend', () => {
            // `collapsed` is your existing flag from setCollapsed()
            // dir = 1 if sidebar visible, -1 if hidden
            applyCentreOffset(collapsed ? -1 : 1, true);
        });
    });

    /* helper that returns the legend’s current width */
    const legend = $('#sidebar-primary');
    const legendW = () => legend.offsetWidth || 0;

    /* centre correction: move map by ± legendW / 2 */
    const applyCentreOffset = (dir, animate = false) => {
        // not on print
        if (isPrint) return;
        const dx = (legendW() / 2) * dir;
        if (dx) map.panBy([-dx, 0], {animate, duration: 0.4});   //  ← negate
    };

    /* initial correction (legend starts visible but not in print) */
   if (!isPrint) {
       applyCentreOffset(+1, true);
   }

    /* ────────── regions & places ────────── */
    $$('.accordion-item').forEach(ai => {
        const {regionId, regionName, centerLat, centerLon} = ai.dataset;
        if (centerLat && centerLon)
            addRegionMarker(map, regionId, [+centerLat, +centerLon], regionName);
    });

    $$('.place-list-item').forEach(li => {
        const d = li.dataset;
        if (d.placeLat && d.placeLon)
            addPlaceMarker(map, d.placeId, [+d.placeLat, +d.placeLon], {
                name: d.placeName, icon: d.placeIcon, color: d.placeColor, region: d.regionId
            });
    });

    /* ────────── segments ────────── */
    $$('.segment-list-item').forEach(li => {
        const d = li.dataset;
        let coords = [];
        if (d.routeWkt) coords = wktToCoords(d.routeWkt);
        if (coords.length < 2 && d.fromLat && d.toLat)
            coords = [[+d.fromLat, +d.fromLon], [+d.toLat, +d.toLon]];
        if (coords.length >= 2) {
            const label = `From ${d.fromPlaceName} to ${d.toPlaceName}, ${d.estimatedDistance} km by ${d.transportMode} in ${d.estimatedDuration}`;
            addSegment(map, d.segmentId, coords, label);
        }
    });

    /* ───── visibility toggles ───── */
    $$('.segment-toggle').forEach(cb => cb.addEventListener('change', e => {
        const sid = e.target.closest('.segment-list-item').dataset.segmentId;
        setSegmentVisible(map, sid, e.target.checked);
    }));

    /* region check-boxes – stop accordion toggle */
    $$('.region-toggle').forEach(cb => {
        cb.addEventListener('click', e => e.stopPropagation());
        cb.addEventListener('change', e => {
            e.stopPropagation();
            const rid = e.target.closest('.accordion-item').dataset.regionId;
            setRegionVisible(map, rid, e.target.checked);
        });
    });

    /* ────────── legend collapse ────────── */
    const hideBtn = $('#btn-collapse-sidebar');

    const showBtn = Object.assign(document.createElement('button'), {
        id: 'btn-show-sidebar',
        className: 'btn btn-light border fw-semibold shadow-lg',
        textContent: 'MAP LEGEND'
    });
    document.body.appendChild(showBtn);
    showBtn.style.display = 'none';

    const DELAY = 600;
    let timer = null;
    let collapsed = false;

    const setCollapsed = hide => {
        if (collapsed === hide) return;      // no double-shifting
        collapsed = hide;

        if (timer) {
            clearTimeout(timer);
            timer = null;
        }

        legend.dataset.collapsed = hide ? 'true' : 'false';

        /* shift map *after* Leaflet recalculates size */
        setTimeout(() => {
            applyCentreOffset(hide ? -1 : +1, true);
            map.invalidateSize();
        }, DELAY + 20);

        if (hide) {
            timer = setTimeout(() => showBtn.style.display = 'block', DELAY);
        } else {
            showBtn.style.display = 'none';
        }
    };

    hideBtn.addEventListener('click', () => setCollapsed(true));
    showBtn.addEventListener('click', () => setCollapsed(false));

    /* ────────── details pane ────────── */
    const pane = $('#sidebar-secondary');

    const detailsHtml = li => {
        const d = li.dataset;
        const firstImg = (d.placeNotes || '').match(/<img[^>]+src="([^"]+)"/i)?.[1] || '';
        const iconUrl = `/icons/wayfarer-map-icons/dist/png/marker/${d.placeColor}/${d.placeIcon}.png`;
        const notesDiv = li.querySelector('.place-notes');
        const notesHtml = notesDiv?.innerHTML || '';

        return `
  <div class="d-flex align-items-center gap-2 border-bottom px-2 py-2 text-bg-light ">
    <button class="btn btn-outline-secondary btn-sm btn-back" title="Back">
      <i class="bi bi-arrow-left"></i></button>
          <img src="${iconUrl}" width="24" height="38" alt="">
      <div><strong>${d.placeName}</strong><br>
           <small class="text-muted">${d.regionName}</small></div>
  </div>

  <div class="p-3">
<!--    ${firstImg ? `<img src="${firstImg}" class="img-fluid w-100 mb-2" alt="">` : ''}-->

    <p class="mb-1"><strong>Lat:</strong> ${(+d.placeLat).toFixed(5)}
       &nbsp;<strong>Lon:</strong> ${(+d.placeLon).toFixed(5)}</p>
    ${d.placeAddress ? `<p class="mb-1"><strong>Address:</strong> ${d.placeAddress}</p>` : ''}

    ${notesHtml ? `
       <div class="border py-2 px-1 mt-3 rounded overflow-auto trip-notes" >
         ${notesHtml}</div>` : ''}

    <div class="mt-3 d-flex flex-wrap gap-2">
      ${gmLink(d.placeAddress || `${d.placeLat},${d.placeLon}`)}
      ${wikiLink(d.placeLat, d.placeLon)}
    </div>
  </div>`;
    };

    /* open details */
    window.wayfarer.openPlaceDetails = pid => {
        const li = $(`.place-list-item[data-place-id="${pid}"]`);
        if (!li) return;
        pane.innerHTML = detailsHtml(li);
        pane.classList.add('open');
        highlightMarker(pid);
        initWikiPopovers(pane);
    };

    /* close details */
    document.addEventListener('click', e => {
        if (e.target.closest('.btn-back')) {
            pane.classList.remove('open');
            removeHighlightMarker();      // un-highlight only on close
        }
    });

    /* list-click mirrors marker-click */
    $$('.place-list-item').forEach(li => {
        li.addEventListener('click', () => {
            window.wayfarer.openPlaceDetails(li.dataset.placeId);
        });

    });
};

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
} else {                       // DOMContentLoaded has already fired
    init();                    // run immediately (print mode / Puppeteer)
}