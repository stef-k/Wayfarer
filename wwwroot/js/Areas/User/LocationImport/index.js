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
    updateRow(payload.FilePath, payload.LastProcessedIndex, payload.LastImportedRecord, payload.TotalRecords, payload.Status, payload.ErrorMessage);
}

/**
 * Update the row in #locationImport matching filePathValue.
 *
 * @param {string} filePathValue        The exact text of the .filePath cell to match
 * @param {string|number} lastProcessedIndex      New value for .lastProcessedIndex
 * @param {string|number} lastImportedRecord     New value for .lastImportedRecord
 * @param {string|number} total
 * @param {string|number} Status     New value for .importStatus
 * @param {string|number} ErrorMessage     New value for .errorMessage
 */
const updateRow = (filePathValue, lastProcessedIndex, lastImportedRecord, total, Status, ErrorMessage) => {
    const filePathCells = document.querySelectorAll('#locationImport tbody .filePath');

    for (let cell of filePathCells) {
        if (cell.textContent.trim() === filePathValue) {
            const row = cell.closest('tr');
            if (!row) return;

            const idxCell = row.querySelector('.lastProcessedIndex');
            const recCell = row.querySelector('.lastImportedRecord');
            const importStatusCell = row.querySelector('.importStatus');
            const errorMessageCell = row.querySelector('.errorMessage');

            if (idxCell) idxCell.textContent = `${lastProcessedIndex} of ${total}`;
            if (recCell) recCell.textContent = lastImportedRecord;
            if (errorMessageCell) errorMessageCell.textContent = ErrorMessage ?? "";

            // 🟡 Normalize status value (handle ImportStatus object)
            const statusString = typeof Status === 'object' && Status !== null && 'Value' in Status
                ? Status.Value
                : String(Status);

            // 🟡 Update badge
            const statusClasses = {
                'Stopped':     'bg-dark',
                'Completed':   'bg-success',
                'Failed':      'bg-danger',
                'In Progress': 'bg-info',
                'Stopping':    'bg-secondary'
            };

            const badgeClass = statusClasses[statusString] || 'bg-light text-dark';
            if (importStatusCell) {
                importStatusCell.innerHTML = `<span class="badge ${badgeClass}">${statusString}</span>`;
            }

            // 🔵 Update action buttons
            const primaryBtn = row.querySelector('.js-import-action');
            const dropdownToggle = row.querySelector('.dropdown-toggle');
            const dropdownDelete = row.querySelector('.dropdown-menu .js-delete-import');

            if (primaryBtn) {
                if (statusString === 'Stopped' || statusString === 'Failed') {
                    primaryBtn.textContent = 'Start';
                    primaryBtn.dataset.action = 'start';
                    primaryBtn.disabled = false;
                } else if (statusString === 'In Progress') {
                    primaryBtn.textContent = 'Stop';
                    primaryBtn.dataset.action = 'stop';
                    primaryBtn.disabled = false;
                } else if (statusString === 'Stopping') {
                    primaryBtn.textContent = 'Stopping…';
                    primaryBtn.removeAttribute('data-action');
                    primaryBtn.disabled = true;
                } else if (statusString === 'Completed') {
                    primaryBtn.textContent = '';
                    primaryBtn.removeAttribute('data-action');
                    primaryBtn.disabled = true;
                }
            }

            // 🔵 Show/hide dropdown for applicable statuses
            const shouldShowDropdown = (
                statusString === 'Stopped' ||
                statusString === 'Failed' ||
                statusString === 'In Progress' ||
                statusString === 'Completed'
            );

            if (dropdownToggle) {
                dropdownToggle.style.display = shouldShowDropdown ? '' : 'none';
            }

            // 🔵 Optional: update data-status in delete link
            if (dropdownDelete) {
                dropdownDelete.dataset.status = statusString;
            }

            return;
        }
    }

    console.warn(`No row found for filePath="${filePathValue}"`);
};

