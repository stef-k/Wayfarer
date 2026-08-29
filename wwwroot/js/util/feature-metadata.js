/** Returns encoded, gap-free rows for optional reverse-geocoding feature metadata. */
export const encodeFeatureText = value => String(value).replace(/[&<>"']/g, character => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    }[character]));

export const renderFeatureMetadata = location => {
    const name = location?.resolvedFeatureName;
    const type = location?.resolvedFeatureType;
    const typeLabel = type ? type.charAt(0).toUpperCase() + type.slice(1) : null;
    return `${name ? `<div class="col-12"><strong>Detected place:</strong> <span>${encodeFeatureText(name)}</span></div>` : ''}`
        + `${typeLabel ? `<div class="col-12"><strong>Feature type:</strong> <span>${encodeFeatureText(typeLabel)}</span></div>` : ''}`;
};
