import { encodeFeatureText, featurePrecisionNotice } from './feature-metadata.js';

const text = value => typeof value === 'string' ? value.trim() : '';

/** Classifies retained enrichment claims, including imported tuples; never capture origin. */
export const isGeoapifyAddress = location => location?.isGeoapifyAddress === true || text(location?.reverseGeocodingProvider).toLowerCase() === 'geoapify'
    && text(location?.reverseGeocodingStorageMode).toLowerCase() === 'persistent'
    && typeof location?.reverseGeocodedAt === 'string' && Number.isFinite(Date.parse(location.reverseGeocodedAt));

/** Builds ordered groups from structured fields only, preserving exact text and first occurrences. */
export const locationAddress = (location, fallback = location?.fullAddress) => {
    if (!isGeoapifyAddress(location)) return { primary: text(fallback) || 'Address details unavailable', notice: '', fallback: '' };
    const seen = new Set();
    const groups = [['streetName', 'addressNumber'], ['postCode', 'place'], ['region'], ['country']]
        .map(fields => fields.map(field => {
            const value = text(location?.[field]);
            if (!value || seen.has(value)) return '';
            seen.add(value);
            return value;
        }).filter(Boolean).join(' ')).filter(Boolean);
    return {
        primary: groups.join(', ') || 'Address details unavailable',
        notice: groups.length && !text(location?.streetName) ? 'Street address unavailable' : '',
        fallback: groups.length ? '' : text(location?.fullAddress) || text(location?.providerAddressLine1) || text(location?.address)
    };
};

/** Plain text for coordinate links, summaries and tooltips; encode at the consuming boundary. */
export const locationAddressText = (location, fallback) => {
    const address = locationAddress(location, fallback);
    return [address.primary, address.notice].filter(Boolean).join(' — ');
};

/** Location-only address hierarchy. Trip consumers keep their existing feature helpers. */
export const renderLocationAddress = (location, fallback) => {
    const address = locationAddress(location, fallback);
    const geoapify = isGeoapifyAddress(location);
    const type = text(location?.resolvedFeatureType).toLowerCase();
    const precision = geoapify ? featurePrecisionNotice(type) : null;
    const label = ['amenity', 'building'].includes(type) ? 'Nearby mapped feature' : precision ? 'Mapped area' : 'Mapped feature';
    const name = geoapify ? text(location?.resolvedFeatureName) : '';
    const secondary = (value, role = '') => `<div class="small text-muted text-break"${role}>${value}</div>`;
    return `<div class="location-address text-break"><strong>Address:</strong> ${encodeFeatureText(address.primary)}`
        + (address.notice ? secondary(address.notice) : '')
        + (address.fallback ? secondary(`Provider display text: ${encodeFeatureText(address.fallback)}`) : '')
        + (name ? secondary(`${label}: ${encodeFeatureText(name)}`) : '')
        + (precision ? secondary(precision, ' role="note"') : '') + '</div>';
};
