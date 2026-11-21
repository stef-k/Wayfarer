/** Reads the existing per-field fallback while tracking whether the entire camera is explicit. */
export const resolveViewerCamera = (query, defaults) => {
    const params = new URLSearchParams(query);
    const read = (key, min, max) => {
        const raw = params.get(key);
        if (!raw?.trim()) return null;
        const value = Number(raw);
        return Number.isFinite(value) && value >= min && value <= max ? value : null;
    };
    const explicit = [read('lat', -90, 90), read('lon', -180, 180), read('zoom', 0, 19)];
    return {
        view: explicit.map((value, index) => value ?? defaults[index]),
        complete: explicit.every(value => value !== null)
    };
};

/** Replaces only the camera query fields, retaining the document fragment and history entry state. */
export const writeViewerCamera = (current, entry, center, zoom) => {
    const url = new URL(current);
    url.searchParams.set('lat', center.lat.toFixed(6));
    url.searchParams.set('lon', center.lng.toFixed(6));
    url.searchParams.set('zoom', String(zoom));
    entry.replaceState(entry.state, '', url);
};
