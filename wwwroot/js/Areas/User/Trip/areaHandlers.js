// added: areaHandlers.js – manages Area (polygon) UI and interactions
// Mirrors structure of regionHandlers.js and placeHandlers.js

import { initAreaMap, renderAreaPolygon, removeAreaPolygon } from './mapManager.js';
import { store } from './storeInstance.js';
import { setupQuill, waitForQuill } from './quillNotes.js';

let cachedTripId = null;

/**
 * Initializes all area-related handlers for the given trip.
 * @param {string} tripId
 */
export const initAreaHandlers = (tripId) => {
    cachedTripId = tripId;

    // Add Area buttons
    document.querySelectorAll('.btn-add-area').forEach(btn => {
        if (btn.dataset.bound) return;
        btn.dataset.bound = '1';
        btn.onclick = () => handleAddArea(btn.dataset.regionId);
    });

    // Edit Area buttons
    document.querySelectorAll('.btn-edit-area').forEach(btn => {
        if (btn.dataset.bound) return;
        btn.dataset.bound = '1';
        btn.onclick = () => handleEditArea(btn.dataset.areaId);
    });

    // Delete Area buttons
    document.querySelectorAll('.btn-delete-area').forEach(btn => {
        if (btn.dataset.bound) return;
        btn.dataset.bound = '1';
        btn.onclick = () => handleDeleteArea(btn.dataset.areaId);
    });

    // Area list-item click: set context and highlight on map
    document.querySelectorAll('.area-list-item').forEach(item => {
        if (item.dataset.bound) return;
        item.dataset.bound = '1';
        item.onclick = () => {
            const areaId = item.dataset.areaId;
            const name = item.querySelector('.area-name')?.textContent.trim() || 'Unnamed Area';
            if (!areaId) return;

            // Dim siblings
            document.querySelectorAll('.area-list-item').forEach(i => i.classList.add('dimmed'));
            item.classList.remove('dimmed');

            store.dispatch('set-context', {
                type: 'area',
                id: areaId,
                action: 'edit',
                meta: { name }
            });

            // Highlight on map
            renderAreaPolygon({ Id: areaId });
        };
    });

    attachAreaFormHandlers();
};

/**
 * Handles adding a new Area form to a Region.
 * @param {string} regionId
 */
const handleAddArea = async (regionId) => {
    store.dispatch('trip-cleanup-open-forms');
    const resp = await fetch(`/User/Areas/CreateOrUpdate?regionId=${regionId}`);
    const html = await resp.text();

    const regionEl = document.getElementById(`region-item-${regionId}`);
    const container = regionEl?.querySelector('[data-region-areas]');
    if (container) {
        container.insertAdjacentHTML('afterbegin', html);
    }

    // Enhance new form
    const formEl = container.querySelector('form[id^="area-form-"]');
    if (formEl) await enhanceAreaForm(formEl);

    // Rebind handlers
    initAreaHandlers(cachedTripId);
};

/**
 * Handles editing an existing Area.
 * @param {string} areaId
 */
const handleEditArea = async (areaId) => {
    store.dispatch('trip-cleanup-open-forms');
    const resp = await fetch(`/User/Areas/Edit/${areaId}`);
    const html = await resp.text();

    const item = document.querySelector(`.area-list-item[data-area-id="${areaId}"]`);
    if (item) item.outerHTML = html;

    const formEl = document.getElementById(`area-form-${areaId}`);
    if (formEl) await enhanceAreaForm(formEl);

    initAreaHandlers(cachedTripId);
};

/**
 * Handles deleting an Area after user confirmation.
 * @param {string} areaId
 */
const handleDeleteArea = (areaId) => {
    wayfarer.showConfirmationModal({
        title: 'Delete Area?',
        message: 'This action cannot be undone.',
        confirmText: 'Delete',
        onConfirm: async () => {
            const fd = new FormData();
            fd.set('__RequestVerificationToken', document.querySelector('input[name="__RequestVerificationToken"]').value);

            const resp = await fetch(`/User/Areas/Delete/${areaId}`, {
                method: 'POST',
                body: fd
            });

            if (resp.ok) {
                document.querySelector(`.area-list-item[data-area-id="${areaId}"]`)?.remove();
                removeAreaPolygon(areaId);
                store.dispatch('clear-context');
                wayfarer.showAlert('success', 'Area deleted.');
            } else {
                wayfarer.showAlert('danger', 'Failed to delete area.');
            }
        }
    });
};

/**
 * Attaches handlers for cancel & save actions inside Area forms.
 */
const attachAreaFormHandlers = () => {
    // Cancel button
    document.querySelectorAll('.btn-area-cancel').forEach(btn => {
        if (btn.dataset.bound) return;
        btn.dataset.bound = '1';
        btn.onclick = async () => {
            const areaId   = btn.dataset.areaId;
            const regionId = btn.dataset.regionId;
            const formEl   = document.getElementById(`area-form-${areaId}`);

            // New (unsaved) areas come back with Guid.Empty
            const isNew = areaId === '00000000-0000-0000-0000-000000000000';
            if (isNew) {
                // brand-new Area → just drop the form
                formEl.remove();
                store.dispatch('clear-context');
                return;
            }

            // Existing Area → reload its parent region
            const resp = await fetch(`/User/Regions/GetItemPartial?regionId=${regionId}`);
            const html = await resp.text();
            document.getElementById(`region-item-${regionId}`).outerHTML = html;
            store.dispatch('region-dom-reloaded', { regionId });
            store.dispatch('clear-context');
        };
    });
    
    // Save button (click)
    document.querySelectorAll('.btn-area-save').forEach(btn => {
        if (btn.dataset.bound) return;
        btn.dataset.bound = '1';
        btn.onclick = async () => {
            const areaId   = btn.dataset.areaId;
            const regionId = btn.dataset.regionId;
            const formEl   = document.getElementById(`area-form-${areaId}`);
            const fd       = new FormData(formEl);
            const token    = fd.get('__RequestVerificationToken');

            const resp = await fetch('/User/Areas/CreateOrUpdate', {
                method: 'POST',
                credentials: 'same-origin',
                body: fd,
                headers: token
                    ? { 'RequestVerificationToken': token }
                    : {}
            });

            const html = await resp.text();
            if (!resp.ok) {
                // parse out server-side validation errors
                const dom = new DOMParser().parseFromString(html, 'text/html');
                const errs = Array.from(dom.querySelectorAll('.area-form-errors ul li'))
                    .map(li => li.textContent.trim());
                wayfarer.showAlert('danger', errs.join('\\n'));
                return;
            }

            // Success: replace entire region accordion
            document.getElementById(`region-item-${regionId}`).outerHTML = html;
            store.dispatch('region-dom-reloaded', { regionId });
            removeAreaPolygon(areaId);
            // re-draw the updated polygon from its new data-attrs
            const el = document.querySelector(`.area-list-item[data-area-id="${areaId}"]`);
            if (el) {
                renderAreaPolygon({
                    Id: areaId,
                    Geometry: JSON.parse(el.dataset.areaGeom),
                    FillHex:   el.dataset.areaFill
                });
            }
            store.dispatch('clear-context');
        };
    });
};

/**
 * Enhances an Area form with Quill and map drawing tools.
 * @param {HTMLFormElement} formEl
 */
export const enhanceAreaForm = async (formEl) => {
    const areaId = formEl.id.replace('area-form-', '');
    // Quill
    try {
        await waitForQuill(`#area-notes-${areaId}`);
        await setupQuill(
            `#area-notes-${areaId}`,
            `#Notes-${areaId}`,
            `#area-form-${areaId}`
        );
    } catch (err) {
        console.error(`❌ Failed to init Quill for area ${areaId}:`, err);
    }
    // Map draw
    const geom = JSON.parse(formEl.querySelector('[name="Geometry"]').value || 'null');
    const fill = formEl.querySelector('[name="FillHex"]').value;
    initAreaMap(areaId, geom, fill);
};

// Rebind handlers after region reload
store.subscribe(({ type, payload }) => {
    if (type === 'region-dom-reloaded') {
        initAreaHandlers(cachedTripId);
    }
});
