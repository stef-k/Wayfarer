// Custom control to display zoom level and copy permalink with theme support
const ZoomLevelControl = L.Control.extend({
    onAdd: function (map) {
        // Create a container element for the control
        const container = L.DomUtil.create('div', 'leaflet-bar leaflet-control leaflet-control-custom');
        container.style.padding = '4px';
        container.style.display = 'flex';
        container.style.alignItems = 'center';
        container.style.gap = '6px';

        // Create zoom text
        const zoomText = L.DomUtil.create('span', '', container);
        zoomText.textContent = `Zoom: ${map.getZoom()}`;
        zoomText.style.fontSize = '14px';

        // Create copy button using Bootstrap Icon
        const copyBtn = L.DomUtil.create('button', '', container);
        copyBtn.title = 'Copy map link';
        copyBtn.style.cursor = 'pointer';
        copyBtn.style.border = 'none';
        copyBtn.style.background = 'transparent';
        copyBtn.style.padding = '0';
        copyBtn.style.lineHeight = '1';
        copyBtn.innerHTML = `<img src="/lib/bootstrap-icons/bootstrap-icons-1.13.1/link-45deg.svg" alt="Copy link" style="width:1.2em; height:1.2em;" />`;

        // Helper to apply current theme to container, text, and icon
        const applyTheme = () => {
            const theme = document.body.getAttribute('data-bs-theme');
            const iconImg = copyBtn.querySelector('img');
            if (theme === 'dark') {
                container.style.backgroundColor = '#2b2b2b';
                container.style.border = '1px solid #555';
                zoomText.style.color = '#fff';
                iconImg.style.filter = 'invert(1)';
            } else {
                container.style.backgroundColor = '#fff';
                container.style.border = '1px solid #ccc';
                zoomText.style.color = '#000';
                iconImg.style.filter = '';
            }
        };

        // Observe theme changes
        const themeObserver = new MutationObserver(applyTheme);
        themeObserver.observe(document.body, { attributes: true, attributeFilter: ['data-bs-theme'] });
        // Initial apply
        applyTheme();

        // Update zoom text on zoom changes
        map.on('zoomend', () => {
            zoomText.textContent = `Zoom: ${map.getZoom()}`;
        });

        // Copy current URL to clipboard on button click
        L.DomEvent.on(copyBtn, 'click', (e) => {
            L.DomEvent.stopPropagation(e);
            const url = window.location.href;
            const showSuccess = () => {
                copyBtn.innerHTML = `<img src=\"/lib/bootstrap-icons/bootstrap-icons-1.13.1/check.svg\" alt=\"Copied\" style=\"width:1.2em; height:1.2em;\" />`;
                applyTheme();
                setTimeout(() => {
                    copyBtn.innerHTML = `<img src=\"/lib/bootstrap-icons/bootstrap-icons-1.13.1/link-45deg.svg\" alt=\"Copy link\" style=\"width:1.2em; height:1.2em;\" />`;
                    applyTheme();
                }, 1500);
            };

            if (navigator.clipboard && navigator.clipboard.writeText) {
                navigator.clipboard.writeText(url)
                    .then(showSuccess)
                    .catch(err => console.error('Copy failed', err));
            } else {
                // Fallback for older browsers
                const input = document.createElement('input');
                document.body.appendChild(input);
                input.value = url;
                input.select();
                document.execCommand('copy');
                document.body.removeChild(input);
                showSuccess();
            }
        });

        // Store observer for cleanup
        container._themeObserver = themeObserver;
        return container;
    },

    onRemove: function (map) {
        // Disconnect theme observer
        if (this._container && this._container._container && this._container._themeObserver) {
            this._container._themeObserver.disconnect();
        }
    }
});

export const addZoomLevelControl = (map) => {
    const zoomLevelControl = new ZoomLevelControl({ position: 'topright' });
    map.addControl(zoomLevelControl);
};

// ... other marker exports remain unchanged ...

export const latestLocationMarker = L.icon({
    iconUrl: '/icons/location-latest-green.svg',
    iconSize: [36, 36],
    iconAnchor: [18, 18],
    popupAnchor: [12, 12],
});
export const liveMarker = L.divIcon({
    className: 'pulsing-marker',
    iconSize: [36, 36],
    iconAnchor: [18, 18],
});

export const eatMarker = L.divIcon({
    iconUrl: '/lib/bootstrap-icons/bootstrap-icons-1.13.1/fork-knife.svg',
    iconAnchor: [18, 18],
    popupAnchor: [12, 12],
});

export const drinkMarker = L.divIcon({
    iconUrl: '/lib/bootstrap-icons/bootstrap-icons-1.13.1/cup-straw.svg',
    iconAnchor: [18, 18],
    popupAnchor: [12, 12],
});

export const cameraMarker = L.divIcon({
    iconUrl: '/lib/bootstrap-icons/bootstrap-icons-1.13.1/camera.svg',
    iconAnchor: [18, 18],
    popupAnchor: [12, 12],
});