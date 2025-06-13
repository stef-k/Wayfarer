// placeHandlers.js
// Handles create/edit/delete of places within a region
import { setMappingContext } from './mappingContext.js';

export const initPlaceHandlers = () => {
    attachPlaceFormHandlers();
};

const attachPlaceFormHandlers = () => {
    document.querySelectorAll('.btn-place-cancel').forEach(btn => {
        btn.onclick = async () => {
            const placeId = btn.dataset.placeId;
            const regionId = btn.dataset.regionId;

            if (!placeId) return;

            const regionEl = document.getElementById(`region-item-${regionId}`);
            const resp = await fetch(`/User/Regions/GetItemPartial?regionId=${regionId}`);
            regionEl.outerHTML = await resp.text();

            const event = new CustomEvent('region-dom-reloaded', { detail: { regionId } });
            document.dispatchEvent(event);
        };
    });

    document.querySelectorAll('.btn-place-save').forEach(btn => {
        btn.onclick = async () => {
            const placeId = btn.dataset.placeId;
            const regionId = btn.dataset.regionId;
            const formEl = document.getElementById(`place-form-${placeId}`);
            const fd = new FormData(formEl);

            const resp = await fetch(`/User/Places/CreateOrUpdate`, {
                method: 'POST',
                body: fd,
                headers: {
                    'X-CSRF-TOKEN': fd.get('__RequestVerificationToken')
                }
            });

            const html = await resp.text();
            const wrapper = formEl.closest('form');

            if (resp.ok) {
                const regionEl = document.getElementById(`region-item-${regionId}`);
                regionEl.outerHTML = html;

                const event = new CustomEvent('region-dom-reloaded', { detail: { regionId } });
                document.dispatchEvent(event);
            } else {
                wrapper.outerHTML = html;
                attachPlaceFormHandlers();

                const dom = new DOMParser().parseFromString(html, 'text/html');
                const errorsBlock = dom.querySelector('.place-form-errors ul');

                if (errorsBlock) {
                    const errors = Array.from(errorsBlock.querySelectorAll('li')).map(li => li.textContent.trim());
                    showAlert('danger', errors.join('\n'));
                }
            }
        };
    });

    document.querySelectorAll('.place-list-item').forEach(li => {
        li.addEventListener('click', () => {
            const placeId = li.dataset.placeId;
            const regionId = li.dataset.regionId;
            if (!placeId || !regionId) return;

            // Clear all highlights
            document.querySelectorAll('.place-list-item').forEach(el =>
                el.classList.remove('bg-warning-subtle')
            );
            document.querySelectorAll('.accordion-item').forEach(el =>
                el.classList.remove('bg-info-subtle', 'bg-info-soft')
            );

            // Highlight place
            li.classList.add('bg-warning-subtle');

            // Soft highlight the region this place belongs to
            const regionEl = document.getElementById(`region-item-${regionId}`);
            regionEl?.classList.add('bg-info-soft');

            const name = li.querySelector('.place-name')?.innerText || 'Unnamed Place';

            setMappingContext({
                type: 'place',
                id: placeId,
                action: 'set-location',
                meta: { name, regionId }
            });
        });
    });

};
