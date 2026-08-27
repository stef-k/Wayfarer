/** Creates the content-free SSE reload coordinator for the import page. */
export const createLocationImportRefresh = ({ schedule, cancel = clearTimeout, reload }) => {
    let source = null;
    let pendingHandle = null;
    let disposed = false;

    const accept = event => {
        let hint;
        try { hint = JSON.parse(event?.data); } catch { return; }
        if (!hint || Object.keys(hint).length !== 1
            || !['import-state', 'enrichment-state'].includes(hint.type)
            || pendingHandle !== null || disposed) return;
        pendingHandle = schedule(() => {
            pendingHandle = null;
            if (!disposed) reload();
        }, 100);
    };

    const connect = (EventSourceType, url) => {
        if (typeof EventSourceType !== 'function') return;
        try {
            source = new EventSourceType(url);
            source.onmessage = accept;
            source.onerror = () => {};
        } catch { source = null; }
    };

    const dispose = () => {
        if (disposed) return;
        disposed = true;
        if (pendingHandle !== null) cancel(pendingHandle);
        pendingHandle = null;
        if (source) {
            source.onmessage = null;
            source.onerror = null;
            source.close();
            source = null;
        }
    };

    return { accept, connect, dispose, get connected() { return source !== null; } };
};
