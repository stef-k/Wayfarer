// regionHandlers.js – create / edit / delete regions

import {
    clearMappingContext,
    setMappingContext
} from './mappingContext.js';

import {
    initPlaceHandlers,
    enhancePlaceForm
} from './placeHandlers.js';

import {
    clearSelectedMarker,
    getMapInstance, getRegionMarkerById,
    removeRegionMarker, selectMarker
} from './mapManager.js';

export const initRegionHandlers = (tripId) => {
    /* ---------- add region ---------- */
    const addBtn = document.getElementById('btn-add-region');
    if (addBtn) addBtn.onclick = async () => {
        if (!tripId) return;

        const resp = await fetch(`/User/Regions/CreateOrUpdate?tripId=${tripId}`);
        const html = await resp.text();

        const container = document.getElementById('regions-accordion');
        container.insertAdjacentHTML('beforeend', html);

        const newRegionForm = container.lastElementChild;
        const newRegionId   = newRegionForm.id.replace('region-form-', '');

        newRegionForm.classList.add('bg-info-subtle');   // highlight

        setMappingContext({
            type:   'region',
            id:     newRegionId,
            action: 'set-center',
            meta:   { name: 'New region' }
        });

        /* re-bind fresh DOM */
        initRegionHandlers(tripId);
        initPlaceHandlers();
        attachRegionFormHandlers();
    };

    /* other buttons … */
    document.querySelectorAll('.btn-edit-region')
        .forEach(btn => btn.onclick = () => handleEditRegion(btn.dataset.regionId, tripId));

    document.querySelectorAll('.btn-delete-region')
        .forEach(btn => btn.onclick = () => handleDeleteRegion(btn.dataset.regionId));

    document.querySelectorAll('.btn-add-place')
        .forEach(btn => btn.onclick = () => handleAddPlace(btn.dataset.regionId));

    /* select region (centre on map) */
    document.querySelectorAll('.region-select-area').forEach(btn => {
        btn.onclick = (e) => {
            e.preventDefault();
            e.stopPropagation();

            const wrapper = btn.closest('.accordion-item');
            const latStr  = wrapper?.dataset.centerLat;
            const lonStr  = wrapper?.dataset.centerLon;
            const lat = parseFloat(latStr);
            const lon = parseFloat(lonStr);
            if (!isNaN(lat) && !isNaN(lon)) {
                getMapInstance()?.setView([lat, lon], 8);
            }

            const regionId   = btn.dataset.regionId;
            const regionName = btn.dataset.regionName || 'Unnamed Region';
            if (!regionId) return;

            document.querySelectorAll('.accordion-item')
                .forEach(i => i.classList.remove('bg-info-subtle', 'bg-info-soft'));

            wrapper?.classList.add('bg-info-subtle');

            setMappingContext({
                type:   'region',
                id:     regionId,
                action: 'set-center',
                meta:   { name: regionName }
            });
            clearSelectedMarker();
            const marker = getRegionMarkerById(regionId);
            if (marker) selectMarker(marker);
        };
    });
};

/* ------------------------------------------------------------------ *
 *  edit / delete / add place helpers (unchanged except delete)
 * ------------------------------------------------------------------ */
const handleDeleteRegion = (regionId) => {
    showConfirmationModal({
        title: 'Delete Region?',
        message: 'Are you sure you want to permanently delete this region and all its places?',
        confirmText: 'Delete',
        onConfirm: async () => {
            const fd = new FormData();
            fd.set('__RequestVerificationToken',
                document.querySelector('input[name="__RequestVerificationToken"]').value);

            const resp = await fetch(`/User/Regions/Delete/${regionId}`, {
                method: 'POST',
                body: fd
            });

            if (resp.ok) {
                document.getElementById(`region-item-${regionId}`)?.remove();
                removeRegionMarker(regionId);          // ← ensure pin disappears
                clearMappingContext();
                showAlert('success', 'Region deleted.');
            } else {
                showAlert('danger', 'Failed to delete region.');
            }
        }
    });
};

const handleAddPlace = async (regionId) => {
    const resp = await fetch(`/User/Places/CreateOrUpdate?regionId=${regionId}`);
    const html = await resp.text();

    const regionItem = document.getElementById(`region-item-${regionId}`);
    regionItem.insertAdjacentHTML('beforeend', html);

    const formEl = regionItem.querySelector('[id^="place-form-"]');
    let newPlaceId = null;

    if (formEl) {
        await enhancePlaceForm(formEl);
        newPlaceId = formEl.id.replace('place-form-', '');
    }

    if (newPlaceId) {
        setMappingContext({
            type: 'place',
            id: newPlaceId,
            action: 'set-location',
            meta: { name: 'New place', regionId }
        });
    }

    document.dispatchEvent(new CustomEvent('region-dom-reloaded', { detail: { regionId } }));
};

const attachRegionFormHandlers = () => {
    document.querySelectorAll('.btn-region-cancel').forEach(btn => {
        btn.onclick = async () => {
            const regionId = btn.dataset.regionId;
            const wrapper  = btn.closest('.accordion-item');

            // brand-new / unsaved region → just drop it
            if (!regionId) {
                wrapper?.remove();
                clearMappingContext();
                return;
            }

            // existing region → reload the read-only partial
            const resp = await fetch(`/User/Regions/GetItemPartial?regionId=${regionId}`);
            wrapper.outerHTML = await resp.text();

            document.dispatchEvent(
                new CustomEvent('region-dom-reloaded', { detail: { regionId } })
            );
            clearMappingContext();
        };
    });

    document.querySelectorAll('.btn-region-save').forEach(btn => {
        btn.onclick = async () => {
            const regionId = btn.dataset.regionId;
            const formEl = document.getElementById(`region-form-${regionId}`);
            const fd = new FormData(formEl);

            const resp = await fetch(`/User/Regions/CreateOrUpdate`, {
                method: 'POST',
                body: fd,
                headers: { 'X-CSRF-TOKEN': fd.get('__RequestVerificationToken') }
            });

            const html = await resp.text();
            const wrapper = formEl.closest('.accordion-item, form');

            if (resp.ok) {
                wrapper.outerHTML = html;
                const newRegionId = wrapper?.id?.replace('region-item-', '');
                if (newRegionId) {
                    document.dispatchEvent(new CustomEvent('region-dom-reloaded', { detail: { regionId: newRegionId } }));
                }
                clearMappingContext();
            } else {
                wrapper.outerHTML = html;
                attachRegionFormHandlers();

                const dom = new DOMParser().parseFromString(html, 'text/html');
                const errorsBlock = dom.querySelector('.region-form-errors ul');
                if (errorsBlock) {
                    const errors = Array.from(errorsBlock.querySelectorAll('li')).map(li => li.textContent.trim());
                    showAlert('danger', errors.join('\n'));
                }
            }
        };
    });
};
document.addEventListener('mapping-context-changed', (e) => {
    const ctx = e.detail;
    if (ctx.type !== 'region' || ctx.action !== 'set-center') return;

    const el = document.getElementById(`region-item-${ctx.id}`);
    if (!el) return;

    el.scrollIntoView({ behavior: 'smooth', block: 'center' });

    document.querySelectorAll('.accordion-item')
        .forEach(i => i.classList.remove('bg-primary-subtle'));

    el.classList.add('bg-primary-subtle');
});
