import {fitCombinedRouteBadgeLabels, placeCombinedRouteBadge, placeRouteBadge, routeBadgeDataUrl,
    routeBadgeDimensions} from './segmentPresentation.js';

/** Owns the viewer's replace-only production badge layers, placement, and image decode generation. */
export const createViewerSegmentBadgeRenderer = map => {
    const layer = L.layerGroup().addTo(map);
    let generation = 0;
    let readiness = Promise.resolve({ok: true});

    const clear = () => {
        generation += 1;
        layer.clearLayers();
        readiness = Promise.resolve({ok: true});
    };

    const render = badges => {
        layer.clearLayers();
        const renderGeneration = ++generation;
        const size = map.getSize();
        const mapBounds = {left: 0, top: 0, right: size.x, bottom: size.y};
        const controlBounds = visibleControlBounds(map);
        const placedBounds = [];
        const blocked = [];
        const images = badges.flatMap(badge => {
            const anchor = map.latLngToContainerPoint([badge.location[1], badge.location[0]]);
            const placement = placeRouteBadge([anchor.x, anchor.y], routeBadgeDimensions(badge.label), mapBounds, controlBounds, placedBounds);
            if (placement.fallback) {
                blocked.push({badge, anchor});
                return [];
            }
            const raster = routeBadgeDataUrl(badge.label);
            placedBounds.push({left: placement.left, top: placement.top,
                right: placement.left + placement.width, bottom: placement.top + placement.height});
            return [renderMarker(badge, anchor, raster, placement)];
        });
        if (blocked.length) {
            const labels = blocked.map(item => item.badge.label);
            const label = labels.join('/');
            const layout = fitCombinedRouteBadgeLabels(labels, mapBounds.right - mapBounds.left - 8);
            const raster = routeBadgeDataUrl(label, layout);
            const anchors = blocked.map(item => [item.anchor.x, item.anchor.y]);
            const placement = placeCombinedRouteBadge(anchors, layout, mapBounds, controlBounds, placedBounds);
            images.push(renderMarker({...blocked[0].badge, label}, blocked[0].anchor, raster, placement));
        }
        readiness = Promise.all(images).then(() => ({ok: true}), error => ({ok: false, error}));
        return renderGeneration;
    };

    /** Adds one decorative raster badge and returns its production decode readiness. */
    const renderMarker = (badge, anchor, raster, placement) => {
        const marker = L.marker([badge.location[1], badge.location[0]], {
            icon: L.icon({iconUrl: raster.url, iconSize: [raster.width, raster.height],
                iconAnchor: [anchor.x - placement.left, anchor.y - placement.top],
                className: placement.fallback ? 'segment-route-badge-fallback' : ''}),
            interactive: false, keyboard: false, alt: ''
        }).addTo(layer);
        return waitForDecodedImage(marker.getElement?.());
    };

    const waitForCurrent = async () => {
        while (true) {
            const observedGeneration = generation;
            const result = await readiness;
            if (observedGeneration !== generation) continue;
            if (!result.ok) throw result.error;
            return generation;
        }
    };

    return {
        clear,
        count: () => layer.getLayers().length,
        dispose: () => { clear(); layer.remove(); },
        render,
        waitForCurrent
    };
};

/** Uses decode when supported and an explicit complete/load fallback otherwise. */
const waitForDecodedImage = image => {
    if (!image) return Promise.reject(new Error('Production route badge image was not attached.'));
    if (typeof image.decode === 'function') return image.decode().then(() => {
        if (!image.complete || image.naturalWidth === 0) throw new Error('Production route badge image decode completed without pixels.');
    });
    if (image.complete) return image.naturalWidth > 0 ? Promise.resolve() : Promise.reject(new Error('Production route badge image failed to load.'));
    return new Promise((resolve, reject) => {
        image.addEventListener('load', resolve, {once: true});
        image.addEventListener('error', () => reject(new Error('Production route badge image failed to load.')), {once: true});
    });
};

/** Projects visible Leaflet controls into map-container coordinates. */
const visibleControlBounds = map => {
    const container = map.getContainer();
    const origin = container.getBoundingClientRect();
    return [...container.querySelectorAll('.leaflet-control')]
        .filter(element => element.offsetParent !== null)
        .map(element => element.getBoundingClientRect())
        .map(bounds => ({left: bounds.left - origin.left, top: bounds.top - origin.top,
            right: bounds.right - origin.left, bottom: bounds.bottom - origin.top}));
};
