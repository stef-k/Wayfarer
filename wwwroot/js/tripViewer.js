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
    addRegionMarker,
    addPlaceMarker,
    addSegment,
    setRegionVisible,
    setSegmentVisible,
    wktToCoords,
    getPlaceMarker,
    getSegmentPolyline,
    canvasRenderer
} from './tripViewerHelpers.js';

const $ = (sel, el = document) => el.querySelector(sel);
const $$ = (sel, el = document) => [...el.querySelectorAll(sel)];
let currentMarker = null;
const _areas = {};

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

// Helper to check if notesHtml is "visibly empty"
const isHtmlEmpty = html => {
    if (!html) return true;
    const div = document.createElement('div');
    div.innerHTML = html;
    const text = div.textContent?.trim() ?? '';
    return text === '';
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

    return [Number.isFinite(urlLat) ? urlLat : defLat, Number.isFinite(urlLon) ? urlLon : defLon, Number.isFinite(urlZoom) ? urlZoom : defZoom];
};
/**
 * Returns the stored Trip lat, lon and zoom
 * @returns {(number|number)[]}
 */
const getDefaultView = () => {
    const root = document.getElementById('trip-view');
    return [Number(root.dataset.tripLat), Number(root.dataset.tripLon), Number(root.dataset.tripZoom) || 7];
};
/* ─── Google Maps + Wikipedia helpers ────────────────────── */
const gmLink = addr => `<a href="https://www.google.com/maps/search/?api=1&query=${encodeURIComponent(addr)}"
      target="_blank" class="btn btn-outline-primary btn-sm"
      title="View in Google Maps">
      <i class="bi bi-globe-europe-africa"></i> Maps</a>`;

const wikiLink = (lat, lon) => `<a href="#" class="btn btn-outline-primary btn-sm wikipedia-link"
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
                        action: 'query',
                        list: 'geosearch',
                        gscoord: `${lat}|${lon}`,
                        gsradius: 100,
                        gslimit: 1,
                        format: 'json',
                        origin: '*'
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

    const root = document.getElementById('trip-view');
    const isEmbed        = root.dataset.embed === 'true';
    const fullscreenUrl  = root.dataset.fullscreenUrl;
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
            animate: true, duration: 1
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

    /**
     * Shift the map horizontally by ±½ legend width.
     * @param {+1|-1} dir      +1 → legend *visible*, -1 → legend *hidden*
     * @param {boolean} [animate=false]
     * @param {number|null} [baseW=null]   width to use instead of live offsetWidth
     */
    const applyCentreOffset = (dir, animate = false, baseW = null) => {
        if (isPrint) return;
        const w  = baseW ?? legendW();             // fall back to live width
        const dx = (w / 2) * dir;
        if (dx) map.panBy([-dx, 0], { animate, duration: 0.4 });
    };

    /* ────────── regions & places ────────── */
    $$('.accordion-item').forEach(ai => {
        const {regionId, regionName, centerLat, centerLon} = ai.dataset;
        if (centerLat && centerLon) addRegionMarker(map, regionId, [+centerLat, +centerLon], regionName);
    });

    $$('.place-list-item').forEach(li => {
        const d = li.dataset;
        if (d.placeLat && d.placeLon) addPlaceMarker(map, d.placeId, [+d.placeLat, +d.placeLon], {
            name: d.placeName, icon: d.placeIcon, color: d.placeColor, region: d.regionId
        });
    });

    /* ────────── areas ────────── */
    // 1) Draw & store all area polygons
    document.querySelectorAll('.area-list-item').forEach(li => {
        const areaId = li.dataset.areaId;
        const geom = JSON.parse(li.dataset.areaGeom || 'null');
        const fill = li.dataset.areaFill || '#3388ff';
        if (!geom?.coordinates) return;

        const coords = geom.coordinates[0].map(([lon, lat]) => [lat, lon]);
        const poly = L.polygon(coords, {
            color: fill,
            fillColor: fill,
            weight: 1,
            opacity: 0.7,
            fillOpacity: 0.1,
            renderer: isPrint ? canvasRenderer : undefined
        }).addTo(map);

        const name = li.querySelector('.area-name')?.textContent.trim();
        if (name) poly.bindTooltip(name, {direction: 'right'});

        _areas[areaId] = poly;
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
        let coords = [];
        if (d.routeWkt) coords = wktToCoords(d.routeWkt);
        if (coords.length < 2 && d.fromLat && d.toLat) coords = [[+d.fromLat, +d.fromLon], [+d.toLat, +d.toLon]];
        if (coords.length >= 2) {
            const label = `From ${d.fromPlaceName} to ${d.toPlaceName}, ${d.estimatedDistance} km by ${d.transportMode} in ${d.estimatedDuration}`;
            addSegment(map, d.segmentId, coords, label);
        }
    });

    const params = new URLSearchParams(window.location.search);
    const onlySeg = params.get('seg');
    if (onlySeg) {
        // find the matching data (you may have serialized WKT into a data-attr)
        const li = document.querySelector(`.segment-list-item[data-segment-id="${onlySeg}"]`);
        let coords = [];
        if (li?.dataset.routeWkt) {
            coords = wktToCoords(li.dataset.routeWkt);
        } else {
            // fallback to from‐to lat/lon
            const {fromLat, fromLon, toLat, toLon} = li.dataset;
            coords = [[+fromLat, +fromLon], [+toLat, +toLon]];
        }
        if (coords.length >= 2) {
            addSegment(map, onlySeg, coords, /* optional label */ '');
        }
    }

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
    /* ----------  FULL-SCREEN button (embed only) ------------------------------ */
    let fsBtn;
    if (isEmbed) {
        fsBtn               = document.createElement('button');
        fsBtn.id            = 'btn-fullscreen';
        fsBtn.className     = 'btn btn-primary btn-sm shadow-lg';
        fsBtn.title         = 'Open full-screen view';
        fsBtn.innerHTML     = '<i class="bi bi-arrows-fullscreen"></i>';
        fsBtn.style.display = 'none';
        fsBtn.addEventListener('click', () => window.open(fullscreenUrl,'_blank'));
        document.body.appendChild(fsBtn);
    }
    const pane = $('#sidebar-secondary');
    
    /**
     * Show the MAP LEGEND / fullscreen buttons only when:
     *   – primary legend is collapsed, and
     *   – details pane (#sidebar-secondary) is NOT open.
     */
    const updateButtonsVisibility = () => {
        const detailsOpen = pane?.classList.contains('open');
        const visible     = collapsed && !detailsOpen;
        showBtn.style.display = visible ? 'block' : 'none';
        if (isEmbed) {
            fsBtn.style.display = visible ? 'block' : 'none';
            if (visible) positionFsBtn();
        }
    };

    const positionFsBtn = () => {
        if (!fsBtn) return;                       // safety
        const r = showBtn.getBoundingClientRect();
        fsBtn.style.left = `${r.left + r.width + 8}px`;
        fsBtn.style.top  = `${r.top}px`;          // just mirrors CSS in case of resize
    };

    positionFsBtn();                            // initial
    window.addEventListener('resize', positionFsBtn);
    const DELAY = 600;
    let timer = null;
    let collapsed = false;

    const setCollapsed =  (hide, skipPan = false) => {
        if (collapsed === hide) return;      // no double-shifting
        const prevW = legendW();       // cache width *before* we change CSS
        
        collapsed = hide;

        if (timer) {
            clearTimeout(timer);
            timer = null;
        }

        legend.dataset.collapsed = hide ? 'true' : 'false';

        /* shift map *after* Leaflet recalculates size */
        setTimeout(() => {
            if (!skipPan) {                         // ⬅️ only when we already had an offset
                applyCentreOffset(hide ? -1 : +1, true, prevW);
            }
            map.invalidateSize();
        }, DELAY + 20);

        timer = setTimeout(updateButtonsVisibility, hide ? DELAY : 0);
    };
    if (isEmbed) setCollapsed(true, true);
    if (!isPrint && !isEmbed) {
        applyCentreOffset(+1, false);
    }
    hideBtn.addEventListener('click', () => setCollapsed(true));
    showBtn.addEventListener('click', () => setCollapsed(false));

    /* ────────── details pane ────────── */
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

    ${!isHtmlEmpty(notesHtml) ? `
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
        pane
            .querySelectorAll('.trip-notes img')
            .forEach(img => {
                const orig = img.src;
                img.setAttribute('data-original', orig);
                img.src = `/Public/ProxyImage?url=${encodeURIComponent(orig)}`;
            });
        pane.classList.add('open');
        updateButtonsVisibility();
        highlightMarker(pid);
        const m = getPlaceMarker(pid);
        if (m) {
            const tgt = m.getLatLng();
            // pick a zoom level that is at least 12 or keeps the current one if closer
            const z = Math.max(map.getZoom(), 12);
            map.flyTo(tgt, z, {animate: true, duration: 1.2});

            // once the movement ends, nudge the map horizontally so the marker
            // stays centred when the legend is visible / hidden
            map.once('moveend', () => {
                applyCentreOffset(collapsed ? -1 : 1, true);
            });
        }
        initWikiPopovers(pane);
    };

    /* close details */
    document.addEventListener('click', e => {
        if (e.target.closest('.btn-back')) {
            pane.classList.remove('open');
            updateButtonsVisibility();
            removeHighlightMarker();      // un-highlight only on close
        }
    });

    /* list-click mirrors marker-click */
    $$('.place-list-item').forEach(li => {
        li.addEventListener('click', () => {
            window.wayfarer.openPlaceDetails(li.dataset.placeId);
        });

    });

    /* ADD – segment click centres map on the whole route */
    $$('.segment-list-item').forEach(li => {
        li.addEventListener('click', e => {
            // ignore clicks coming from the visibility checkbox
            if (e.target.closest('.segment-toggle')) return;

            const sid = li.dataset.segmentId;
            const pl = getSegmentPolyline(sid);
            if (!pl) return;

            const b = pl.getBounds();                 // route bounds
            const padX = collapsed ? 60                 // legend hidden
                : legendW() / 2 + 60; // space for legend + bonus
            map.flyToBounds(b, {
                animate: true, duration: 1.2, padding: [padX, 60]                    // neat margin
            });

            // after movement, nudge map for sidebar width (as for places)
            map.once('moveend', () => {
                applyCentreOffset(collapsed ? -1 : 1, true);
            });
        });
    });

    // --------------------------------------------------------------------
    // Export Buttons – show spinner until navigation starts (cannot detect
    // download completion reliably without a service-worker)
    //
    // Requires: Bootstrap modal (#exportWait) from Viewer.cshtml
    // --------------------------------------------------------------------
    // viewer -- export helpers
    const MAX_SAFE_SIZE = 120 * 1024 * 1024;   // 120 MB  ➜ tweak to taste
    const waitModal = new bootstrap.Modal('#exportWait', {backdrop: 'static'});
    const spinnerTxt = document.querySelector('#exportWait strong');
    const dlFrame = document.getElementById('exportFrame'); // keep the iframe

    function showModal(modal) {
        return new Promise(res => {
            const el = modal._element;
            const alreadyShown = el.classList.contains('show');

            if (alreadyShown) return res();           // nothing to wait for
            el.addEventListener('shown.bs.modal', res, {once: true});
            modal.show();
        });
    }

    function hideModal(modal) {
        return new Promise(res => {
            const el = modal._element;
            const alreadyHidden = !el.classList.contains('show');

            if (alreadyHidden) return res();          // nothing to wait for
            el.addEventListener('hidden.bs.modal', res, {once: true});
            modal.hide();
        });
    }

    //------------------------------------------------------------------
    // (A) fast-path – fetch into memory → Blob → <a download>
    //------------------------------------------------------------------
    async function fetchAndSave(url, fallbackName = 'download') {
        const resp = await fetch(url, {credentials: 'include'});
        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);

        const cd = resp.headers.get('Content-Disposition') ?? '';
        const m = cd.match(/filename\*?=(?:UTF-8'')?["']?([^"';]+)["']?/i);
        const fileName = m ? decodeURIComponent(m[1]) : fallbackName;

        const chunks = [];
        const reader = resp.body.getReader();
        while (true) {
            const {done, value} = await reader.read();
            if (done) break;
            chunks.push(value);
        }
        const blob = new Blob(chunks, {type: resp.headers.get('Content-Type') || 'application/octet-stream'});
        const objUrl = URL.createObjectURL(blob);

        const a = document.createElement('a');
        a.href = objUrl;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(objUrl);
    }

    //------------------------------------------------------------------
    // (B) fallback – let the browser stream via hidden iframe
    //------------------------------------------------------------------
    function iframeDownload(url) {
        return new Promise(resolve => {
            // 1️⃣ hard-timeout – gives up after 25 s
            const failSafe = setTimeout(resolve, 25_000);

            // 2️⃣ occasionally load *does* fire (e.g. 204 responses)
            const onLoad = () => {
                clearTimeout(failSafe);
                dlFrame.removeEventListener('load', onLoad);
                resolve();
            };
            dlFrame.addEventListener('load', onLoad);

            // 3️⃣ kick-off download ( ⚠️ missing “+” fixed )
            dlFrame.src = url + (url.includes('?') ? '&' : '?') + 'v=' + Date.now();
        });
    }

    async function smartDownload(url) {
        // HEAD request to know size (falls back to iframe if HEAD fails)
        let isBig = false;
        try {
            const head = await fetch(url, {method: 'HEAD', credentials: 'include'});
            const sz = Number(head.headers.get('Content-Length')) || 0;
            isBig = sz > MAX_SAFE_SIZE;
        } catch { /* HEAD failed – treat as unknown */
        }

        if (!isBig) {
            try {           // fast path first
                await fetchAndSave(url);
                return;     // success → exit
            } catch (e) {
                console.warn('Blob download failed, switching to iframe:', e);
            }
        }
        // large or fetch failed → iframe streaming
        await iframeDownload(url);
    }

    ['export-wayfarer', 'export-mymaps', 'export-pdf'].forEach(id => {
        const btn = document.getElementById(id);
        if (!btn) return;

        btn.addEventListener('click', async e => {
            e.preventDefault();

            spinnerTxt.textContent = 'Generating file…';
            await showModal(waitModal);                 // ① wait for fade-in

            try {
                await smartDownload(btn.href);            // ② download
            } catch (err) {
                wayfarer.showAlert('danger', 'Download failed.');
                console.error(err);
            } finally {
                await hideModal(waitModal);               // ③ wait for fade-out
            }
        });
    });


};

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
} else {                       // DOMContentLoaded has already fired
    init();                    // run immediately (print mode / Puppeteer)
}