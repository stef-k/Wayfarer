import { encodeFeatureText, featurePrecisionNotice } from './feature-metadata.js';
import { message } from '../localization/messages.js';

const text = value => typeof value === 'string' ? value.trim() : '';

/** Classifies retained enrichment claims, including imported tuples; never capture origin. */
export const isGeoapifyAddress = location => location?.isGeoapifyAddress === true || text(location?.reverseGeocodingProvider).toLowerCase() === 'geoapify'
    && text(location?.reverseGeocodingStorageMode).toLowerCase() === 'persistent'
    && typeof location?.reverseGeocodedAt === 'string' && Number.isFinite(Date.parse(location.reverseGeocodedAt));

/** Builds ordered groups from structured fields only, preserving exact text and first occurrences. */
const resolveLocationAddress = (location, fallback = location?.fullAddress) => {
    if (!isGeoapifyAddress(location)) return { primary: text(fallback), streetMissing: false, fallback: '' };
    const seen = new Set();
    const groups = [['streetName', 'addressNumber'], ['postCode', 'place'], ['region'], ['country']]
        .map(fields => fields.map(field => {
            const value = text(location?.[field]);
            if (!value || seen.has(value)) return '';
            seen.add(value);
            return value;
        }).filter(Boolean).join(' ')).filter(Boolean);
    return {
        primary: groups.join(', '),
        streetMissing: groups.length > 0 && !text(location?.streetName),
        fallback: groups.length ? '' : text(location?.fullAddress) || text(location?.providerAddressLine1) || text(location?.address)
    };
};

/** Retains the original plain-text address wording used in link queries and existing consumers. */
export const locationAddress = (location, fallback) => {
    const address = resolveLocationAddress(location, fallback);
    return {
        primary: address.primary || 'Address details unavailable',
        notice: address.streetMissing ? 'Street address unavailable' : '',
        fallback: address.fallback
    };
};

/** Plain text for coordinate links, summaries and tooltips; encode at the consuming boundary. */
export const locationAddressText = (location, fallback) => {
    const address = locationAddress(location, fallback);
    return [address.primary, address.notice].filter(Boolean).join(' — ');
};

/** Location-only address hierarchy. Trip consumers keep their existing feature helpers. */
export const renderLocationAddress = (location, fallback) => {
    const address = resolveLocationAddress(location, fallback);
    const geoapify = isGeoapifyAddress(location);
    const type = text(location?.resolvedFeatureType).toLowerCase();
    const precision = geoapify ? featurePrecisionNotice(type) : null;
    const name = geoapify ? text(location?.resolvedFeatureName) : '';
    const feature = ['amenity', 'building'].includes(type)
        ? message('Shell.LocationNearbyFeature', 'Nearby mapped feature: {0}', name)
        : precision ? message('Shell.LocationMappedArea', 'Mapped area: {0}', name)
            : message('Shell.LocationMappedFeature', 'Mapped feature: {0}', name);
    const heading = message('Shell.LocationAddress', 'Address:');
    const primary = address.primary || message('Shell.LocationAddressUnavailable', 'Address details unavailable');
    const secondary = (value, role = '') => `<span class="d-block small text-muted text-break"${role}>${value}</span>`;
    return `<span class="d-block location-address text-break"><strong>${encodeFeatureText(heading)}</strong> ${encodeFeatureText(primary)}`
        + (address.streetMissing ? secondary(encodeFeatureText(message('Shell.LocationStreetUnavailable', 'Street address unavailable'))) : '')
        + (address.fallback ? secondary(encodeFeatureText(message('Shell.LocationProviderText', 'Provider display text: {0}', address.fallback))) : '')
        + (name ? secondary(encodeFeatureText(feature)) : '')
        + (precision ? secondary(encodeFeatureText(precision), ' role="note"') : '') + '</span>';
};

/** Encodes an individual address component in Location tables, retaining the missing-data icon. */
export const renderAddressComponent = (value, label) => value ? encodeFeatureText(value)
    : `<i class="bi bi-patch-question" title="No available data for ${encodeFeatureText(label)}"></i>`;
