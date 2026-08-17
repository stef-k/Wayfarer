import {
    getSegmentPolyline,
    getSegmentPresentationSnapshot,
    refreshSegmentPresentation,
    setActiveSegment
} from './tripViewerHelpers.js';

/** Owns transient viewer selection, keyboard state, map emphasis, and print readiness. */
export const createViewerSegmentPresentationController = (map, root, options) => {
    const select = (segmentId, fit = true) => {
        setActiveSegment(map, segmentId);
        document.querySelectorAll('.segment-selection-button').forEach(button => {
            const selected = button.closest('.segment-list-item')?.dataset.segmentId === segmentId;
            if (selected) button.setAttribute('aria-current', 'true');
            else button.removeAttribute('aria-current');
        });
        root.dataset.activeSegmentId = segmentId ?? '';
        publishSnapshot();
        const line = segmentId ? getSegmentPolyline(segmentId) : null;
        if (fit && line) {
            map.flyToBounds(line.getBounds(), {
                animate: !options.isPrint,
                duration: options.isPrint ? 0 : 1.2,
                padding: [options.paddingX(), 60]
            });
        }
    };

    const refresh = () => {
        refreshSegmentPresentation(map);
        publishSnapshot();
    };
    map.on('zoomend moveend', refresh);
    window.wayfarer ??= {};
    window.wayfarer.selectSegment = segmentId => select(segmentId);

    const initializePrint = isolatedSegmentId => {
        if (isolatedSegmentId && getSegmentPolyline(isolatedSegmentId)) {
            select(isolatedSegmentId, false);
            map.fitBounds(getSegmentPolyline(isolatedSegmentId).getBounds(), {padding: [60, 60], animate: false});
        }
        requestAnimationFrame(() => requestAnimationFrame(() => {
            const snapshot = publishSnapshot();
            window.__segmentPresentationReady = options.isPrint
                ? isolatedSegmentId ? snapshot.segments.length === 1 && snapshot.routeBadgeCount > 0 : snapshot.segments.length === 0
                : true;
        }));
    };

    return {initializePrint, select};
};

/** Publishes only the issue-approved serializable ownership snapshot. */
const publishSnapshot = () => {
    const snapshot = getSegmentPresentationSnapshot();
    window.__segmentPresentationSnapshot = snapshot;
    return snapshot;
};
