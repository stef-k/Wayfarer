// placeHandlers.js
// Handles create/edit/delete of places within a region
import {setMappingContext, getMappingContext, clearMappingContext} from './mappingContext.js';
import { renderPlaceMarker, removePlaceMarker  } from './mapManager.js';
import {populateIconDropdown, populateColorDropdown, updateDropdownIconColors} from './uiCore.js';

export const initPlaceHandlers = () => {
    attachPlaceFormHandlers();
};

export const enhancePlaceForm = async (formEl) => {
    const iconMenu = formEl.querySelector('.icon-dropdown-menu');
    if (iconMenu) {
        await populateIconDropdown(iconMenu);
    } else {
        console.warn('⚠️ No icon menu found in form');
    }

    const colorMenu = formEl.querySelector('.color-dropdown-menu');
    if (colorMenu) {
        await populateColorDropdown(formEl);
    } else {
        console.warn('⚠️ No color menu found in form');
    }

    const hiddenColorInput = formEl.querySelector('input[name="MarkerColor"]');
    const currentColor = hiddenColorInput?.value || 'bg-blue';

    // Apply preview + recolor icons now
    updateDropdownIconColors(formEl, currentColor);

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
            removePlaceMarker(placeId);
            clearMappingContext();
        };
    });

    document.querySelectorAll('.btn-place-save').forEach(btn => {
        btn.onclick = async () => {
            const placeId = btn.dataset.placeId;
            const regionId = btn.dataset.regionId;
            const formEl = document.getElementById(`place-form-${placeId}`);
            const fd = new FormData(formEl);
            const token = fd.get('__RequestVerificationToken');   // Razor adds this

            const resp = await fetch('/User/Places/CreateOrUpdate', {
                method: 'POST',
                body: fd,
                credentials: 'same-origin',              // send the anti-forgery cookie
                headers: token ? {                       // add header only if we have one
                    RequestVerificationToken: token      // official ASP.NET Core header name
                } : {}
            });

            const html = await resp.text();
            const wrapper = formEl.closest('form');

            if (resp.ok) {
                const regionEl = document.getElementById(`region-item-${regionId}`);
                regionEl.outerHTML = html;

                const event = new CustomEvent('region-dom-reloaded', { detail: { regionId } });
                document.dispatchEvent(event);
                regionEl.querySelectorAll('form[id^="place-form-"]').forEach(await enhancePlaceForm);

                const context = getMappingContext();
                if (context?.type === 'place' && context.id === placeId) {
                    const el = document.querySelector(`.place-list-item[data-place-id="${placeId}"]`);
                    if (el) {
                        const lat = el.dataset.placeLat;
                        const lon = el.dataset.placeLon;
                        const icon = el.dataset.placeIcon;
                        const color = el.dataset.placeColor;

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
                }
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

            document.querySelectorAll('.place-list-item').forEach(el =>
                el.classList.remove('bg-warning-subtle')
            );
            document.querySelectorAll('.accordion-item').forEach(el =>
                el.classList.remove('bg-info-subtle', 'bg-info-soft')
            );

            li.classList.add('bg-warning-subtle');
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

document.addEventListener('region-dom-reloaded', (e) => {
    (async () => {
        const regionId = e.detail.regionId;
        const regionEl = document.getElementById(`region-item-${regionId}`);
        if (!regionEl) return;

        const forms = regionEl.querySelectorAll('form[id^="place-form-"]');
        for (const form of forms) {
            await enhancePlaceForm(form);
        }
    })();
});
