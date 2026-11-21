import { registerLanguageDraft } from '../../../localization/languageNavigation.js';
import { locale, message } from '../../../localization/messages.js';

/** Bind the actual map edit lifecycle and serialized draft to the shared selector. */
export const bindHiddenAreaDraft = (map, events, input, polygons) => {
    const initial = input.value;
    let editing = false;
    const start = () => { editing = true; };
    const stop = () => { editing = false; };
    const store = layer => { input.value = toWkt(layer.getLatLngs()); };
    map.on(events.DRAWSTART, start).on(events.EDITSTART, start).on(events.DELETESTART, start);
    map.on(events.DRAWSTOP, stop).on(events.EDITSTOP, stop).on(events.DELETESTOP, stop);
    map.on(events.CREATED, ({ layer }) => {
        polygons.clearLayers().addLayer(layer);
        store(layer);
    });
    map.on(events.EDITED, ({ layers }) => layers.eachLayer(store));
    map.on(events.DELETED, () => { input.value = ''; });
    return registerLanguageDraft(() => editing || input.value !== initial);
};

/** Leaflet uses latitude/longitude. WKT stores longitude/latitude with invariant decimals. */
const fromWkt = wkt => {
    const groups = /^(?:SRID=4326;\s*)?POLYGON\s*\(\s*(.+)\s*\)$/is.exec(wkt.trim());
    if (!groups) throw new Error('Invalid polygon');
    const rings = groups[1].split(/\)\s*,\s*\(/);
    return rings.map(ring => ring.replace(/^\s*\(|\)\s*$/g, '').split(',').map(pair => {
        const [lng, lat] = pair.trim().split(/\s+/).map(Number);
        if (!Number.isFinite(lng) || !Number.isFinite(lat)) throw new Error('Invalid coordinate');
        return [lat, lng];
    }));
};

/** Writes each ring in its original order, closing it with the same first vertex. */
const toWkt = rings => {
    const coordinates = rings.map(ring => {
        const points = ring.map(({ lng, lat }) => `${lng} ${lat}`);
        return `(${[...points, points[0]].join(', ')})`;
    });
    return `POLYGON(${coordinates.join(', ')})`;
};

/** Retain the forms' existing img-based retries with a short, finite backoff. */
const addTiles = map => {
    const { tilesUrl = '/Public/tiles/{z}/{x}/{y}.png', attribution = '&copy; OpenStreetMap contributors' } =
        window.wayfarerTileConfig || {};
    const attempts = new Map();
    L.tileLayer(tilesUrl, { maxZoom: 19, attribution }).addTo(map).on('tileerror', ({ tile }) => {
        const source = tile.src;
        if (!source) return;
        const key = source.split('?')[0];
        const count = (attempts.get(key) || 0) + 1;
        attempts.set(key, count);
        if (count <= 5) setTimeout(() => { tile.src = source; }, 1000 * 2 ** (count - 1));
    });
};

/** Leaflet.Draw treats tooltip content as HTML, so all interpolated phrases are encoded. */
const localizeDrawing = () => {
    const plain = (key, fallback) => message(`HiddenAreas.${key}`, fallback);
    const text = document.createElement('span');
    const html = value => {
        text.textContent = value;
        return text.innerHTML;
    };
    const chrome = (key, fallback) => html(message(`Shell.${key}`, fallback));
    const { draw, edit } = L.drawLocal;
    draw.toolbar.buttons.polygon = plain('DrawPolygon', 'Draw a polygon');
    draw.toolbar.actions = { title: plain('DrawCancel', 'Cancel drawing'), text: chrome('Cancel', 'Cancel') };
    draw.toolbar.finish = { title: plain('DrawFinishTitle', 'Finish drawing'), text: html(plain('DrawFinish', 'Finish')) };
    draw.toolbar.undo = { title: plain('DrawUndoTitle', 'Delete last point drawn'), text: html(plain('DrawUndo', 'Delete last point')) };
    draw.handlers.polygon.tooltip = {
        start: chrome('DrawPolygonStart', 'Click to start drawing the area.'),
        cont: chrome('DrawPolygonContinue', 'Click to add another vertex.'),
        end: chrome('DrawPolygonFinish', 'Click the first vertex to close the area.')
    };
    draw.handlers.polyline.error = chrome('DrawEdgesCannotCross', 'Area edges cannot cross.');
    edit.toolbar.buttons = {
        edit: plain('EditPolygon', 'Edit polygon'), editDisabled: plain('EditDisabled', 'No polygon to edit'),
        remove: plain('RemovePolygon', 'Remove polygon'), removeDisabled: plain('RemoveDisabled', 'No polygon to remove')
    };
    edit.toolbar.actions = {
        save: { title: plain('EditSave', 'Save drawing changes'), text: html(plain('Save', 'Save')) },
        cancel: { title: plain('EditCancel', 'Cancel and undo drawing changes'), text: chrome('Cancel', 'Cancel') },
        clearAll: { title: plain('ClearAllTitle', 'Clear all polygons'), text: html(plain('ClearAll', 'Clear all')) }
    };
    edit.handlers.edit.tooltip = {
        text: chrome('EditPolygonHandles', 'Drag the handles to edit the area.'),
        subtext: chrome('EditPolygonCancel', 'Click Cancel to undo changes.')
    };
    edit.handlers.remove.tooltip.text = html(plain('RemoveHint', 'Click the polygon to remove it.'));

    const readableArea = L.GeometryUtil.readableArea.bind(L.GeometryUtil);
    const parts = new Intl.NumberFormat(locale).formatToParts(12345.6);
    L.drawLocal.format = { numeric: { delimiters: {
        thousands: parts.find(part => part.type === 'group')?.value,
        decimal: parts.find(part => part.type === 'decimal')?.value
    } } };
    L.GeometryUtil.readableArea = (...args) => {
        const value = readableArea(...args);
        const [amount, unit] = value.split(/ (?=\S+$)/);
        return html(unit === 'ha' ? message('HiddenAreas.Hectares', '{0} ha', amount)
            : message('HiddenAreas.SquareMetres', '{0} m²', amount));
    };
};

/** Mounts the same polygon state owner for the create and edit forms. */
const mountDrawing = root => {
    localizeDrawing();
    const create = root.dataset.create === 'true';
    const map = L.map(root, { zoomControl: false }).setView([0, 0], create ? 3 : 2);
    L.control.zoom({ zoomInTitle: message('Shell.ZoomIn', 'Zoom in'),
        zoomOutTitle: message('Shell.ZoomOut', 'Zoom out') }).addTo(map);
    addTiles(map);
    const poweredBy = document.createElement('a');
    poweredBy.href = 'https://wayfarer.stefk.me';
    poweredBy.target = '_blank';
    poweredBy.title = message('Shell.WayfarerMapCredit', 'Powered by Wayfarer, made by Stef');
    poweredBy.textContent = 'Wayfarer';
    const blog = document.createElement('a');
    blog.href = 'https://stefk.me';
    blog.target = '_blank';
    blog.title = message('HiddenAreas.Blog', 'Visit my blog');
    blog.textContent = 'Stef K';
    map.attributionControl.setPrefix(`&copy; ${poweredBy.outerHTML} | ${blog.outerHTML} | &copy; <a href="https://leafletjs.com/" target="_blank">Leaflet</a>`);

    const polygons = new L.FeatureGroup().addTo(map);
    const drawingStyle = { color: '#ff7800', weight: 3 };
    map.addControl(new L.Control.Draw({
        draw: create ? { polyline: false, rectangle: false, circle: false, circlemarker: false, marker: false,
            polygon: { allowIntersection: false, showArea: true, shapeOptions: drawingStyle } } : false,
        edit: { featureGroup: polygons, edit: true, remove: true }
    }));

    const input = document.getElementById('areaInput');
    if (input.value) {
        try {
            const polygon = L.polygon(fromWkt(input.value), create ? { color: drawingStyle.color } : {});
            polygons.addLayer(polygon);
            map.fitBounds(polygon.getBounds());
        } catch {
            document.getElementById('area-map-error').hidden = false;
        }
    }
    bindHiddenAreaDraft(map, L.Draw.Event, input, polygons);
};

const root = globalThis.document?.getElementById('hidden-area-map');
if (root) mountDrawing(root);
