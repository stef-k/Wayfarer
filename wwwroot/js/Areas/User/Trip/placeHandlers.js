// placeHandlers.js
// Handles create/edit/delete of places within a region
import {setMappingContext, getMappingContext, clearMappingContext} from './mappingContext.js';
import {
    renderPlaceMarker,
    removePlaceMarker,
    getMapInstance,
    getPlaceMarkerById, clearSelectedMarker, selectMarker
} from './mapManager.js';
import {populateIconDropdown, populateColorDropdown, updateDropdownIconColors} from './uiCore.js';

export const initPlaceHandlers = () => {
    attachPlaceFormHandlers();
};

export const enhancePlaceForm = async (formEl) => {
    const placeId = formEl.dataset?.placeId || formEl.id.replace('place-form-', '');
    const selector = `#place-notes-${placeId}`;
    const inputSelector = `#Notes-${placeId}`;
    const formSelector = `#place-form-${placeId}`;

    try {
        const {setupQuill, waitForQuill} = await import('./quillNotes.js');
        await waitForQuill(selector);
        await setupQuill(selector, inputSelector, formSelector);
    } catch (err) {
        console.warn(`❌ Failed to init Quill for place ${placeId}:`, err);
    }
    const iconMenu = formEl.querySelector('.icon-dropdown-menu');
    if (iconMenu) {
        await populateIconDropdown(iconMenu);
    } else {
        console.warn('⚠️ No icon menu found in form');
    }

    await populateColorDropdown(formEl);

    // ✅ Keep dropdown previews in sync
    const currentColor = formEl.querySelector('input[name="MarkerColor"]')?.value || 'bg-blue';
    updateDropdownIconColors(formEl, currentColor);
};


const attachPlaceFormHandlers = () => {
    /* ---------- cancel ---------- */
    document.querySelectorAll('.btn-place-cancel').forEach(btn => {
        btn.onclick = async () => {
            const placeId = btn.dataset.placeId;
            const regionId = btn.dataset.regionId;
            if (!placeId) return;

            const regionEl = document.getElementById(`region-item-${regionId}`);
            const resp = await fetch(`/User/Regions/GetItemPartial?regionId=${regionId}`);
            regionEl.outerHTML = await resp.text();

            document.dispatchEvent(new CustomEvent('region-dom-reloaded', {detail: {regionId}}));
            removePlaceMarker(placeId);

            // ✅ Restore view mode and context
            const item = document.querySelector(`.place-list-item[data-place-id="${placeId}"]`);
            const name = item?.querySelector('.place-name')?.innerText || 'Unnamed';
            const lat = item?.dataset.placeLat;
            const lon = item?.dataset.placeLon;
            const icon = item?.dataset.placeIcon;
            const color = item?.dataset.placeColor;

            if (lat && lon) {
                renderPlaceMarker?.({
                    Id: placeId,
                    Name: name,
                    Latitude: lat,
                    Longitude: lon,
                    IconName: icon,
                    MarkerColor: color,
                    RegionId: regionId
                });
            }

            setMappingContext({
                type: 'place',
                id: placeId,
                action: 'set-location',
                meta: {name, regionId}
            });

            document.dispatchEvent(new CustomEvent('place-context-selected', {
                detail: {placeId, regionId, name}
            }));
        };
    });


    /* ---------- edit ---------- */
    document.querySelectorAll('.btn-edit-place').forEach(btn => {
        btn.onclick = async () => {
            const placeId = btn.dataset.placeId;
            const regionId = btn.dataset.regionId;
            if (!placeId || !regionId) return;

            const resp = await fetch(`/User/Places/Edit/${placeId}`);
            const html = await resp.text();

            const item = document.querySelector(`.place-list-item[data-place-id="${placeId}"]`);
            if (!item) return;

            item.outerHTML = html;

            const formEl = document.getElementById(`place-form-${placeId}`);
            if (formEl) await enhancePlaceForm(formEl);
            attachPlaceFormHandlers();

            const name = formEl.querySelector('input[name="Name"]')?.value || 'Unnamed';

            setMappingContext({
                type: 'place',
                id: placeId,
                action: 'edit',
                meta: {name, regionId}
            });
        };
    });

    /* ---------- save ---------- */
    document.querySelectorAll('.btn-place-save').forEach(btn => {
        btn.onclick = async () => {
            const placeId = btn.dataset.placeId;
            const regionId = btn.dataset.regionId;
            const formEl = document.getElementById(`place-form-${placeId}`);

            const fd = new FormData(formEl);
            const token = fd.get('__RequestVerificationToken');

            const resp = await fetch('/User/Places/CreateOrUpdate', {
                method: 'POST',
                body: fd,
                credentials: 'same-origin',
                headers: token ? {RequestVerificationToken: token} : {}
            });

            const html = await resp.text();
            const wrapper = formEl.closest('.accordion-item');
            if (resp.ok) {
                wrapper.outerHTML = html;
                attachPlaceFormHandlers();

                // Re-render marker for the *currently active* place, if any
                const context = getMappingContext();
                if (context?.type === 'place' && context.id === placeId) {
                    const el = document.querySelector(`.place-list-item[data-place-id="${placeId}"]`);
                    const lat = el?.dataset.placeLat;
                    const lon = el?.dataset.placeLon;
                    const icon = el?.dataset.placeIcon;
                    const color = el?.dataset.placeColor;

                    if (lat && lon) {
                        const name = el.querySelector('.place-name')?.innerText || 'Unnamed';
                        renderPlaceMarker?.({
                            Id: placeId,
                            Name: name,
                            Latitude: lat,
                            Longitude: lon,
                            IconName: icon,
                            MarkerColor: color,
                            RegionId: regionId
                        });
                    }
                }
            } else {
                wrapper.outerHTML = html;
                attachPlaceFormHandlers();

                const dom = new DOMParser().parseFromString(html, 'text/html');
                const errorsBlock = dom.querySelector('.place-form-errors ul');
                const errors = [...errorsBlock?.querySelectorAll('li') || []].map(li => li.textContent.trim());
                showAlert('danger', errors.join('\n'));
            }
        };
    });

    /* ---------- place list (select) ---------- */
    document.querySelectorAll('.place-list-item').forEach(li => {
        li.onclick = () => {
            const placeId = li.dataset.placeId;
            const regionId = li.dataset.regionId;
            if (!placeId || !regionId) return;

            const lat = li.dataset.placeLat;
            const lon = li.dataset.placeLon;
            const icon = li.dataset.placeIcon;
            const color = li.dataset.placeColor;
            const name = li.querySelector('.place-name')?.innerText || 'Unnamed';

            document.querySelectorAll('.place-list-item').forEach(i =>
                i.classList.remove('bg-info-subtle', 'bg-info-soft')
            );
            li.classList.add('bg-info-subtle');

            setMappingContext({
                type: 'place',
                id: placeId,
                action: 'set-location',
                meta: {name, regionId}
            });

            if (lat && lon) {
                renderPlaceMarker?.({
                    Id: placeId,
                    Name: name,
                    Latitude: lat,
                    Longitude: lon,
                    IconName: icon,
                    MarkerColor: color,
                    RegionId: regionId
                });
                const map = getMapInstance();
                if (map) map.setView([lat, lon], Math.max(map.getZoom(), 10));
                clearSelectedMarker();
                const marker = getPlaceMarkerById(placeId);
                if (marker) selectMarker(marker);
            }
        };
    });

    /* ---------- delete ---------- */
    document.querySelectorAll('.btn-delete-place').forEach(btn => {
        btn.onclick = () => {
            const {placeId, regionId} = btn.dataset;
            if (!placeId) return;

            showConfirmationModal({
                title: 'Delete place?',
                message: 'This action cannot be undone.',
                confirmText: 'Delete',
                onConfirm: async () => {
                    const fd = new FormData();
                    fd.set('__RequestVerificationToken',
                        document.querySelector('input[name="__RequestVerificationToken"]').value);

                    const resp = await fetch(`/User/Places/Delete/${placeId}`, {
                        method: 'POST',
                        body: fd,
                        headers: {RequestVerificationToken: fd.get('__RequestVerificationToken')}
                    });

                    if (resp.ok) {
                        const regionEl = document.getElementById(`region-item-${regionId}`);
                        const html = await resp.text();
                        regionEl.outerHTML = html;
                        document.dispatchEvent(new CustomEvent('region-dom-reloaded', {detail: {regionId}}));
                        removePlaceMarker(placeId);
                        clearMappingContext();
                    } else {
                        showAlert('danger', 'Failed to delete place.');
                    }
                }
            });
        };
    });
};

/* ------------------------------------------------------------------ */
/*  When the region container is re-rendered, re-bind its new DOM      */
/* ------------------------------------------------------------------ */
document.addEventListener('region-dom-reloaded', e => {
    (async () => {
        const {regionId} = e.detail;
        const regionEl = document.getElementById(`region-item-${regionId}`);

        // Auto-expand the accordion panel that just changed
        regionEl?.querySelector('.accordion-button')?.classList.remove('collapsed');
        regionEl?.querySelector('.accordion-collapse')?.classList.add('show');

        attachPlaceFormHandlers();
    })();
});

document.addEventListener('mapping-context-cleared', () => {
    clearSelectedMarker?.();

    // ✅ Do NOT invoke .btn-place-cancel here
    // Instead just close any open edit form by reloading region DOM without restoring selection

    const openPlaceForm = document.querySelector('form[id^="place-form-"]');
    const placeId = openPlaceForm?.querySelector('[name="Id"]')?.value;
    const regionId = openPlaceForm?.querySelector('[name="RegionId"]')?.value;

    if (placeId && regionId) {
        (async () => {
            const regionEl = document.getElementById(`region-item-${regionId}`);
            if (!regionEl) return;
            const resp = await fetch(`/User/Regions/GetItemPartial?regionId=${regionId}`);
            regionEl.outerHTML = await resp.text();

            document.dispatchEvent(new CustomEvent('region-dom-reloaded', { detail: { regionId } }));
            removePlaceMarker(placeId);
        })();
    }
});







