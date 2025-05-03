// index.js
document.addEventListener('DOMContentLoaded', () => {
    // SSE stream
    const stream = new EventSource(`/api/sse/stream/import/${userId}`);
    // grab the antiforgery token
    const antiForgeryToken = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    const cfg = window.__locationImportConfig;

    stream.onmessage = (event) => {
        handleStream(event);
    }
    
    // helper to POST a tiny form
    function postForm(actionUrl, payload) {
        const form = document.createElement('form');
        form.method = 'post';
        form.action = actionUrl;

        // antiforgery
        const af = document.createElement('input');
        af.type  = 'hidden';
        af.name  = '__RequestVerificationToken';
        af.value = antiForgeryToken;
        form.appendChild(af);

        // any other fields
        Object.entries(payload).forEach(([key, val]) => {
            const inp = document.createElement('input');
            inp.type  = 'hidden';
            inp.name  = key;
            inp.value = val;
            form.appendChild(inp);
        });

        document.body.appendChild(form);
        form.submit();
    }

    // START / STOP buttons
    document.querySelectorAll('.js-import-action').forEach(btn => {
        btn.addEventListener('click', () => {
            const action = btn.dataset.action; // "start" or "stop"
            const id     = btn.dataset.id;
            const isStart = action === 'start';

            showConfirmationModal({
                title:       isStart ? 'Start Import' : 'Stop Import',
                message:     isStart
                    ? 'Are you sure you want to start this import job?'
                    : 'Are you sure you want to stop this import job?',
                confirmText: isStart ? 'Start Import' : 'Stop Import',
                onConfirm:   () => postForm(
                    isStart ? cfg.startImportUrl : cfg.stopImportUrl,
                    { id }
                )
            });
        });
    });

    // DELETE / CANCEL links
    document.querySelectorAll('.js-delete-import').forEach(link => {
        link.addEventListener('click', e => {
            e.preventDefault();
            const id     = link.dataset.id;
            const status = link.dataset.status;
            const isPending = status === 'Pending';

            showConfirmationModal({
                title:       isPending ? 'Cancel Import' : 'Remove Import',
                message:     isPending
                    ? 'Are you sure you want to cancel this import?'
                    : 'Are you sure you want to remove this import? This cannot be undone.',
                confirmText: isPending ? 'Cancel Import' : 'Remove Import',
                onConfirm:   () => postForm(cfg.deleteUrl, {
                    id,
                    status: isPending ? 'Pending' : status
                })
            });
        });
    });
});

/**
 * Parses the incoming Server Send Event payload and updates the correct row with the updated data.
 * @param event
 */
const handleStream = (event) => {
    const payload = JSON.parse(event.data);
    updateRow(payload.FilePath, payload.LastProcessedIndex, payload.LastImportedRecord);
}

/**
 * Update the row in #locationImport matching filePathValue.
 *
 * @param {string} filePathValue        The exact text of the .filePath cell to match
 * @param {string|number} lastProcessedIndex      New value for .lastProcessedIndex
 * @param {string|number} lastImportedRecord     New value for .lastImportedRecord
 */
const updateRow = (filePathValue, lastProcessedIndex, lastImportedRecord) => {
    // Get all the filePath cells in the table
    const filePathCells = document.querySelectorAll(
        '#locationImport tbody .filePath'
    );

    for (let cell of filePathCells) {
        // Trim whitespace in case of accidental padding
        if (cell.textContent.trim() === filePathValue) {
            // Found the matching row
            const row = cell.closest('tr');
            if (!row) return;  // sanity check

            // Update the two target cells
            const idxCell = row.querySelector('.lastProcessedIndex');
            const recCell = row.querySelector('.lastImportedRecord');
            const totalRecords = idxCell.dataset.totalRecords;
            
            if (idxCell) idxCell.textContent = `${lastProcessedIndex} of  ${totalRecords}`;
            if (recCell) recCell.textContent = lastImportedRecord;

            return;  // done!
        }
    }

    console.warn(`No row found for filePath="${filePathValue}"`);
};