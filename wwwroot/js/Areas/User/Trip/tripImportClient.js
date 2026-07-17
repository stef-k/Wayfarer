/** Fixed, non-server-derived message used for every failed import request. */
export const genericImportFailure = 'Import failed. Please try again.';

/**
 * Submits one trip import without exposing response bodies to the UI.
 * @param {File} file Selected KML file.
 * @param {string} mode Import mode understood by the server.
 * @param {{ fetchImpl: typeof fetch, onRedirect: (url: string) => void, onDuplicate: () => void, showError: (message: string) => void }} handlers UI callbacks.
 * @returns {Promise<void>} Completes after the response has been handled safely.
 */
export const submitTripImport = async (file, mode, handlers) => {
    const formData = new FormData();
    formData.append('file', file);
    formData.append('mode', mode);

    try {
        const response = await handlers.fetchImpl('/User/Trip/Import', { method: 'POST', body: formData });
        if (response.redirected) {
            handlers.onRedirect(response.url);
            return;
        }

        const payload = await response.json();
        if (payload?.status === 'duplicate') {
            handlers.onDuplicate();
            return;
        }

        handlers.showError(genericImportFailure);
    } catch (error) {
        console.error('[Trip] Import request failed:', error);
        handlers.showError(genericImportFailure);
    }
};
