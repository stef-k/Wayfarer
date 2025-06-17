// segmentHandlers.js
// Handles create/edit/delete for segments (travel legs)
import { clearMappingContext, setMappingContext } from './mappingContext.js';

export const initSegmentHandlers = () => {
    bindSegmentActions();
};

const bindSegmentActions = () => {
    /* ---------- edit ---------- */
    document.querySelectorAll('.btn-edit-segment').forEach(btn => {
        btn.onclick = async () => {
            const segmentId = btn.dataset.segmentId;
            if (!segmentId || segmentId === 'undefined') return;

            const resp = await fetch(`/User/Segments/Edit/${segmentId}`);
            const html = await resp.text();

            const wrapper = document.getElementById(`segment-item-${segmentId}`);
            wrapper.outerHTML = html;
            bindSegmentFormHandlers();
        };
    });

    /* ---------- save ---------- */
    document.querySelectorAll('.btn-segment-save').forEach(btn => {
        btn.onclick = async () => {
            const segmentId = btn.dataset.segmentId;
            const formEl    = document.getElementById(`segment-form-${segmentId}`);
            const fd        = new FormData(formEl);

            const resp = await fetch(`/User/Segments/Create`, {
                method:  'POST',
                body:    fd,
                headers: { 'X-CSRF-TOKEN': fd.get('__RequestVerificationToken') }
            });

            const html    = await resp.text();
            const wrapper = formEl.closest('.accordion-item');
            wrapper.outerHTML = html;
            bindSegmentFormHandlers();
        };
    });

    /* ---------- delete ---------- */
    document.querySelectorAll('.btn-delete-segment').forEach(btn => {
        btn.onclick = () => {
            const segmentId = btn.dataset.segmentId;
            if (!segmentId || segmentId === 'undefined') return;

            showConfirmationModal({
                title:       'Delete segment?',
                message:     'This action cannot be undone.',
                confirmText: 'Delete',
                onConfirm: async () => {
                    const resp = await fetch(`/User/Segments/Delete/${segmentId}`, { method: 'POST' });
                    if (resp.ok) {
                        document.getElementById(`segment-item-${segmentId}`)?.remove();
                        clearMappingContext();
                    } else {
                        showAlert('danger', 'Failed to delete segment.');
                    }
                }
            });
        };
    });

    /* ---------- segment list (select) ---------- */
    document.querySelectorAll('.segment-list-item').forEach(item => {
        item.onclick = () => {
            const segmentId = item.dataset.segmentId;
            const name      = item.dataset.segmentName || 'Unnamed segment';
            if (!segmentId) return;

            document.querySelectorAll('.segment-list-item').forEach(i =>
                i.classList.remove('bg-info-subtle', 'bg-info-soft')
            );
            item.classList.add('bg-info-subtle');

            setMappingContext({
                type:   'segment',
                id:     segmentId,
                action: 'edit',
                meta:   { name }
            });
        };
    });
};

export const loadSegmentCreateForm = async (tripId) => {
    if (!tripId) return;

    // 1) fetch the blank form
    const resp = await fetch(`/User/Segments/Create?tripId=${tripId}`);
    const html = await resp.text();

    // 2) append it to the list (one form max)
    const list = document.getElementById('segments-list');
    if (!list) return;

    // remove any stray, unsaved create-forms so we never stack them
    list.querySelectorAll('form[id^="segment-form-"]').forEach(f =>
        f.closest('.accordion-item')?.remove()
    );

    list.insertAdjacentHTML('beforeend', html);

    // 3) bind buttons inside the freshly injected DOM
    bindSegmentActions();       // save / delete / select
    bindSegmentFormHandlers();  // cancel
};


/* ------------------------------------------------------------------ */
/*  Bind form-level events once the edit form is injected              */
/* ------------------------------------------------------------------ */
const bindSegmentFormHandlers = () => {
    document.querySelectorAll('.btn-segment-cancel').forEach(btn => {
        btn.onclick = () => {
            const segmentId = btn.dataset.segmentId;
            if (!segmentId) return;

            // reload original panel
            (async () => {
                const resp    = await fetch(`/User/Segments/GetItemPartial?segmentId=${segmentId}`);
                const html    = await resp.text();
                const wrapper = document.getElementById(`segment-item-${segmentId}`);
                wrapper.outerHTML = html;
                bindSegmentActions();
                clearMappingContext();
            })();
        };
    });
};
