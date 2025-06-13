// segmentHandlers.js
// Handles create/edit/delete for segments (travel legs)
import { setMappingContext } from './mappingContext.js';

export const initSegmentHandlers = () => {
    bindSegmentActions();
};

const bindSegmentActions = () => {
    document.querySelectorAll('.btn-edit-segment').forEach(btn => {
        btn.addEventListener('click', async () => {
            const segmentId = btn.dataset.segmentId;
            if (!segmentId || segmentId === 'undefined') return;

            const resp = await fetch(`/User/Segments/Edit/${segmentId}`);
            const html = await resp.text();
            const item = document.getElementById(`segment-item-${segmentId}`);
            item.outerHTML = html;
            bindSegmentFormHandlers();
        });
    });

    document.querySelectorAll('.btn-delete-segment').forEach(btn => {
        btn.addEventListener('click', () => {
            const segmentId = btn.dataset.segmentId;
            if (!segmentId || segmentId === 'undefined') return;

            showConfirmationModal({
                title: 'Delete Segment?',
                message: 'Are you sure you want to permanently delete this segment?',
                confirmText: 'Delete',
                onConfirm: async () => {
                    const fd = new FormData();
                    fd.set('__RequestVerificationToken', document.querySelector('input[name="__RequestVerificationToken"]').value);

                    const resp = await fetch(`/User/Segments/Delete/${segmentId}`, {
                        method: 'POST',
                        body: fd
                    });

                    const html = await resp.text();

                    if (resp.ok) {
                        document.getElementById('segments-list').innerHTML = html;
                        bindSegmentActions(); // rebind for refreshed DOM
                        showAlert('success', 'Segment deleted.');
                    } else {
                        showAlert('danger', 'Failed to delete segment.');
                    }
                }
            });
        });
    });

    document.querySelectorAll('.segment-list-item').forEach(item => {
        item.addEventListener('click', () => {
            const segmentId = item.dataset.segmentId;
            const name = item.dataset.segmentName || 'Unnamed Segment';
            if (!segmentId) return;

            // Clear all highlights
            document.querySelectorAll('.segment-list-item').forEach(el =>
                el.classList.remove('bg-success-subtle')
            );

            // Highlight this one
            item.classList.add('bg-success-subtle');

            // Update context
            setMappingContext({ type: 'segment', id: segmentId, action: 'trace-route', meta: { name } });

        });
    });
    
    bindSegmentFormHandlers();
};

const bindSegmentFormHandlers = () => {
    document.querySelectorAll('.btn-segment-cancel').forEach(btn => {
        btn.onclick = async () => {
            const segmentId = btn.dataset.segmentId;
            const resp = await fetch(`/User/Segments/Edit/${segmentId}`);
            const html = await resp.text();
            const item = document.getElementById(`segment-item-${segmentId}`);
            item.outerHTML = html;
            bindSegmentActions(); // rebind
        };
    });

    document.querySelectorAll('.btn-segment-save').forEach(btn => {
        btn.onclick = async () => {
            const segmentId = btn.dataset.segmentId;
            const formEl = document.getElementById(`segment-form-${segmentId}`);
            const fd = new FormData(formEl);

            const resp = await fetch(`/User/Segments/Create`, {
                method: 'POST',
                body: fd,
                headers: { 'X-CSRF-TOKEN': fd.get('__RequestVerificationToken') }
            });

            const html = await resp.text();
            const wrapper = formEl.closest('form');

            if (resp.ok) {
                document.getElementById('segments-list').innerHTML = html;
                bindSegmentActions(); // rebind
            } else {
                wrapper.outerHTML = html;
                bindSegmentFormHandlers();

                const dom = new DOMParser().parseFromString(html, 'text/html');
                const errorsBlock = dom.querySelector('.segment-form-errors ul');
                if (errorsBlock) {
                    const errors = Array.from(errorsBlock.querySelectorAll('li')).map(li => li.textContent.trim());
                    showAlert('danger', errors.join('\n'));
                }
            }
        };
    });
};

export const loadSegmentCreateForm = async (tripId) => {
    const resp = await fetch(`/User/Segments/Create?tripId=${tripId}`);
    const html = await resp.text();

    const container = document.getElementById('segments-list');
    container.insertAdjacentHTML('beforeend', html);

    bindSegmentFormHandlers();
};
