import {
    getSegmentPolyline,
    getSegmentPresentationSnapshot,
    refreshSegmentPresentation,
    setActiveSegment,
    waitForCurrentBadgeImages
} from './tripViewerHelpers.js';

/** Owns transient viewer selection, keyboard state, map emphasis, and print readiness. */
export const createViewerSegmentPresentationController = (map, root, options) => {
    let initializationGeneration = 0;
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

    const initialize = async requestedSegmentId => {
        const generation = ++initializationGeneration;
        const registeredLine = requestedSegmentId ? getSegmentPolyline(requestedSegmentId) : null;
        if (!options.isPrint) {
            select(registeredLine ? requestedSegmentId : null, Boolean(registeredLine));
            return true;
        }

        window.__segmentPresentationReady = false;
        if (registeredLine) {
            select(requestedSegmentId, false);
            map.fitBounds(registeredLine.getBounds(), {padding: [60, 60], animate: false});
        } else {
            select(null, false);
        }
        try {
            while (generation === initializationGeneration) {
                const badgeGeneration = await waitForCurrentBadgeImages();
                if (generation !== initializationGeneration) return false;
                if (document.fonts?.ready) await document.fonts.ready;
                await finalFrames();
                if (generation !== initializationGeneration) return false;
                const stableGeneration = await waitForCurrentBadgeImages();
                if (generation !== initializationGeneration) return false;
                if (badgeGeneration !== stableGeneration) continue;
                const snapshot = publishSnapshot();
                const complete = registeredLine
                    ? snapshot.segments.length === 1 && snapshot.segments[0].id === requestedSegmentId
                      && snapshot.segments[0].lineCount === 1 && snapshot.segments[0].chevronCount > 0 && snapshot.routeBadgeCount > 0
                    : snapshot.segments.length === 0 && snapshot.routeBadgeCount === 0;
                if (generation === initializationGeneration) window.__segmentPresentationReady = complete;
                return complete;
            }
        } catch (error) {
            if (generation === initializationGeneration) window.__segmentPresentationReady = false;
            console.error('[print] production Segment badge decode failed', error);
        }
        return false;
    };

    return {initialize, select};
};

/** Completes the two final paint opportunities required by snapshot capture. */
const finalFrames = () => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));

/** Publishes only the issue-approved serializable ownership snapshot. */
const publishSnapshot = () => {
    const snapshot = getSegmentPresentationSnapshot();
    window.__segmentPresentationSnapshot = snapshot;
    return snapshot;
};
