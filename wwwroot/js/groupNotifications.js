/**
 * Owns the single protected per-user group-notification stream for a page.
 * Events are content-free hints; callers reload authenticated durable state.
 */
export const reloadGroupNotificationState = async (types, signal, dependencies) => {
    const reloads = [];
    if (types.has('invitation-state')) {
        reloads.push(dependencies.fetch('/api/invitations', { signal }).then(async response => {
            if (signal.aborted || !response.ok) return;
            const invitations = await response.json();
            if (signal.aborted) return;
            await Promise.all([
                dependencies.updateInvitesBadge(invitations, signal),
                dependencies.checkPendingInvitesDiff(invitations, signal)
            ]);
            if (!signal.aborted) dependencies.dispatchInvitationState(invitations);
        }));
    }
    if (types.has('membership-state')) {
        reloads.push(
            dependencies.checkUserActivityDigest(true, signal),
            dependencies.checkJoinedGroups(signal),
            dependencies.updateManagerActivity(signal));
    }
    await Promise.all(reloads);
};

export const createGroupNotificationRefresh = ({
    schedule = callback => setTimeout(callback, 100),
    cancel = handle => clearTimeout(handle),
    reload
}) => {
    let source = null;
    let pending = null;
    let inFlight = null;
    let activeReload = null;
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
        if (pending !== null || inFlight !== null) return;
        queueReload();
    };

    const queueReload = () => {
        pending = schedule(() => {
            pending = null;
            if (disposed) return;
            const acceptedTypes = new Set(types);
            types.clear();
            const controller = new AbortController();
            activeReload = controller;
            inFlight = Promise.resolve().then(() => reload(acceptedTypes, controller.signal)).catch(() => {}).finally(() => {
                if (activeReload === controller) activeReload = null;
                inFlight = null;
                if (!disposed && types.size > 0) queueReload();
            });
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
        activeReload?.abort();
        activeReload = null;
        if (source) {
            source.onmessage = null;
            source.onerror = null;
            source.close();
            source = null;
        }
    };

    return { accept, connect, dispose, get connected() { return source !== null; } };
};
