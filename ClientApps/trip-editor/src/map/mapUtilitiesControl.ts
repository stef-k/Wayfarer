import L, { type LeafletMouseEvent, type Map as LeafletMap } from 'leaflet';

export type MapUtilitiesControl = L.Control & {
  cancelMeasure: () => void;
};

export const createMapUtilitiesControl = (element: HTMLElement): MapUtilitiesControl => {
  const control = new L.Control({ position: 'topright' });
  let cancelMeasure: (() => void) | null = null;

  control.onAdd = (map: LeafletMap) => {
    const container = L.DomUtil.create('div', 'leaflet-bar trip-editor-map-utilities');
    const zoomText = L.DomUtil.create('span', 'trip-editor-map-utilities__zoom', container);
    const measureButton = mapUtilityButton(container, 'Measure distance', '/lib/bootstrap-icons/bootstrap-icons-1.13.1/rulers.svg');
    const copyButton = mapUtilityButton(container, 'Copy map link', '/lib/bootstrap-icons/bootstrap-icons-1.13.1/link-45deg.svg');
    let measureTool: ReturnType<typeof createDistanceMeasureTool> | null = null;
    let copyTimer: number | null = null;

    const updateZoomText = (): void => {
      zoomText.textContent = `Zoom: ${map.getZoom()}`;
    };

    const stopMeasure = (): void => {
      measureTool?.cancel();
      measureTool = null;
      measureButton.classList.remove('active');
      element.style.cursor = '';
    };
    cancelMeasure = stopMeasure;

    const startMeasure = (): void => {
      measureTool = createDistanceMeasureTool(map, () => {
        measureTool = null;
        measureButton.classList.remove('active');
        element.style.cursor = '';
      });
      measureButton.classList.add('active');
      element.style.cursor = 'crosshair';
    };

    L.DomEvent.disableClickPropagation(container);
    L.DomEvent.disableScrollPropagation(container);
    updateZoomText();
    map.on('zoomend', updateZoomText);

    L.DomEvent.on(measureButton, 'click', event => {
      L.DomEvent.stop(event);
      if (measureTool) {
        stopMeasure();
      } else {
        startMeasure();
      }
    });

    L.DomEvent.on(copyButton, 'click', event => {
      L.DomEvent.stop(event);
      void copyCurrentMapLink(map).then(() => {
        copyButton.classList.add('trip-editor-map-utilities__button--copied');
        copyButton.setAttribute('aria-label', 'Map link copied');
        copyButton.setAttribute('title', 'Map link copied');
        if (copyTimer !== null) {
          window.clearTimeout(copyTimer);
        }
        copyTimer = window.setTimeout(() => {
          copyButton.classList.remove('trip-editor-map-utilities__button--copied');
          copyButton.setAttribute('aria-label', 'Copy map link');
          copyButton.setAttribute('title', 'Copy map link');
          copyTimer = null;
        }, 1500);
      });
    });

    control.onRemove = () => {
      map.off('zoomend', updateZoomText);
      stopMeasure();
      cancelMeasure = null;
      if (copyTimer !== null) {
        window.clearTimeout(copyTimer);
      }
    };

    return container;
  };

  return Object.assign(control, {
    cancelMeasure: () => cancelMeasure?.()
  });
};

function mapUtilityButton(container: HTMLElement, label: string, iconUrl: string): HTMLButtonElement {
  const button = L.DomUtil.create('button', 'trip-editor-map-utilities__button', container);
  button.type = 'button';
  button.setAttribute('aria-label', label);
  button.setAttribute('title', label);
  const image = document.createElement('img');
  image.src = iconUrl;
  image.alt = '';
  image.width = 18;
  image.height = 18;
  button.append(image);
  return button;
}

const createDistanceMeasureTool = (map: LeafletMap, onCancel: () => void): { cancel: () => void } => {
  const layer = L.layerGroup().addTo(map);
  const points: L.LatLng[] = [];
  const helpControl = new L.Control({ position: 'bottomleft' });

  helpControl.onAdd = () => {
    const div = L.DomUtil.create('div', 'leaflet-bar trip-editor-map-measure-help');
    div.textContent = 'Click to add points. Esc cancels.';
    return div;
  };

  const cleanup = (): void => {
    map.off('click', onMapClick);
    window.removeEventListener('keydown', onKeydown);
    layer.remove();
    helpControl.remove();
    onCancel();
  };

  const onKeydown = (event: KeyboardEvent): void => {
    if (event.key === 'Escape') {
      cleanup();
    }
  };

  const onMapClick = (event: LeafletMouseEvent): void => {
    points.push(event.latlng);
    L.circleMarker(event.latlng, {
      radius: 4,
      color: '#0d6efd',
      fillColor: '#0d6efd',
      fillOpacity: 0.9
    }).addTo(layer);

    if (points.length > 1) {
      const polyline = L.polyline(points, { color: '#0d6efd', weight: 3, dashArray: '5, 5' });
      polyline.addTo(layer);
      const km = distanceKilometers(points);
      L.marker(event.latlng, {
        icon: L.divIcon({
          className: 'trip-editor-map-distance-label',
          html: `<span>${km.toFixed(2)} km</span>`,
          iconSize: [72, 24],
          iconAnchor: [36, 12]
        }),
        interactive: false,
        keyboard: false
      }).addTo(layer);
    }
  };

  helpControl.addTo(map);
  map.on('click', onMapClick);
  window.addEventListener('keydown', onKeydown);
  return { cancel: cleanup };
};

function distanceKilometers(points: L.LatLng[]): number {
  let kilometers = 0;
  for (let index = 1; index < points.length; index += 1) {
    kilometers += points[index - 1].distanceTo(points[index]) / 1000;
  }
  return kilometers;
}

async function copyCurrentMapLink(map: LeafletMap): Promise<void> {
  const center = map.getCenter();
  const url = new URL(window.location.href);
  url.searchParams.set('lat', center.lat.toFixed(6));
  url.searchParams.set('lng', center.lng.toFixed(6));
  url.searchParams.set('zoom', String(map.getZoom()));
  const value = url.toString();
  if (navigator.clipboard?.writeText) {
    await navigator.clipboard.writeText(value);
    return;
  }

  const input = document.createElement('input');
  input.value = value;
  input.style.position = 'fixed';
  input.style.left = '-1000px';
  document.body.append(input);
  input.select();
  document.execCommand('copy');
  input.remove();
}
