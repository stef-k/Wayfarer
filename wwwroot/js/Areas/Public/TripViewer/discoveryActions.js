import { message } from '../../../localization/messages.js';

/** Loads a request-localized Razor fragment into the current preview modal. */
export const initQuickPreview = () => {
    const modal = document.getElementById('tripQuickView');
    const body = modal.querySelector('.modal-body');
    const loading = document.getElementById('trip-preview-loading');
    const failure = document.getElementById('trip-preview-failure');
    let request;

    document.addEventListener('click', async event => {
        const button = event.target.closest('[data-trip-preview]:not([data-hero-toggle])');
        if (!button?.dataset.tripPreview) return;

        const id = encodeURIComponent(button.dataset.tripPreview);
        request?.abort();
        request = new AbortController();
        const { signal } = request;
        body.removeAttribute('lang');
        body.replaceChildren(loading.content.cloneNode(true));

        try {
            const response = await fetch(`/Public/Trips/Preview/${id}`, {
                headers: { 'X-Requested-With': 'XMLHttpRequest' },
                signal
            });
            if (!response.ok) throw new Error(`Trip preview response: ${response.status}`);
            const html = await response.text();
            if (signal.aborted) return;
            const language = response.headers.get('Content-Language');
            if (language) body.lang = language;
            body.innerHTML = html;
        } catch (error) {
            if (signal.aborted) return;
            console.error('Failed to load trip preview:', error);
            const content = failure.content.cloneNode(true);
            content.querySelector('a').href = `/Public/Trips/${id}`;
            body.replaceChildren(content);
        }
    });
};

/** Copies a Trip URL and reports the clipboard result through the document's shared chrome. */
export const initShareButton = () => {
    document.addEventListener('click', async event => {
        const button = event.target.closest('.copy-url');
        if (!button?.dataset.url) return;
        event.preventDefault();

        const notify = wayfarer.showToast || wayfarer.showAlert;
        try {
            await navigator.clipboard.writeText(button.dataset.url);
            notify('success', message('Shell.UrlCopied', 'URL copied to clipboard!'));
        } catch (error) {
            console.error('Failed to copy URL:', error);
            notify('danger', message('Shell.UrlCopyFailed', 'Failed to copy URL.'));
        }
    });
};

/** Confirms the existing clone POST and shows its localized busy state. */
export const initCloneForms = () => {
    const { cloneTitle, cloneQuestion, cloning } = document.querySelector('[data-discovery]').dataset;
    document.addEventListener('submit', event => {
        const form = event.target;
        if (!form.classList.contains('clone-trip-form')) return;
        event.preventDefault();

        const submit = () => {
            const button = form.querySelector('button[type="submit"]');
            if (button) {
                const originalLabel = button.getAttribute('aria-label');
                const originalContent = button.innerHTML;
                button.disabled = true;
                button.setAttribute('aria-label', cloning);
                button.innerHTML = '<i class="bi bi-hourglass-split" aria-hidden="true"></i>';
                setTimeout(() => {
                    button.innerHTML = originalContent;
                    if (originalLabel) button.setAttribute('aria-label', originalLabel);
                    else button.removeAttribute('aria-label');
                    button.disabled = false;
                }, 5000);
            }
            form.submit();
        };

        if (typeof wayfarer.showConfirmationModal === 'function') {
            wayfarer.showConfirmationModal({
                title: cloneTitle, message: cloneQuestion, confirmText: cloneTitle, onConfirm: submit
            });
        } else if (confirm(cloneQuestion)) {
            submit();
        }
    });
};
