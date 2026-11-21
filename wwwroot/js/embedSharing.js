import { message } from './localization/messages.js';

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

const publicHttpsError = "Open Wayfarer at its public HTTPS address to copy an embed.";

/** Reject clearly local origins instead of silently advertising an unusable public snippet. */
const publicOrigin = origin => {
    const url = new URL(origin);
    const host = url.hostname.toLowerCase();
    const privateHost = (!host.startsWith('[') && !host.includes('.')) ||
        host.endsWith('.localhost') || host.endsWith('.local') || host.endsWith('.internal') ||
        /^\[(?:::1\]|::\]|f[cd]|fe[89ab]|::ffff:)/.test(host) ||
        /^(0|10|127|169\.254|192\.168)\./.test(host) || /^172\.(1[6-9]|2\d|3[01])\./.test(host);
    if (url.protocol !== 'https:' || privateHost || url.username || url.password) {
        throw new Error(publicHttpsError);
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

/** Shares one clipboard path with the direct URL and both embed formats. */
export const copyWithFeedback = async (getText, label, feedback) => {
    const notify = (level, text) => (wayfarer.showToast || wayfarer.showAlert).call(wayfarer, level, text);
    try {
        await navigator.clipboard.writeText(getText());
        notify('success', feedback?.success ?? `${label} copied to clipboard!`);
    } catch (error) {
        const detail = error.message === publicHttpsError ? ' ' + message('Shell.EmbedPublicHttps', publicHttpsError) : '';
        notify('danger', (feedback?.failure ?? `Failed to copy ${label}.`) + detail);
    }
};

/** Copy either format from the same output contract. */
export const copyMapEmbed = (element, origin = window.location.origin) => {
    const format = element.dataset.embedFormat;
    const feedback = format === 'html'
        ? { success: message('Shell.EmbedHtmlCopied', 'Embed HTML copied to clipboard!'),
            failure: message('Shell.EmbedHtmlCopyFailed', 'Failed to copy the embed HTML.') }
        : { success: message('Shell.EmbedUrlCopied', 'Embed URL copied to clipboard!'),
            failure: message('Shell.EmbedUrlCopyFailed', 'Failed to copy the embed URL.') };
    return copyWithFeedback(() => {
        if (format !== 'url' && format !== 'html') throw new Error('Unknown embed format.');
        const output = buildMapEmbed({ kind: element.dataset.embedKind, id: element.dataset.embedId,
            title: element.dataset.embedTitle }, origin);
        return output[format];
    }, undefined, feedback);
};

/** Delegation supports dynamically rendered Trip rows and static Timeline settings. */
export const handleEmbedCopy = event => {
    const element = event.target.closest('[data-embed-format]');
    if (!element) return;
    event.preventDefault();
    return copyMapEmbed(element);
};
