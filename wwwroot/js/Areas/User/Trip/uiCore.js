/**
 * uiCore.js — core UI helpers (pure store edition)
 * ------------------------------------------------
 * • saveTrip          — submits the Trip form and handles server response
 * • dimAll / clearDim — shared dimming utilities
 * • hideAllIndicators — hides “selected” chevrons/badges
 * • populateIconDropdown / populateColorDropdown / update* helpers
 * • rebindMainButtons — (re)attach Save buttons after DOM replacement
 */

import { store } from './storeInstance.js';

/* ------------------------------------------------------------------ *
 *  Trip form
 * ------------------------------------------------------------------ */
export const saveTrip = async (action) => {
    const form = document.getElementById('trip-form');
    if (!form) return;

    /* ensure no other form is open */
    store.dispatch('trip-cleanup-open-forms');           // 🔔 direct action
    await new Promise(r => setTimeout(r, 200));          // small debounce

    /* copy Quill HTML ➜ hidden input */
    const notesInput = form.querySelector('#Notes');
    const editor     = document.querySelector('#notes .ql-editor');
    if (notesInput && editor) {
        const html = editor.innerHTML.trim();
        notesInput.value = (html === '<p><br></p>') ? '' : html;
    }

    const formData = new FormData(form);
    formData.set('submitAction', action || 'save');

    try {
        const resp = await fetch(form.action, {
            method : 'POST',
            body   : formData,
            headers: { 'RequestVerificationToken': formData.get('__RequestVerificationToken') }
        });

        if (resp.redirected) {
            window.location.href = resp.url;             // success → navigation
        } else {
            const html   = await resp.text();            // validation errors → re-render
            const doc    = new DOMParser().parseFromString(html, 'text/html');
            const newForm= doc.querySelector('#trip-form');
            if (newForm) form.replaceWith(newForm);
        }
    } catch (err) {
        console.error('❌ Error saving trip:', err);
        wayfarer.showAlert('danger', `Failed to save the trip. Please try again.<br>${err?.message || ''}`);
    }
};

/* ------------------------------------------------------------------ *
 *  Dimming utilities
 * ------------------------------------------------------------------ */
export const dimAll = () => {
    document.querySelectorAll('.accordion-item, .place-list-item, .segment-list-item')
        .forEach(el => el.classList.add('dimmed'));
};

export const clearDim = () => {
    document.querySelectorAll('.accordion-item, .place-list-item, .segment-list-item')
        .forEach(el => el.classList.remove('dimmed'));
};

export const hideAllIndicators = () => {
    document.querySelectorAll('.selected-indicator')
        .forEach(el => el.classList.add('d-none'));
};

/* ------------------------------------------------------------------ *
 *  Main Save buttons
 * ------------------------------------------------------------------ */
export const rebindMainButtons = () => {
    document.getElementById('btn-save-trip')
        ?.addEventListener('click', () => saveTrip('save'));
    document.getElementById('btn-save-edit-trip')
        ?.addEventListener('click', () => saveTrip('save-edit'));
};

/* ------------------------------------------------------------------ *
 *  Icon dropdown
 * ------------------------------------------------------------------ */
export const populateIconDropdown = async (menuEl) => {
    const inputId      = menuEl.dataset.targetInput;
    const hiddenInput  = document.getElementById(inputId);
    if (!hiddenInput) {
        console.warn(`⚠️ Icon dropdown: hidden input with ID '${inputId}' not found`);
        return;
    }

    const dropdown     = menuEl.closest('.dropdown');
    const button       = dropdown.querySelector('.dropdown-toggle');
    const selectedLabel= button.querySelector('.selected-icon-label');
    const form         = dropdown.closest('form');
    const colorInput   = form?.querySelector('[name="MarkerColor"]');

    if (colorInput && !colorInput.value) colorInput.value = 'bg-blue';
    const currentColor = () => colorInput?.value?.trim() || 'bg-blue';

    try {
        const res   = await fetch('/api/icons?layout=marker');
        let   icons = await res.json();

        const PRIORITY = [
            'marker','star','camera','museum','eat','drink','hotel',
            'info','help','flag','danger','beach','hike','wc','sos','map'
        ];
        icons = [...PRIORITY.filter(i => icons.includes(i)), ...icons.filter(i => !PRIORITY.includes(i))];

        menuEl.innerHTML = '';

        const defaultIcon = icons.includes('marker') ? 'marker' : icons[0];
        const currentIcon = hiddenInput.value || defaultIcon;
        form?.querySelectorAll('input[name="IconName"]').forEach(inp => {
            if (!inp.value) inp.value = currentIcon;
        });

        for (const icon of icons) {
            const li  = document.createElement('li');
            const a   = document.createElement('a');
            a.className = 'dropdown-item d-flex align-items-center gap-2';
            a.href      = '#';
            a.dataset.icon = icon;

            const img  = document.createElement('img');
            img.src    = `/icons/wayfarer-map-icons/dist/png/marker/${currentColor()}/${icon}.png`;
            img.className = 'map-icon';
            img.width  = 24;
            img.height = 41;

            const label= document.createElement('span');
            label.textContent = icon;

            a.append(img,label);
            li.appendChild(a);
            menuEl.appendChild(li);

            a.addEventListener('click', e => {
                e.preventDefault();
                form?.querySelectorAll('input[name="IconName"]').forEach(inp => inp.value = icon);
                selectedLabel.innerHTML = '';
                selectedLabel.append(img.cloneNode(true), ` ${icon}`);
            });

            if (icon === currentIcon) {
                selectedLabel.innerHTML = '';
                selectedLabel.append(img.cloneNode(true), ` ${icon}`);
            }
        }

    } catch (err) {
        console.error('💥 Error loading icons for dropdown', err);
        menuEl.innerHTML = '<li><span class="dropdown-item text-danger">Failed to load icons</span></li>';
    }
};

/* ------------------------------------------------------------------ *
 *  Colour dropdown
 * ------------------------------------------------------------------ */
export const populateColorDropdown = async (formEl) => {
    const colorMenu = formEl.querySelector('.color-dropdown-menu');
    const toggleBtn = formEl.querySelector('.color-selector');
    const hiddenInp = formEl.querySelector('input[name="MarkerColor"]');
    if (!colorMenu || !toggleBtn || !hiddenInp) {
        console.warn('⚠️ Color dropdown: missing required elements.');
        return;
    }

    try {
        const resp = await fetch('/api/icons/colors');
        if (!resp.ok) throw new Error('Failed to load colors');
        const data = await resp.json();

        let colors = data?.backgrounds || [];
        const priority = ['bg-blue','bg-black','bg-purple','bg-green','bg-red'];
        colors = [...priority.filter(c => colors.includes(c)), ...colors.filter(c => !priority.includes(c))];

        if (!hiddenInp.value) hiddenInp.value = 'bg-blue';
        colorMenu.innerHTML = '';

        colors.forEach(color => {
            const li   = document.createElement('li');
            const a    = document.createElement('a');
            a.href     = '#';
            a.className = 'dropdown-item d-flex align-items-center gap-2';
            a.dataset.color = color;

            const dot  = document.createElement('span');
            dot.className = `marker-circle ${color}`;
            dot.style.width = dot.style.height = '1em';
            if (color === 'bg-white') dot.style.border = '1px solid #ccc';

            const label = document.createElement('span');
            label.textContent = color.replace(/^bg-/, '');

            a.append(dot,label);
            li.appendChild(a);
            colorMenu.appendChild(li);

            a.addEventListener('click', async e => {
                e.preventDefault();
                formEl.querySelectorAll('input[name="MarkerColor"]').forEach(inp => inp.value = color);
                renderSelectedColor(toggleBtn, color);
                await updateSelectedIconDropdown(formEl);
                updateDropdownIconColors(formEl, color);
            });
        });

        renderSelectedColor(toggleBtn, hiddenInp.value);
    } catch (err) {
        console.error('💥 Error loading colors for dropdown:', err);
    }
};

/* ------------------------------------------------------------------ *
 *  Icon/colour sync helpers
 * ------------------------------------------------------------------ */
export const updateSelectedIconDropdown = async (formEl) => {
    const selectedLabel = formEl.querySelector('.selected-icon-label');
    const iconInput     = formEl.querySelector('input[name="IconName"]');
    const colorInput    = formEl.querySelector('input[name="MarkerColor"]');
    if (!selectedLabel || !iconInput || !colorInput) return;

    const icon  = iconInput.value  || 'marker';
    const color = colorInput.value || 'bg-blue';

    const img   = document.createElement('img');
    img.src     = `/icons/wayfarer-map-icons/dist/png/marker/${color}/${icon}.png`;
    img.className = 'map-icon';
    img.width   = 24;
    img.height  = 41;

    selectedLabel.innerHTML = '';
    selectedLabel.append(img, ` ${icon}`);
};

export const updateDropdownIconColors = (formEl, newColor) => {
    formEl.querySelectorAll('.icon-dropdown-menu a.dropdown-item').forEach(link => {
        const icon = link.dataset.icon;
        const img  = link.querySelector('img.map-icon');
        if (img && icon)
            img.src = `/icons/wayfarer-map-icons/dist/png/marker/${newColor}/${icon}.png`;
    });
};

export const renderSelectedColor = (btn, color) => {
    btn.innerHTML = '';

    const dot = document.createElement('span');
    dot.className = `marker-circle ${color} me-2`;

    const css  = getComputedStyle(dot);
    const bgCol= css.getPropertyValue('--map-icon-bg').trim();
    if (bgCol) dot.style.backgroundColor = bgCol;
    if (color === 'bg-white') dot.style.border = '1px solid #ccc';

    const text = document.createElement('span');
    text.textContent = color.replace(/^bg-/, '');

    btn.append(dot,text);
};
