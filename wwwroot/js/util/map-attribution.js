import { message } from '../localization/messages.js';
import { encodeFeatureText as encode } from './feature-metadata.js';

/**
 * Installs the localized Wayfarer prefix on Leaflet's attribution control.
 * @param {import('leaflet').Map} map - Map whose layers supply their own attribution.
 */
export const installMapAttribution = map => {
    const control = map.attributionControl;
    if (!control) return;

    const credit = encode(message('Shell.WayfarerMapCredit', 'Powered by Wayfarer, made by Stef'));
    const author = encode(message('Shell.AuthorSite', 'Hi, I am Stef check out my blog!'));
    control.setPrefix(
        `&copy; <a href="https://wayfarer.stefk.me" title="${credit}" target="_blank" rel="noopener">Wayfarer</a> | ` +
        `<a href="https://stefk.me" title="${author}" target="_blank" rel="noopener">Stef K</a> | ` +
        '&copy; <a href="https://leafletjs.com/" target="_blank" rel="noopener">Leaflet</a>'
    );

    const label = message('Shell.MapAttribution', 'Map attribution');
    const container = control.getContainer();
    container?.setAttribute('aria-label', label);
    container?.setAttribute('title', label);
};
