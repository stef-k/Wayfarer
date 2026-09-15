/** Canonical public map output. The browser origin is the public-facing origin, never a backend Host. */
export const publicMapPath = (kind, id) => {
    if (!id || !String(id).trim()) throw new Error('A public map identifier is required.');
    const value = encodeURIComponent(id);
    if (kind === 'trip') return `/Public/Trips/${value}`;
    if (kind === 'timeline') return `/Public/Users/Timeline/${value}`;
    throw new Error('Unknown public map type.');
};

/** Attribute escaping is independent of DOM parsing and also protects quotes in map titles. */
export const escapeEmbedAttribute = value => String(value).replace(/[&<>"']/g,
    character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]);

/** Reject clearly local origins instead of silently advertising an unusable public snippet. */
const publicOrigin = origin => {
    const url = new URL(origin);
    const host = url.hostname.toLowerCase();
    const privateHost = !host.includes('.') || host.endsWith('.localhost') || host.endsWith('.local') ||
        host.endsWith('.internal') || host.includes(':') ||
        /^(0|10|127|169\.254|192\.168)\./.test(host) || /^172\.(1[6-9]|2\d|3[01])\./.test(host);
    if (url.protocol !== 'https:' || privateHost || url.username || url.password) {
        throw new Error('Open Wayfarer at its public HTTPS address to copy an embed.');
    }
    return url.origin;
};

/** URL and HTML always derive from one canonical URL, with no current viewport or owner state. */
export const buildMapEmbed = ({ kind, id, title }, origin = window.location.origin) => {
    const path = publicMapPath(kind, id);
    const url = `${publicOrigin(origin)}${path}${kind === 'trip' ? '?embed=true' : '/embed'}`;
    const accessibleTitle = String(title || '').trim() || `Wayfarer ${kind} map`;
    const html = `<iframe src="${escapeEmbedAttribute(url)}" title="${escapeEmbedAttribute(accessibleTitle)}" width="100%" height="600" loading="lazy" style="border:0;"></iframe>`;
    return { url, html };
};

/** Existing Trip clipboard feedback, shared with Timeline and deferred output validation. */
export const copyWithFeedback = async (getText, label) => {
    const notify = (level, message) => (wayfarer.showToast || wayfarer.showAlert).call(wayfarer, level, message);
    try {
        await navigator.clipboard.writeText(getText());
        notify('success', `${label} copied to clipboard!`);
    } catch (error) {
        const reason = error.message?.startsWith('Open Wayfarer at') ? ` ${error.message}` : '';
        notify('danger', `Failed to copy ${label}.${reason}`);
    }
};

/** Copy either format from the same output contract. */
export const copyMapEmbed = (element, origin = window.location.origin) => copyWithFeedback(() => {
    const output = buildMapEmbed({ kind: element.dataset.embedKind, id: element.dataset.embedId,
        title: element.dataset.embedTitle }, origin);
    const format = element.dataset.embedFormat;
    if (format !== 'url' && format !== 'html') throw new Error('Unknown embed format.');
    return output[format];
}, `embed ${element.dataset.embedFormat?.toUpperCase()}`);

/** Delegation supports dynamically rendered Trip rows and static Timeline settings. */
export const handleEmbedCopy = event => {
    const element = event.target.closest('[data-embed-format]');
    if (!element) return;
    event.preventDefault();
    return copyMapEmbed(element);
};
