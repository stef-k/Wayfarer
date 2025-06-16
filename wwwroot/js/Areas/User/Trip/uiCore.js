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
    if (colorInput && !colorInput.value) {
        colorInput.value = 'bg-blue';
    }

    const getCurrentColor = () =>
        colorInput?.value?.trim() || 'bg-blue';

    const fetchSvg = async (icon) => {
        const res = await fetch(`/icons/wayfarer-map-icons/dist/marker/${icon}.svg`);
        const text = await res.text();
        const wrapper = document.createElement('div');
        wrapper.innerHTML = text.trim();
        const svg = wrapper.querySelector('svg');
        if (svg) {
            svg.setAttribute('width', '20');
            svg.setAttribute('height', '20');
            svg.classList.add('map-icon', 'color-white', 'me-2');
        }
        return svg;
    };

    try {
        const res = await fetch('/api/icons?layout=marker');
        const icons = await res.json();

        menuEl.innerHTML = '';

        const currentIcon = hiddenInput.value || icons[0];
        hiddenInput.value ||= currentIcon;

        for (const icon of icons) {
            const li = document.createElement('li');
            const a = document.createElement('a');
            a.className = 'dropdown-item d-flex align-items-center gap-2';
            a.href = '#';
            a.dataset.icon = icon;

            const label = document.createElement('span');
            label.textContent = icon;

            const svg = await fetchSvg(icon);
            if (svg) {
                svg.classList.add(getCurrentColor());  // 🔥 Apply bg color to all items
                a.appendChild(svg);
            }
            a.appendChild(label);
            li.appendChild(a);
            menuEl.appendChild(li);

            a.addEventListener('click', (e) => {
                e.preventDefault();
                hiddenInput.value = icon;
                const color = getCurrentColor();

                selectedLabel.innerHTML = '';
                const selectedSvg = svg.cloneNode(true);
                selectedSvg.classList.add(color);
                selectedLabel.appendChild(selectedSvg);
                selectedLabel.append(` ${icon}`);

                updateIconPreview(hiddenInput);
            });

            if (icon === currentIcon) {
                const color = getCurrentColor();
                selectedLabel.innerHTML = '';
                const selectedSvg = svg.cloneNode(true);
                selectedSvg.classList.add(color);
                selectedLabel.appendChild(selectedSvg);
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
        const colors = data?.backgrounds || [];

        // Fallback to blue if not set
        if (!hiddenInput.value) hiddenInput.value = 'bg-blue';

        // Clear previous entries
        colorDropdown.innerHTML = '';

        // Render all choices
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
            label.textContent = color;

            a.appendChild(dot);
            a.appendChild(label);
            li.appendChild(a);
            colorDropdown.appendChild(li);

            a.addEventListener('click', async (e) => {
                e.preventDefault();
                hiddenInput.value = color;

                console.log('Color clicked:', color);
                console.log('Toggle button before renderSelectedColor:', toggleBtn);

                // Update toggle button UI
                renderSelectedColor(toggleBtn, color);

                // Confirm updateSelectedIconDropdown call
                const hiddenIconInput = formEl.querySelector('input[name="IconName"]');
                if (hiddenIconInput) {
                    const iconValue = hiddenIconInput.value;
                    if (iconValue) {
                        console.log('Updating selected icon dropdown for icon:', iconValue);
                        await updateSelectedIconDropdown(formEl);
                    }
                }

                updateIconPreview(formEl);
                updateDropdownIconColors(formEl, color);
            });


        });

        // 🟦 Initial display
        renderSelectedColor(toggleBtn, hiddenInput.value);

    } catch (err) {
        console.error('💥 Error loading colors for dropdown:', err);
    }
};

export const updateDropdownIconColors = (formEl, newColor) => {
    const iconDropdown = formEl.querySelector('.icon-dropdown-menu');
    if (!iconDropdown) return;

    iconDropdown.querySelectorAll('svg').forEach(svg => {
        // Remove any old bg-* classes
        svg.classList.forEach(cls => {
            if (cls.startsWith('bg-')) svg.classList.remove(cls);
        });
        svg.classList.add(newColor);
    });
};

export const updateSelectedIconDropdown = async (formEl) => {
    const icon = formEl.querySelector('input[name="IconName"]')?.value;
    console.log('updateSelectedIconDropdown called with icon:', icon);
    const colorClass = formEl.querySelector('input[name="MarkerColor"]')?.value || 'bg-blue';
    const selectedLabel = formEl.querySelector('.selected-icon-label');

    if (!icon || !selectedLabel) return;

    const res = await fetch(`/icons/wayfarer-map-icons/dist/marker/${icon}.svg`);
    const text = await res.text();
    const wrapper = document.createElement('div');
    wrapper.innerHTML = text.trim();
    const svg = wrapper.querySelector('svg');
    if (!svg) return;

    svg.setAttribute('width', '20');
    svg.setAttribute('height', '20');
    svg.classList.add('map-icon', 'color-white', 'me-2');

    // Remove any previously applied bg-* classes from svg element
    svg.classList.forEach(cls => {
        if (cls.startsWith('bg-')) svg.classList.remove(cls);
    });
    // Add current bg color class
    svg.classList.add(colorClass);

    // Now also update the SVG internal fill color of background shapes, e.g. .map-icon-bg
    const bgElements = svg.querySelectorAll('.map-icon-bg, .map-icon-bg-outline');
    if (bgElements.length > 0) {
        // Get the computed CSS variable value for the background color
        const tempSpan = document.createElement('span');
        tempSpan.className = colorClass;
        document.body.appendChild(tempSpan);
        const bgColor = getComputedStyle(tempSpan).getPropertyValue('--map-icon-bg').trim();
        document.body.removeChild(tempSpan);

        bgElements.forEach(el => {
            el.style.fill = bgColor;
            el.style.stroke = bgColor; // for outline elements if needed
        });
    }

    selectedLabel.innerHTML = '';
    selectedLabel.appendChild(svg);
    selectedLabel.append(` ${icon}`);
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
    text.textContent = color;

    btn.appendChild(dot);
    btn.appendChild(text);
};

export const updateIconPreview = (formEl) => {
    const icon = formEl.querySelector('input[name="IconName"]')?.value || 'flag';
    const color = formEl.querySelector('input[name="MarkerColor"]')?.value || 'bg-blue';
    const preview = formEl.querySelector('img[id^="place-icon-preview"]');

    if (!preview) return;

    // Update src based on icon name
    preview.src = `/icons/wayfarer-map-icons/dist/marker/${icon}.svg`;

    // Remove any old bg-* classes
    preview.classList.forEach(cls => {
        if (cls.startsWith('bg-')) {
            preview.classList.remove(cls);
        }
    });

    // Apply new background class
    preview.className = `map-icon ${color} color-white`;

};



export const rebindMainButtons = () => {
    document.getElementById('btn-save-trip')?.addEventListener('click', () => saveTrip('save'));
    document.getElementById('btn-save-edit-trip')?.addEventListener('click', () => saveTrip('save-edit'));
};
