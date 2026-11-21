import { message, number } from '../localization/messages.js';

const progressMessages = new Map(
[
  [
    "Viewer.PdfLoad",
    "Loading trip data…"
  ],
  [
    "Viewer.PdfInventory",
    "Regions: {0} · Places: {1} · Segments: {2}"
  ],
  [
    "Viewer.PdfMapLimit",
    "Map-image limit reached (64); the complete itinerary will be exported."
  ],
  [
    "Viewer.PdfCover",
    "Downloading cover photo…"
  ],
  [
    "Viewer.PdfOverview",
    "Capturing trip overview map…"
  ],
  [
    "Viewer.PdfRegions",
    "Capturing region maps ({0})…"
  ],
  [
    "Viewer.PdfRegion",
    "Region {0}/{1}: {2}"
  ],
  [
    "Viewer.PdfPlaces",
    "Capturing place maps ({0})…"
  ],
  [
    "Viewer.PdfPlace",
    "Place {0}/{1}{2}"
  ],
  [
    "Viewer.PdfSegments",
    "Capturing route segments ({0})…"
  ],
  [
    "Viewer.PdfSegment",
    "Segment {0}/{1}: {2}"
  ],
  [
    "Viewer.PdfTemplate",
    "Rendering PDF template…"
  ],
  [
    "Viewer.PdfStart",
    "Starting PDF generator…"
  ],
  [
    "Viewer.PdfRender",
    "Generating PDF document…"
  ],
  [
    "Viewer.PdfReady",
    "PDF ready. Starting download…"
  ]
]);

/** Renders trusted progress identities and scalar arguments into a text sink. */
export const pdfProgressText = event => {
    const fallback = progressMessages.get(event?.presentation);
    const args = Array.isArray(event?.arguments) ? event.arguments : [];
    if (!fallback || args.some(value => typeof value !== 'number' && typeof value !== 'string')) {
        return message('Viewer.ExportUnknown', 'Export is still running…');
    }
    return message(event.presentation, fallback, ...args.map(value => typeof value === 'number' ? number(value) : value));
};

/** Shows live export progress in the initiating document's locale. */
export const initViewerExports = () => {
    const MAX_SAFE_SIZE = 120 * 1024 * 1024;   // 120 MB  ➜ tweak to taste
    const waitModal = new bootstrap.Modal('#exportWait', {backdrop: 'static'});
    const spinnerTxt = document.querySelector('#exportWait strong');
    const dlFrame = document.getElementById('exportFrame'); // keep the iframe

    function showModal(modal) {
        return new Promise(res => {
            const el = modal._element;
            const alreadyShown = el.classList.contains('show');

            if (alreadyShown) return res();           // nothing to wait for
            el.addEventListener('shown.bs.modal', res, {once: true});
            modal.show();
        });
    }

    function hideModal(modal) {
        return new Promise(res => {
            const el = modal._element;
            const alreadyHidden = !el.classList.contains('show');

            if (alreadyHidden) return res();          // nothing to wait for
            el.addEventListener('hidden.bs.modal', res, {once: true});
            modal.hide();
        });
    }

    //------------------------------------------------------------------
    // (A) fast-path – fetch into memory → Blob → <a download>
    //------------------------------------------------------------------
    async function fetchAndSave(url, fallbackName = 'download', signal = null) {
        const resp = await fetch(url, {credentials: 'include', signal});
        if (!resp.ok) throw new ExportReplyError(resp.status);

        const cd = resp.headers.get('Content-Disposition') ?? '';
        const m = cd.match(/filename\*?=(?:UTF-8'')?["']?([^"';]+)["']?/i);
        const fileName = m ? decodeURIComponent(m[1]) : fallbackName;

        const chunks = [];
        const reader = resp.body.getReader();
        while (true) {
            const {done, value} = await reader.read();
            if (done) break;
            chunks.push(value);
        }
        const blob = new Blob(chunks, {type: resp.headers.get('Content-Type') || 'application/octet-stream'});
        const objUrl = URL.createObjectURL(blob);

        const a = document.createElement('a');
        a.href = objUrl;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(objUrl);
    }

    //------------------------------------------------------------------
    // (B) fallback – let the browser stream via hidden iframe
    //------------------------------------------------------------------
    function iframeDownload(url) {
        return new Promise(resolve => {
            // 1️⃣ hard-timeout – gives up after 25 s
            const failSafe = setTimeout(resolve, 25_000);

            // 2️⃣ occasionally load *does* fire (e.g. 204 responses)
            const onLoad = () => {
                clearTimeout(failSafe);
                dlFrame.removeEventListener('load', onLoad);
                resolve();
            };
            dlFrame.addEventListener('load', onLoad);

            // 3️⃣ kick-off download ( ⚠️ missing “+” fixed )
            dlFrame.src = url + (url.includes('?') ? '&' : '?') + 'v=' + Date.now();
        });
    }

    async function smartDownload(url, signal = null) {
        // HEAD request to know size (falls back to iframe if HEAD fails)
        let isBig = false;
        try {
            const head = await fetch(url, {method: 'HEAD', credentials: 'include', signal});
            const sz = Number(head.headers.get('Content-Length')) || 0;
            isBig = sz > MAX_SAFE_SIZE;
        } catch (e) {
            // If aborted, re-throw to let caller handle it
            if (e.name === 'AbortError') throw e;
            /* HEAD failed – treat as unknown */
        }

        if (!isBig) {
            try {           // fast path first
                await fetchAndSave(url, 'download', signal);
                return;     // success → exit
            } catch (e) {
                // If aborted, re-throw to let caller handle it
                if (e.name === 'AbortError' || e instanceof ExportReplyError) throw e;
                console.warn('Blob download failed, switching to iframe:', e);
            }
        }
        // large or fetch failed → iframe streaming (cannot abort iframe downloads)
        await iframeDownload(url);
    }

    ['export-wayfarer', 'export-mymaps', 'export-pdf'].forEach(id => {
        const btn = document.getElementById(id);
        if (!btn) return;

        btn.addEventListener('click', async e => {
            e.preventDefault();

            const isPdf = id === 'export-pdf';
            let eventSource = null;
            let abortController = null;
            const progressMsg = document.getElementById('export-progress-message');
            const cancelBtn = document.getElementById('export-cancel-btn');

            if (isPdf && progressMsg) {
                // Generate unique session ID for this export
                const sessionId = crypto.randomUUID();

                // Extract trip ID from button href (format: /Trip/ExportPdf/{tripId})
                const tripId = btn.href.split('/').pop().split('?')[0];

                // Setup SSE for real-time progress updates
                const progressUrl = `/Trip/ExportProgress/${tripId}?sessionId=${sessionId}`;
                eventSource = new EventSource(progressUrl);

                eventSource.onmessage = (evt) => {
                    try {
                        progressMsg.textContent = pdfProgressText(JSON.parse(evt.data));
                    } catch (e) {
                        console.warn('Failed to parse progress message:', e);
                    }
                };

                eventSource.onerror = () => {
                    // Connection lost - not critical, just stop updating
                    eventSource?.close();
                };

                spinnerTxt.textContent = message('Viewer.GeneratingPdf', 'Generating PDF Export');
                progressMsg.textContent = message('Viewer.Connecting', 'Connecting...');

                // Create AbortController for cancellation
                abortController = new AbortController();

                // Wire up cancel button
                if (cancelBtn) {
                    cancelBtn.style.display = 'inline-block';
                    const handleCancel = () => {
                        abortController.abort();

                    };
                    cancelBtn.onclick = handleCancel;
                }

                // Add sessionId to download URL
                const dlUrl = btn.href + (btn.href.includes('?') ? '&' : '?') + `sessionId=${sessionId}`;

                await showModal(waitModal);

                try {
                    await smartDownload(dlUrl, abortController.signal);
                } catch (err) {
                    if (err.name === 'AbortError') {
                        progressMsg.textContent = message('Viewer.ExportCancelled', 'PDF export cancelled.');
                        wayfarer.showAlert('info', message('Viewer.ExportCancelled', 'PDF export cancelled.'));
                    } else {
                        wayfarer.showAlert('danger', downloadFailure(err, true));
                        console.error(err);
                    }
                } finally {
                    eventSource?.close();
                    if (cancelBtn) { cancelBtn.onclick = null; cancelBtn.style.display = 'none'; }
                    await hideModal(waitModal);
                }
            } else {
                // KML exports - simple flow (no progress)
                spinnerTxt.textContent = message('Viewer.GeneratingFile', 'Generating file…');
                await showModal(waitModal);

                try {
                    await smartDownload(btn.href);
                } catch (err) {
                    wayfarer.showAlert('danger', downloadFailure(err));
                    console.error(err);
                } finally {
                    await hideModal(waitModal);
                }
            }
        });
    });

};

/** Carries an explicit HTTP rejection through the download fallback boundary. */
class ExportReplyError extends Error {
    constructor(status) {
        super(`HTTP ${status}`);
        this.status = status;
    }
}

/** Maps actionable HTTP outcomes without rendering remote diagnostics. */
const downloadFailure = (error, pdf = false) => {
    switch (error.status) {
        case 401:
        case 403:
        case 404:
            return message('Viewer.DownloadUnavailable', 'The Trip is unavailable or you no longer have access.');
        case 409:
            return message('Viewer.DownloadConflict', 'The Trip changed. Review its current state before trying again.');
        case 503:
            if (pdf) return message('Viewer.DownloadRetry', 'PDF generation is temporarily unavailable. Retry shortly.');
    }
    return pdf ? message('Viewer.ExportFailed', 'PDF export failed.') : message('Viewer.DownloadFailed', 'Download failed.');
};
