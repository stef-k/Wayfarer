// uiCore.js
// Handles save trip logic and re-binding action buttons

export const saveTrip = async (action) => {
    const form = document.getElementById('trip-form');
    const formData = new FormData(form);
    formData.set('submitAction', action);

    try {
        const resp = await fetch(form.action, {
            method: 'POST',
            body: formData,
            headers: {
                'X-CSRF-TOKEN': formData.get('__RequestVerificationToken')
            }
        });

        if (resp.redirected) {
            window.location.href = resp.url;
        } else {
            const html = await resp.text();
            const parser = new DOMParser();
            const doc = parser.parseFromString(html, 'text/html');
            const newForm = doc.querySelector('#trip-form');

            if (newForm) {
                form.replaceWith(newForm);
                rebindMainButtons(); // re-attach after replacing DOM
            }
        }
    } catch (err) {
        console.error(err);
        alert('Error saving trip.');
    }
};

export const populateIconDropdown = async (menuEl) => {
    const inputId = menuEl.dataset.targetInput;
    const hiddenInput = document.getElementById(inputId);
    if (!hiddenInput) {
        console.warn(`⚠️ Icon dropdown: hidden input with ID '${inputId}' not found`);
        return;
    }

    const dropdown = menuEl.closest('.dropdown');
    const button = dropdown.querySelector('.dropdown-toggle');
    const selectedLabel = button.querySelector('.selected-icon-label');
    const form = dropdown.closest('form');
    const colorInput = form?.querySelector('[name="MarkerColor"]');

    // Ensure default color is applied if empty
    if (colorInput && !colorInput.value) colorInput.value = 'bg-blue';

    const getCurrentColor = () =>
        colorInput?.value?.trim() || 'bg-blue';

    try {
        const res = await fetch('/api/icons?layout=marker');
        let icons = await res.json();

        const PRIORITY_ICONS = [
            'marker', 'star', 'camera', 'museum', 'eat', 'drink', 'hotel',
            'info', 'help', 'flag', 'danger', 'beach', 'hike', 'wc', 'sos', 'map'
        ];

        icons = [
            ...PRIORITY_ICONS.filter(p => icons.includes(p)),
            ...icons.filter(i => !PRIORITY_ICONS.includes(i))
        ];

        menuEl.innerHTML = '';

        const defaultIcon = icons.includes('marker') ? 'marker' : icons[0];
        const currentIcon = hiddenInput.value || defaultIcon;

        form?.querySelectorAll('input[name="IconName"]').forEach(inp => {
            if (!inp.value) inp.value = currentIcon;
        });

        for (const icon of icons) {
            const color = getCurrentColor();
            const imgUrl = `/icons/wayfarer-map-icons/dist/png/marker/${color}/${icon}.png`;

            const li = document.createElement('li');
            const a = document.createElement('a');
            a.className = 'dropdown-item d-flex align-items-center gap-2';
            a.href = '#';
            a.dataset.icon = icon;

            const img = document.createElement('img');
            img.src = imgUrl;
            img.className = 'map-icon';
            img.width = 24;
            img.height = 41;

            const label = document.createElement('span');
            label.textContent = icon;

            a.appendChild(img);
            a.appendChild(label);
            li.appendChild(a);
            menuEl.appendChild(li);

            a.addEventListener('click', (e) => {
                e.preventDefault();
                form?.querySelectorAll('input[name="IconName"]').forEach(inp => inp.value = icon);

                selectedLabel.innerHTML = '';
                const selectedImg = img.cloneNode(true);
                selectedLabel.appendChild(selectedImg);
                selectedLabel.append(` ${icon}`);
            });

            if (icon === currentIcon) {
                selectedLabel.innerHTML = '';
                const selectedImg = img.cloneNode(true);
                selectedLabel.appendChild(selectedImg);
                selectedLabel.append(` ${icon}`);
            }
        }

    } catch (err) {
        console.error('💥 Error loading icons for dropdown', err);
        menuEl.innerHTML = '<li><span class="dropdown-item text-danger">Failed to load icons</span></li>';
    }
};

export const populateColorDropdown = async (formEl) => {
    const colorDropdown = formEl.querySelector('.color-dropdown-menu');
    const toggleBtn = formEl.querySelector('.color-selector');
    const hiddenInput = formEl.querySelector('input[name="MarkerColor"]');
    
    if (!colorDropdown || !toggleBtn || !hiddenInput) {
        console.warn('⚠️ Color dropdown: missing one or more required elements.');
        return;
    }

    try {
        const resp = await fetch('/api/icons/colors');
        if (!resp.ok) throw new Error('Failed to load colors');

        const data = await resp.json();

        let colors = data?.backgrounds || [];
        const priority = ['bg-blue', 'bg-black', 'bg-purple', 'bg-green', 'bg-red'];
        colors = [
            ...priority.filter(c => colors.includes(c)),
            ...colors.filter(c => !priority.includes(c)),
        ];

        if (!hiddenInput.value) hiddenInput.value = 'bg-blue';
        colorDropdown.innerHTML = '';

        colors.forEach(color => {
            const li = document.createElement('li');
            const a = document.createElement('a');
            a.href = '#';
            a.className = 'dropdown-item d-flex align-items-center gap-2';
            a.dataset.color = color;

            const dot = document.createElement('span');
            dot.className = `marker-circle ${color}`;
            dot.style.width = '1em';
            dot.style.height = '1em';
            dot.style.verticalAlign = 'middle';
            if (color === 'bg-white') {
                dot.style.border = '1px solid #ccc';
            }

            const label = document.createElement('span');
            label.textContent = color.replace(/^bg-/, '');

            a.appendChild(dot);
            a.appendChild(label);
            li.appendChild(a);
            colorDropdown.appendChild(li);

            a.addEventListener('click', async (e) => {
                e.preventDefault();
                formEl.querySelectorAll('input[name="MarkerColor"]').forEach(inp => inp.value = color);
                renderSelectedColor(toggleBtn, color);
                await updateSelectedIconDropdown(formEl);
                updateDropdownIconColors(formEl, color);
            });
        });

        renderSelectedColor(toggleBtn, hiddenInput.value);
    } catch (err) {
        console.error('💥 Error loading colors for dropdown:', err);
    }
};

export const updateSelectedIconDropdown = async (formEl) => {
    const selectedLabel = formEl.querySelector('.selected-icon-label');
    const iconInput = formEl.querySelector('input[name="IconName"]');
    const colorInput = formEl.querySelector('input[name="MarkerColor"]');

    if (!selectedLabel || !iconInput || !colorInput) return;

    const icon = iconInput.value || 'marker';
    const color = colorInput.value || 'bg-blue';

    const img = document.createElement('img');
    img.src = `/icons/wayfarer-map-icons/dist/png/marker/${color}/${icon}.png`;
    img.className = 'map-icon';
    img.width = 24;
    img.height = 41;

    selectedLabel.innerHTML = '';
    selectedLabel.appendChild(img);
    selectedLabel.append(` ${icon}`);
};


export const updateDropdownIconColors = (formEl, newColor) => {
    const iconDropdown = formEl.querySelector('.icon-dropdown-menu');
    if (!iconDropdown) return;

    const iconInput = formEl.querySelector('input[name="IconName"]');
    const icon = iconInput?.value || 'marker';

    iconDropdown.querySelectorAll('a.dropdown-item').forEach(link => {
        const iconName = link.dataset.icon;
        const img = link.querySelector('img.map-icon');
        if (img && iconName) {
            img.src = `/icons/wayfarer-map-icons/dist/png/marker/${newColor}/${iconName}.png`;
        }
    });
};

export const renderSelectedColor = (btn, color) => {
    btn.innerHTML = '';

    const dot = document.createElement('span');
    dot.className = `marker-circle ${color} me-2`;

    // Read the CSS variable value from the computed style and set it inline
    const computedStyle = getComputedStyle(dot);
    const bgColor = computedStyle.getPropertyValue('--map-icon-bg').trim();
    if (bgColor) {
        dot.style.backgroundColor = bgColor;
    }

    if (color === 'bg-white') {
        dot.style.border = '1px solid #ccc';
    }

    const text = document.createElement('span');
    // show friendly name
    text.textContent = color.replace(/^bg-/, '');

    btn.appendChild(dot);
    btn.appendChild(text);
};

export const rebindMainButtons = () => {
    document.getElementById('btn-save-trip')?.addEventListener('click', () => saveTrip('save'));
    document.getElementById('btn-save-edit-trip')?.addEventListener('click', () => saveTrip('save-edit'));
};
