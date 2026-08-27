/**
 * Owns the single protected per-user group-notification stream for a page.
 * Events are content-free hints; callers reload authenticated durable state.
 */
export const createGroupNotificationRefresh = ({
    schedule = callback => setTimeout(callback, 100),
    cancel = handle => clearTimeout(handle),
    reload
}) => {
    let source = null;
    let pending = null;
    let disposed = false;
    const types = new Set();

    const accept = event => {
        if (disposed || typeof event?.data !== 'string') return;
        let data;
        try { data = JSON.parse(event.data); } catch { return; }
        if (!data || Array.isArray(data) || typeof data !== 'object') return;
        const keys = Object.keys(data);
        if (keys.length !== 1 || keys[0] !== 'type') return;
        if (data.type !== 'invitation-state' && data.type !== 'membership-state') return;
        types.add(data.type);
        if (pending !== null) return;
        pending = schedule(() => {
            pending = null;
            if (disposed) return;
            const acceptedTypes = new Set(types);
            types.clear();
            reload(acceptedTypes);
        });
    };

    const connect = EventSourceType => {
        if (disposed || typeof EventSourceType !== 'function' || source) return;
        source = new EventSourceType('/api/sse/group-notifications');
        source.onmessage = accept;
        source.onerror = () => {};
    };

    const dispose = () => {
        if (disposed) return;
        disposed = true;
        if (pending !== null) cancel(pending);
        pending = null;
        types.clear();
        if (source) {
            source.onmessage = null;
            source.onerror = null;
            source.close();
            source = null;
        }
    };

    return { accept, connect, dispose, get connected() { return source !== null; } };
};
