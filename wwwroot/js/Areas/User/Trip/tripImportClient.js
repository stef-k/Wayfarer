/** Fixed fallback message; bounded preservation refusals have their own local explanation. */
export const genericImportFailure = 'Import failed. Please try again.';
/** Deterministic WB1 refusal: no server-authored text enters the dialog. */
export const planningLossExplanation = 'This update would remove objects with private Planning. Your existing Trip was not changed. Cancel to keep it, or create a separate copy from the selected file. The copy will not include the existing Trip’s private Planning.';

/**
 * Submits one trip import without exposing response bodies to the UI.
 * @param {File} file Selected KML file.
 * @param {string} mode Import mode understood by the server.
 * @param {{ fetchImpl: typeof fetch, onRedirect: (url: string) => void, onDuplicate: () => void, showError: (message: string) => void, showNotices?: (notices: Array<object>) => void, onPlanningLoss?: () => void }} handlers UI callbacks.
 * @returns {Promise<void>} Completes after the response has been handled safely.
 */
export const submitTripImport = async (file, mode, handlers) => {
    const formData = new FormData();
    formData.append('file', file);
    formData.append('mode', mode);

    try {
        const response = await handlers.fetchImpl.call(globalThis, '/User/Trip/Import', { method: 'POST', headers: { 'RequestVerificationToken': document.querySelector('input[name="__RequestVerificationToken"]')?.value || '' }, body: formData });
        const payload = await response.json();
        if (response.status === 409 && !response.redirected && payload?.status === 'error' && payload.code === 'planning_loss_requires_review') {
            if (handlers.onPlanningLoss) handlers.onPlanningLoss();
            else handlers.showError(planningLossExplanation);
            return;
        }
        if (payload?.status === 'success' && typeof payload.redirectUrl === 'string') {
            handlers.showNotices?.(Array.isArray(payload.notices) ? payload.notices : []);
            handlers.onRedirect(payload.redirectUrl);
            return;
        }
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

/** Owns the selected file across duplicate/refusal choices; cancellation never sends another request. */
export const createTripImporter = handlers => {
    let selectedFile = null;
    const upload = async (file, mode = 'Auto') => {
        selectedFile = file;
        await submitTripImport(file, mode, handlers);
    };
    return {
        upload,
        update: async () => { if (selectedFile) await upload(selectedFile, 'Upsert'); },
        createCopy: async () => { if (selectedFile) await upload(selectedFile, 'CreateNew'); },
        cancel: () => { selectedFile = null; }
    };
};
