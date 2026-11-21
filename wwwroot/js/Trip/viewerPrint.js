/** Builds the printable readable view from the content already rendered in the initiating document. */
export const populateViewerPrint = (target, source, title, origin) => {
    target.documentElement.lang = source.ownerDocument.documentElement.lang;
    target.title = title;
    const heading = target.createElement('h5');
    heading.textContent = title;
    const content = source.cloneNode(true);
    content.querySelectorAll('.profile-photo, #btn-back-to-top').forEach(element => element.remove());
    for (const img of content.querySelectorAll('img')) {
        if (img.src.includes('/icons/wayfarer-map-icons/')) continue;
        const url = new URL(img.src, origin);
        if (url.origin === origin && url.pathname.toLowerCase() === '/public/proxyimage') {
            url.searchParams.set('maxWidth', '600');
            url.searchParams.set('quality', '85');
            img.src = url.href;
        } else if (['http:', 'https:'].includes(url.protocol) && url.origin !== origin) {
            img.src = `/Public/ProxyImage?url=${encodeURIComponent(img.src)}&maxWidth=600&quality=85`;
        }
        img.style.maxWidth = '100%';
        img.style.height = 'auto';
    }
    target.body.append(heading, ...content.childNodes);
};

/** Retains the readable modal print recipe without reinterpreting the Trip title as markup. */
const printReadable = () => {
    const printable = window.open('', '', 'width=800,height=600');
    if (!printable) return;
    const target = printable.document;
    target.open();
    target.write(`<!DOCTYPE html>
        <html><head><meta charset="utf-8">
        <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.8/dist/css/bootstrap.min.css" rel="stylesheet">
        <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap-icons@1.13.2/font/bootstrap-icons.css">
        <style>
            @page { size: A4; margin: 3cm 2cm 2cm 3.5cm; }
            body { padding: 0; font-size: 12pt; }
            @media print {
                body { padding: 0; margin: 0; font-size: 11pt; }
                .no-print { display: none !important; }
                img { max-width: 100% !important; height: auto !important; page-break-inside: avoid; }
            }
            @media screen { body { padding: 2rem; font-size: 14pt; } }
            h5 { text-align: center; margin-bottom: 2rem; font-size: 1.5rem; }
            h6 { font-size: 1.1rem; }
            .region-readable h5 { font-size: 1.3rem; }
            .region-readable { page-break-inside: avoid; }
        </style></head><body></body></html>`);
    populateViewerPrint(target, document.getElementById('readable-modal-body'),
        document.getElementById('readableViewModalLabel').textContent, location.origin);
    printable.addEventListener('load', () => setTimeout(() => printable.print(), 500), { once: true });
    target.close();
};

globalThis.document?.getElementById('btn-print-modal')?.addEventListener('click', printReadable);
