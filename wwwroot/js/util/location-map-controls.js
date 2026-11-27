import { message } from '../localization/messages.js';
import { installMapAttribution } from './map-attribution.js';

/** Keep Leaflet's default zoom position and the existing provider attribution. */
export const installLocationMapControls = map => {
    L.control.zoom({
        zoomInTitle: message('Shell.ZoomIn', 'Zoom in'),
        zoomOutTitle: message('Shell.ZoomOut', 'Zoom out')
    }).addTo(map);
    installMapAttribution(map);
};
