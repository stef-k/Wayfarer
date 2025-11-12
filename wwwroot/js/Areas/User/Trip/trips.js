/* trips.js Trip index view functionality */
(() => {
    /* ------------------------------------------------ Delete trip */
    const handleDeleteClick = btn => {
        const tripId = btn.dataset.tripId;

        wayfarer.showConfirmationModal({
            title: "Delete Trip",
            message: "Are you sure you want to delete this trip?",
            confirmText: "Delete",
            onConfirm: async () => {
                const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
                const fd = new FormData();
                fd.set('__RequestVerificationToken', token);
                fd.set('id', tripId);

                const resp = await fetch('/User/Trip/Delete', {
                    method: 'POST',
                    body: fd
                });

                if (resp.ok) {
                    document.querySelector(`tr button[data-trip-id="${tripId}"]`)?.closest('tr')?.remove();
                    wayfarer.showAlert("success", "Trip deleted.");
                } else {
                    wayfarer.showAlert("danger", "Failed to delete trip.");
                }
            }
        });
    };

    /* ------------------------------------------------ Import trip */
    const importInput      = document.getElementById('importFile');       // Wayfarer
    const importInputMyMap = document.getElementById('importFileMyMaps'); // My Maps
    const dupModalEl = document.getElementById('dupModal');
    const dupModal = new bootstrap.Modal(dupModalEl);
    let pendingFile;           // holds the File until user picks Upsert / Copy

    /* ------------------------------------------------ file-picker hooks ----- */
    [importInput, importInputMyMap].forEach(inp => {
        if (!inp) return;
        inp.addEventListener('change', e => {
            const file = e.target.files?.[0];
            if (file) upload(file);            // default mode = Auto
            inp.value = '';
        });
    });
    const upload = async (file, mode = 'Auto') => {
        const fd = new FormData();
        fd.append('file', file);
        fd.append('mode', mode);

        const resp = await fetch('/User/Trip/Import', {method: 'POST', body: fd});

        /* 1 ── success: server replied 302 Location → browser-side redirect */
        if (resp.redirected) {
            dupModal.hide();
            window.location.href = resp.url;
            return;
        }

        /* 2 ── try to read JSON only when response is JSON ---------------- */
        let payload = null;
        const isJson = resp.headers
            .get('Content-Type')
            ?.startsWith('application/json');

        if (isJson) {
            try {
                payload = await resp.json();
            } catch (e) { /* silence parse errors */
            }
        }

        if (payload?.status === 'duplicate') {
            pendingFile = file;
            dupModal.show();
        } else {
            const msg = payload?.message || await resp.text();      // fallback
            wayfarer.showAlert('danger', msg || 'Import failed.');
        }
    };

    document.getElementById('btnUpdate')?.addEventListener('click', () => {
        dupModal.hide();
        if (pendingFile) upload(pendingFile, 'Upsert');
    });

    document.getElementById('btnCopy')?.addEventListener('click', () => {
        dupModal.hide();
        if (pendingFile) upload(pendingFile, 'CreateNew');
    });

    /* ------------------------------------------------ boot */
    document.addEventListener('DOMContentLoaded', () => {
        // Clipboard copy handling
        document.addEventListener('click', async (e) => {
            const el = e.target.closest('a.copy-url');
            if (!el) return;

            e.preventDefault();

            const url = el.dataset.url;
            try {
                await navigator.clipboard.writeText(`${window.location.origin}${url}`);
                // Use toast instead of alert to avoid viewport jumps
                if (wayfarer.showToast) {
                    wayfarer.showToast('success', 'URL copied to clipboard!');
                } else {
                    wayfarer.showAlert('success', 'URL copied to clipboard!');
                }
            } catch (err) {
                if (wayfarer.showToast) {
                    wayfarer.showToast('danger', 'Failed to copy URL.');
                } else {
                    wayfarer.showAlert('danger', 'Failed to copy URL.');
                }
            }
        });
        document.querySelectorAll('.btn-trip-delete')
            .forEach(btn => btn.addEventListener('click', () => handleDeleteClick(btn)));
    });
})();
