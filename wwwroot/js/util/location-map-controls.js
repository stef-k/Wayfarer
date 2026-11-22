import { message } from '../localization/messages.js';
import { encodeFeatureText as encode } from './feature-metadata.js';

/** Keep Leaflet's default zoom position and the existing provider attribution. */
export const installLocationMapControls = map => {
    L.control.zoom({
        zoomInTitle: message('Shell.ZoomIn', 'Zoom in'),
        zoomOutTitle: message('Shell.ZoomOut', 'Zoom out')
    }).addTo(map);
    const attribution = map.attributionControl;
    attribution.setPrefix(`&copy; <a href="https://wayfarer.stefk.me" title="${encode(message('Shell.WayfarerMapCredit', 'Powered by Wayfarer, made by Stef'))}" target="_blank">Wayfarer</a> | <a href="https://stefk.me" title="${encode(message('Locations.AuthorBlog', 'Check my blog'))}" target="_blank">Stef K</a> | &copy; <a href="https://leafletjs.com/" target="_blank">Leaflet</a>`);
    attribution.getContainer()?.setAttribute('aria-label', message('Shell.MapAttribution', 'Map attribution'));
};
