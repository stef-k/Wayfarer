import { createTileLayer } from '../../../retryTileLayer.js';
import { addZoomLevelControl } from '../../../map-utils.js';
/** Leaflet consumes observed records and geometry; camera movement never changes matching intent. */
export const createCalendarMap = (select, moved) => {
    const map = L.map('mapContainer').setView([20, 0], 3);
    createTileLayer().addTo(map); addZoomLevelControl(map);
    const markers = L.markerClusterGroup({ maxClusterRadius: 50, chunkedLoading: false }).addTo(map);
    const path = L.layerGroup().addTo(map), area = L.layerGroup().addTo(map), selected = L.layerGroup().addTo(map);
    let observed = [], previousPeriod, previousAnalysis, previousArea;
    map.on('moveend', () => moved({ latitude: map.getCenter().lat, longitude: map.getCenter().lng, zoom: map.getZoom() }));
    const fit = () => {
        const positions = observed.map(item => item.record.position).filter(point => point.status === 'available');
        if (positions.length) map.fitBounds(positions.map(point => [point.latitude, point.longitude]), { padding: [30, 30], maxZoom: 16 });
    };
    return {
        map, fit,
        restore(camera) {
            if (camera && [camera.latitude, camera.longitude, camera.zoom].every(Number.isFinite)
                && camera.latitude >= -90 && camera.latitude <= 90) map.setView([camera.latitude, camera.longitude], camera.zoom);
        },
        viewport() { const bounds = map.getBounds(); return { west: bounds.getWest(), east: bounds.getEast(), south: bounds.getSouth(), north: bounds.getNorth() }; },
        select(record) {
            selected.clearLayers();
            if (record?.position?.status === 'available') L.circleMarker([record.position.latitude, record.position.longitude],
                { radius: 10, fillOpacity: 0, color: '#8a3c00', weight: 3 }).bindTooltip('Selected record (outside map sample counts)').addTo(selected);
        },
        locate(record) { if (record?.position?.status === 'available') map.panTo([record.position.latitude, record.position.longitude]); },
        render(owner) {
            if (owner.state.period !== previousPeriod) {
                previousPeriod = owner.state.period; observed = previousPeriod?.markers || []; markers.clearLayers();
                for (const { record } of observed) if (record.position.status === 'available') {
                    L.marker([record.position.latitude, record.position.longitude], { title: `Record ${record.locationId}` })
                        .on('click', () => select(record.locationId)).addTo(markers);
                }
            }
            const analysis = owner.path ? owner.state.analysis : null;
            if (analysis !== previousAnalysis) {
                previousAnalysis = analysis; path.clearLayers();
                if (analysis?.geometry?.state === 'available') for (const island of analysis.geometry.islands || []) {
                    L.polyline(island.map(([longitude, latitude]) => [latitude, longitude]), { color: '#526e82', opacity: 0.55, weight: 3, interactive: false }).addTo(path);
                }
            }
            const rectangle = owner.filters.area;
            if (rectangle !== previousArea) {
                previousArea = rectangle; area.clearLayers();
                if (rectangle) {
                    const ends = rectangle.west > rectangle.east ? [[rectangle.west, 180], [-180, rectangle.east]] : [[rectangle.west, rectangle.east]];
                    for (const [west, east] of ends) L.rectangle([[rectangle.south, west], [rectangle.north, east]],
                        { color: '#8a3c00', weight: 2, dashArray: '6 4', fillOpacity: 0.03, interactive: false }).addTo(area);
                }
            }
        }
    };
};
