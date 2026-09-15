/** Shared cooperative interaction and full-view escape for public iframe maps. */
const installations = new WeakMap();

/** Preserve normal/export defaults; embed wheel zoom is enabled per intentional event. */
export const embeddedMapOptions = (embedded, printing = false) =>
    embedded && !printing ? { scrollWheelZoom: false } : {};

/** Install once per map; Leaflet owns two-finger pan/zoom and explicit controls. */
export const installEmbeddedMap = (map, fullViewUrl) => {
    if (installations.has(map)) return installations.get(map);
    const container = map.getContainer();
    const previousTouchAction = container.style.touchAction;
    container.style.touchAction = 'pan-x pan-y';

    // Capture runs before Leaflet's bubbling handlers, including its pointer-to-touch adapter.
    const wheel = event => {
        const intentional = /Mac/i.test(navigator.platform) ? event.metaKey : event.ctrlKey;
        if (intentional) map.scrollWheelZoom.enable();
        else map.scrollWheelZoom.disable();
    };
    const pointer = event => {
        if (event.pointerType === 'touch') map.dragging.disable();
        else map.dragging.enable();
    };
    const touch = event => {
        if (event.touches.length === 2) event.preventDefault();
    };
    container.addEventListener('wheel', wheel, { capture: true, passive: true });
    container.addEventListener('pointerdown', pointer, true);
    container.addEventListener('touchstart', touch, { capture: true, passive: false });

    const escape = L.control({ position: 'topright' });
    escape.onAdd = () => {
        const link = document.createElement('a');
        link.className = 'btn btn-primary btn-sm shadow d-print-none';
        link.textContent = 'Open full view';
        link.title = 'Open full view in a new tab';
        link.href = fullViewUrl;
        link.target = '_blank';
        link.rel = 'noopener';
        L.DomEvent.disableClickPropagation(link);
        return link;
    };
    escape.addTo(map);

    // Map removal and explicit reinitialization release every listener and control.
    const dispose = () => {
        container.removeEventListener('wheel', wheel, true);
        container.removeEventListener('pointerdown', pointer, true);
        container.removeEventListener('touchstart', touch, true);
        container.style.touchAction = previousTouchAction;
        escape.remove();
        map.off('unload', dispose);
        installations.delete(map);
    };
    installations.set(map, dispose);
    map.on('unload', dispose);
    return dispose;
};
