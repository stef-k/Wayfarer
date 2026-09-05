/**
 * Render the shared Timeline statistics hierarchy with presentation-only missing parents.
 * Coordinate URLs, link hooks, accordion IDs and recorded counts retain their existing behavior.
 */
export const renderStatistics = (stats, highlightType, displayDate, period = null) => {
    // Labels are encoded only at the HTML boundary; raw component names remain matching keys.
    const encode = (value) => String(value).replace(/[&<>"']/g, char => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[char]);
    // Missing-parent sections exist only in this local presentation tree, never in API arrays.
    const countries = [...stats.countries];
    if (stats.regions.some(r => r.countryName === '') || stats.cities.some(c => c.countryName === '')) {
        countries.push({ name: '', missing: true });
    }
    let html = '<div class="container-fluid">';

    // Summary section
    html += '<div class="row mb-3">';
    html += '<div class="col-12">';
    html += `<h6>Overview</h6>`;
    html += `<p><strong>Total Locations:</strong> ${stats.totalLocations}</p>`;
    if (period !== null) html += `<p><strong>Period:</strong> ${encode(period)}</p>`;
    if (stats.fromDate && stats.toDate) {
        html += `<p><strong>Date Range:</strong> ${displayDate(stats.fromDate)} to ${displayDate(stats.toDate)}</p>`;
    }
    html += '</div>';
    html += '</div>';

    // Countries section with hierarchical collapsible structure
    const countriesHighlight = highlightType === 'countries' ? 'bg-light border' : '';
    html += `<div class="row mb-3 ${countriesHighlight} p-2">`;
    html += '<div class="col-12">';
    html += `<h6>Countries (${stats.countries.length})</h6>`;

    if (countries.length > 0) {
        html += '<div class="accordion" id="countriesAccordion">';

        countries.forEach((country, countryIdx) => {
            const homeLabel = country.isHomeCountry ? ' <span class="badge bg-info">Home</span>' : '';
            const firstVisit = country.missing ? '' : displayDate(country.firstVisit);
            const lastVisit = country.missing ? '' : displayDate(country.lastVisit);

            // Extract coordinates from PostGIS Point
            const lat = country.coordinates?.latitude || 0;
            const lng = country.coordinates?.longitude || 0;
            const countryMapUrl = `?lat=${lat.toFixed(6)}&lng=${lng.toFixed(6)}&zoom=8`;

            // Get regions for this country
            const countryRegions = stats.regions.filter(r => r.countryName === country.name);
            const recordedRegionCount = countryRegions.length;
            if (stats.cities.some(c => c.countryName === country.name && c.regionName === '')) {
                countryRegions.push({ name: '', missing: true });
            }

            html += `<div class="accordion-item">`;
            html += `<h2 class="accordion-header" id="country-heading-${countryIdx}">`;
            html += `<div class="d-flex w-100 align-items-center">`;
            html += `<button class="accordion-button collapsed flex-grow-1" type="button" data-bs-toggle="collapse" data-bs-target="#country-${countryIdx}">`;
            html += `${country.missing ? 'Country not recorded' : encode(country.name)}${homeLabel}`;
            if (!country.missing) html += `<small class="ms-2 text-muted">(${country.visitCount} records, ${firstVisit} - ${lastVisit})</small>`;
            html += `</button>`;
            if (!country.missing) html += `<a href="${countryMapUrl}" class="btn btn-sm btn-outline-primary country-coords-link me-2" data-lat="${lat}" data-lng="${lng}" onclick="event.stopPropagation();" title="View on map" style="min-width: 70px;"><i class="bi bi-geo-alt"></i> Map</a>`;
            html += `</div>`;
            html += `</h2>`;
            html += `<div id="country-${countryIdx}" class="accordion-collapse collapse" data-bs-parent="#countriesAccordion">`;
            html += `<div class="accordion-body">`;

            if (countryRegions.length > 0) {
                html += `<h6>Regions (${recordedRegionCount})</h6>`;
                html += `<div class="accordion" id="regionsAccordion-${countryIdx}">`;

                countryRegions.forEach((region, regionIdx) => {
                    const regFirstVisit = region.missing ? '' : displayDate(region.firstVisit);
                    const regLastVisit = region.missing ? '' : displayDate(region.lastVisit);
                    const regLat = region.coordinates?.latitude || 0;
                    const regLng = region.coordinates?.longitude || 0;
                    const regionMapUrl = `?lat=${regLat.toFixed(6)}&lng=${regLng.toFixed(6)}&zoom=10`;

                    // Get cities for this region
                    const regionCities = stats.cities.filter(c => c.regionName === region.name && c.countryName === country.name);

                    html += `<div class="accordion-item">`;
                    html += `<h2 class="accordion-header" id="region-heading-${countryIdx}-${regionIdx}">`;
                    html += `<div class="d-flex w-100 align-items-center">`;
                    html += `<button class="accordion-button collapsed flex-grow-1" type="button" data-bs-toggle="collapse" data-bs-target="#region-${countryIdx}-${regionIdx}">`;
                    html += `${region.missing ? 'Region not recorded' : encode(region.name)}`;
                    if (!region.missing) html += `<small class="ms-2 text-muted">(${region.visitCount} records, ${regFirstVisit} - ${regLastVisit})</small>`;
                    html += `</button>`;
                    if (!region.missing) html += `<a href="${regionMapUrl}" class="btn btn-sm btn-outline-primary country-coords-link me-2" data-lat="${regLat}" data-lng="${regLng}" onclick="event.stopPropagation();" title="View on map" style="min-width: 70px;"><i class="bi bi-geo-alt"></i> Map</a>`;
                    html += `</div>`;
                    html += `</h2>`;
                    html += `<div id="region-${countryIdx}-${regionIdx}" class="accordion-collapse collapse" data-bs-parent="#regionsAccordion-${countryIdx}">`;
                    html += `<div class="accordion-body">`;

                    if (regionCities.length > 0) {
                        html += `<h6>Cities (${regionCities.length})</h6>`;
                        html += '<div class="list-group">';

                        regionCities.forEach(city => {
                            const cityFirstVisit = displayDate(city.firstVisit);
                            const cityLastVisit = displayDate(city.lastVisit);
                            const cityLat = city.coordinates?.latitude || 0;
                            const cityLng = city.coordinates?.longitude || 0;
                            const cityMapUrl = `?lat=${cityLat.toFixed(6)}&lng=${cityLng.toFixed(6)}&zoom=13`;

                            html += `<div class="list-group-item d-flex justify-content-between align-items-center">`;
                            html += `<div><strong>${encode(city.name)}</strong> <small class="text-muted">(${city.visitCount} records, ${cityFirstVisit} - ${cityLastVisit})</small></div>`;
                            html += `<a href="${cityMapUrl}" class="btn btn-sm btn-outline-primary country-coords-link" data-lat="${cityLat}" data-lng="${cityLng}" title="View on map" style="min-width: 70px;"><i class="bi bi-geo-alt"></i> Map</a>`;
                            html += `</div>`;
                        });

                        html += '</div>';
                    } else {
                        html += '<p class="text-muted">No cities in this region</p>';
                    }

                    html += `</div></div></div>`;
                });

                html += `</div>`;
            } else {
                html += '<p class="text-muted">No regions in this country</p>';
            }

            html += `</div></div></div>`;
        });

        html += '</div>';
    } else {
        html += '<p class="text-muted">No country data available</p>';
    }

    html += '</div>';
    html += '</div>';

    html += '</div>';

    return html;
};
