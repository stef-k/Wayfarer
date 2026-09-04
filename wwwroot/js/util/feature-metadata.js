/** Returns encoded, gap-free rows for optional reverse-geocoding feature metadata. */
export const encodeFeatureText = value => String(value).replace(/[&<>"']/g, character => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    }[character]));

/** Maps provider classification metadata to an end-user address precision notice. */
export const featurePrecisionNotice = value => {
    switch (String(value || '').trim().toLowerCase()) {
        case 'postcode': return 'Approximate address — resolved to postcode level.';
        case 'city': return 'Approximate address — resolved to city level.';
        case 'suburb':
        case 'district': return 'Approximate address — resolved to local-area level.';
        case 'county':
        case 'state':
        case 'country': return 'Approximate address — resolved only to a regional level.';
        default: return null;
    }
};

export const renderFeatureMetadata = location => {
    const name = location?.resolvedFeatureName;
    const precision = featurePrecisionNotice(location?.resolvedFeatureType);
    return `${name ? `<div class="col-12"><strong>Detected place:</strong> <span>${encodeFeatureText(name)}</span></div>` : ''}`
        + `${precision ? `<div class="col-12" role="note">${precision}</div>` : ''}`;
};
