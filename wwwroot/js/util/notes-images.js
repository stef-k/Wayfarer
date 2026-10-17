/** Restores same-origin display proxies without decoding HTML entities a second time. */
export const canonicalNoteImageSource = value => {
    let source = (value || '').trim();
    const seen = new Set();
    for (let depth = 0; depth <= 32 && !seen.has(source); depth++) {
        // Reject network-path sources, including browser-normalized slashes/controls, before any live src assignment.
        if (/^[\u0000-\u0020]*[\\/][\t\n\r]*[\\/]/.test(source)) return '';
        seen.add(source);
        try {
            const url = new URL(source, window.location.origin);
            if (url.origin !== window.location.origin || url.pathname.toLowerCase() !== '/public/proxyimage') return source;
            const targets = url.searchParams.getAll('url');
            if (targets.length !== 1) return source;
            source = targets[0].trim();
        } catch { return source; }
    }
    return '';
};

/** Routes only absolute external HTTP(S) note images through the existing endpoint once. */
export const noteImageSourceForDisplay = value => {
    const source = canonicalNoteImageSource(value);
    try {
        const url = new URL(source);
        if ((url.protocol === 'http:' || url.protocol === 'https:') && url.origin !== window.location.origin) {
            return `/Public/ProxyImage?url=${encodeURIComponent(source)}`;
        }
    } catch { /* Relative and unsupported sources retain their existing policy. */ }
    return source;
};

/** Transforms images in an inert template; the server remains the rich-notes sanitizer. */
const mapNoteImages = (html, transform) => {
    const template = document.createElement('template');
    template.innerHTML = html || '';
    template.content.querySelectorAll('img[src]').forEach(image => {
        image.setAttribute('src', transform(image.getAttribute('src')));
    });
    return template.innerHTML;
};

/** Prepares published canonical notes before insertion into the live document. */
export const notesHtmlForDisplay = html => mapNoteImages(html, noteImageSourceForDisplay);

/** Keeps form submissions canonical while leaving the displayed editor untouched. */
export const notesHtmlForSave = html => mapNoteImages(html, canonicalNoteImageSource);

/** Creates proxied Quill images before src assignment, including toolbar, clipboard and delta insertion. */
export const registerNoteImageBlot = Quill => {
    const Image = Quill.import('formats/image');
    class NoteImage extends Image {
        /** The native blot receives a display URL before it creates an image element. */
        static create(value) { return super.create(noteImageSourceForDisplay(value)); }
        /** Quill deltas retain the original URL independently of the displayed src. */
        static value(node) { return canonicalNoteImageSource(super.value(node)); }
    }
    Quill.register(NoteImage, true);
};
