import assert from 'node:assert/strict';
import test from 'node:test';
import { locationAddress, renderLocationAddress } from '../../wwwroot/js/util/location-address.js';

const retained = {
    reverseGeocodingProvider: ' Geoapify ', reverseGeocodingStorageMode: 'persistent',
    reverseGeocodedAt: '2020-01-01T00:00:00Z', fullAddress: 'Legacy business text', address: 'Old POI',
    streetName: ' Οδός ', addressNumber: '10-12', postCode: '00123', place: 'Town', region: 'Region', country: 'Country'
};

test('historical and imported valid tuples share structured presentation independently of capture origin', () => {
    for (const source of [undefined, 'import']) {
        const value = { ...retained, source, provider: 'gps', resolvedFeatureName: 'Hotel', resolvedFeatureType: 'building' };
        const html = renderLocationAddress(value);
        assert.equal(locationAddress(value).primary, 'Οδός 10-12, 00123 Town, Region, Country');
        assert.equal(locationAddress({ ...value, reverseGeocodingProvider: null, isGeoapifyAddress: true }).primary, locationAddress(value).primary);
        assert.doesNotMatch(html, /Legacy business|Old POI/);
        assert.match(html, /small text-muted text-break/);
        assert.ok(html.indexOf('Οδός') < html.indexOf('Nearby mapped feature: Hotel'));
    }
});

test('manual, invalid provenance and historical Mapbox retain their existing display preference and encode text', () => {
    for (const tuple of [{}, { reverseGeocodingProvider: 'mapbox', reverseGeocodingStorageMode: 'permanent' },
        { reverseGeocodingProvider: 'geoapify', reverseGeocodingStorageMode: 'permanent' },
        { reverseGeocodingProvider: 'geoapify', reverseGeocodingStorageMode: 'persistent', reverseGeocodedAt: 'invalid' }]) {
        const value = { ...retained, reverseGeocodingProvider: null, ...tuple, fullAddress: '<img src=x>&"' };
        const html = renderLocationAddress(value);
        assert.match(html, /&lt;img src=x&gt;&amp;&quot;/);
        assert.doesNotMatch(html, /Οδός|Provider display|<img/);
    }
});

test('missing components remain qualified, including lone numbers and feature-only legacy text', () => {
    const empty = { reverseGeocodingProvider: 'geoapify', reverseGeocodingStorageMode: 'persistent', reverseGeocodedAt: retained.reverseGeocodedAt };
    assert.match(renderLocationAddress({ ...empty, addressNumber: '001' }), /001.*Street address unavailable/);
    assert.match(renderLocationAddress({ ...empty, region: 'Region', country: 'Country' }), /Region, Country.*Street address unavailable/);
    const html = renderLocationAddress({ ...empty, fullAddress: '<Hotel>', resolvedFeatureName: '<Hotel>', resolvedFeatureType: 'amenity' });
    assert.match(html, /Address details unavailable.*Provider display text: &lt;Hotel&gt;.*Nearby mapped feature/);
    assert.doesNotMatch(html, /Address:<\/strong> &lt;Hotel/);
});

test('exact duplicates are omitted in first-occurrence order without altering substrings or Unicode', () => {
    assert.equal(locationAddress({ ...retained, streetName: 'Town Road', addressNumber: 'Town', postCode: 'Town', place: 'Town', region: 'Country' }).primary,
        'Town Road Town, Country');
    assert.equal(locationAddress({ ...retained, streetName: 'Å  Road', place: 'Å Road' }).primary,
        'Å  Road 10-12, 00123 Å Road, Region, Country');
});

test('broad precision survives without a feature name and hostile names are encoded', () => {
    assert.match(renderLocationAddress({ ...retained, resolvedFeatureType: 'state' }), /role="note".*Approximate address/);
    assert.match(renderLocationAddress({ ...retained, resolvedFeatureType: 'city', resolvedFeatureName: '<area>' }), /Mapped area: &lt;area&gt;/);
    assert.match(renderLocationAddress({ ...retained, resolvedFeatureType: 'street', resolvedFeatureName: 'Road' }), /Mapped feature: Road/);
});
