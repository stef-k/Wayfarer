// regionHandlers.js
// Handles create/edit/delete of regions and triggers context updates

import {clearMappingContext, setMappingContext} from './mappingContext.js';
import {refreshTripDays} from './uiCore.js';
import {initPlaceHandlers} from './placeHandlers.js';

let activeDrawingRegionId = null;

export const initRegionHandlers = (tripId) => {

    document.getElementById('btn-add-region')?.addEventListener('click', async () => {
        if (!tripId) return;

        const resp = await fetch(`/User/Regions/Create?tripId=${tripId}`);
        const html = await resp.text();

        const container = document.getElementById('regions-accordion');
        container.insertAdjacentHTML('beforeend', html);

        initRegionHandlers(tripId); // Rebind region handlers for new form
        initPlaceHandlers();        // Rebind places if form includes place slots
        attachRegionFormHandlers(); // Attach the form handlers 
    });


    document.querySelectorAll('.btn-edit-region').forEach(btn => {
        btn.onclick = () => handleEditRegion(btn.dataset.regionId, tripId);
    });

    document.querySelectorAll('.btn-delete-region').forEach(btn => {
        btn.onclick = () => handleDeleteRegion(btn.dataset.regionId);
    });

    document.querySelectorAll('.btn-add-place').forEach(btn => {
        btn.onclick = () => handleAddPlace(btn.dataset.regionId);
    });

    document.querySelectorAll('.region-select-area').forEach(btn => {
        btn.addEventListener('click', (e) => {
            e.preventDefault();       // Prevent any link default
            e.stopPropagation();      // Prevent Bootstrap collapse

            const regionId = btn.dataset.regionId;
            const regionName = btn.dataset.regionName || 'Unnamed Region';
            if (!regionId) return;

            document.querySelectorAll('.accordion-item').forEach(item =>
                item.classList.remove('bg-info-subtle', 'bg-info-soft')
            );

            const regionPanel = btn.closest('.accordion-item');
            regionPanel?.classList.add('bg-info-subtle');

            setMappingContext({
                type: 'region',
                id: regionId,
                action: 'set-center',
                meta: {name: regionName}
            });
        });
    });

    document.querySelectorAll('.btn-edit-boundary').forEach(btn => {
        btn.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();

            const regionId = btn.dataset.regionId;
            const regionName = document.querySelector(`#region-item-${regionId} .accordion-button strong`)?.textContent || 'Unnamed Region';

            // Toggle logic
            if (activeDrawingRegionId === regionId) {
                clearMappingContext(); // cancels draw
                activeDrawingRegionId = null;
            } else {
                setMappingContext({
                    type: 'region',
                    id: regionId,
                    action: 'draw-boundary',
                    meta: {name: regionName}
                });
                activeDrawingRegionId = regionId;
            }
        });
    });

    document.querySelectorAll('.btn-draw-boundary').forEach(btn => {
        btn.addEventListener('click', () => {
            const regionId = btn.dataset.regionId;
            const regionName = btn.dataset.regionName;

            setMappingContext({
                type: 'region',
                id: regionId,
                action: 'draw-boundary',
                meta: {name: regionName}
            });
        });
    });
    
};


const handleEditRegion = async (regionId, tripId) => {
    const resp = await fetch(`/User/Regions/Create?tripId=${tripId}&regionId=${regionId}`);
    const html = await resp.text();
    const item = document.getElementById(`region-item-${regionId}`);
    item.outerHTML = html;
    attachRegionFormHandlers();
};

const handleDeleteRegion = (regionId) => {
    showConfirmationModal({
        title: 'Delete Region?',
        message: 'Are you sure you want to permanently delete this region and all its places?',
        confirmText: 'Delete',
        onConfirm: async () => {
            const fd = new FormData();
            fd.set('__RequestVerificationToken', document.querySelector('input[name="__RequestVerificationToken"]').value);

            const resp = await fetch(`/User/Regions/Delete/${regionId}`, {
                method: 'POST',
                body: fd
            });

            if (resp.ok) {
                const el = document.getElementById(`region-item-${regionId}`);
                el?.remove();
                await refreshTripDays();
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

    initPlaceHandlers();
};

const attachRegionFormHandlers = () => {
    document.querySelectorAll('.btn-region-cancel').forEach(btn => {
        btn.onclick = async () => {
            const regionId = btn.dataset.regionId;
            const wrapper = document.getElementById(`region-form-${regionId}`);
            const resp = await fetch(`/User/Regions/GetItemPartial?regionId=${regionId}`);
            wrapper.outerHTML = await resp.text();
            initRegionHandlers();
        };
    });

    document.querySelectorAll('.btn-region-save').forEach(btn => {
        btn.onclick = async () => {
            const regionId = btn.dataset.regionId;
            const formEl = document.getElementById(`region-form-${regionId}`);
            const fd = new FormData(formEl);
            const resp = await fetch(`/User/Regions/Create`, {
                method: 'POST',
                body: fd,
                headers: {'X-CSRF-TOKEN': fd.get('__RequestVerificationToken')}
            });

            const html = await resp.text();
            const wrapper = formEl.closest('.accordion-item, form');

            if (resp.ok) {
                wrapper.outerHTML = html;
                initRegionHandlers();
                await refreshTripDays();
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
