import assert from 'node:assert/strict';
import test from 'node:test';
import { canonicalNoteImageSource, noteImageSourceForDisplay, registerNoteImageBlot } from '../../wwwroot/js/util/notes-images.js';

globalThis.window = { location: { origin: 'https://wayfarer.example.test' } };
const source = 'https://images.example.test/photo?a=1&entity=&amp;&q=two+words%20here';
const proxy = `/Public/ProxyImage?url=${encodeURIComponent(source)}`;

test('display wraps external sources once and save restores their exact original URL', () => {
    assert.equal(noteImageSourceForDisplay(source), proxy);
    for (const value of [proxy, window.location.origin + proxy,
        `/Public/ProxyImage?url=${encodeURIComponent(proxy)}`]) {
        assert.equal(noteImageSourceForDisplay(value), proxy);
        assert.equal(canonicalNoteImageSource(value), source);
    }
});

test('local, generated and unsupported sources retain existing URL policy', () => {
    for (const value of ['/thumbs/trips/map.jpg?v=1', 'https://wayfarer.example.test/icons/a.png',
        'data:image/png;base64,AQI=', '../relative.jpg', 'ftp://example.test/a']) {
        assert.equal(noteImageSourceForDisplay(value), value);
    }
});

// Browsers resolve network-path references before fetching, including backslashes and ignored controls.
test('scheme-relative sources are neutralized directly and through nested same-origin proxies', () => {
    for (const source of ['//images.example.test/negative.png', '//wayfarer.example.test/local.png',
        '\\\\images.example.test/negative.png', '/\\images.example.test/negative.png',
        '\u0000/\n/images.example.test/negative.png']) {
        const proxy = `/Public/ProxyImage?url=${encodeURIComponent(source)}`;
        for (const value of [source, proxy, window.location.origin + proxy,
            `/Public/ProxyImage?url=${encodeURIComponent(window.location.origin + proxy)}`]) {
            assert.equal(canonicalNoteImageSource(value), '');
            assert.equal(noteImageSourceForDisplay(value), '');
        }
    }
});

test('Quill image creation receives the display source before assigning src and emits canonical delta values', () => {
    let registered;
    const Quill = {
        import: () => class {
            static create(value) { return { src: value }; }
            static value(node) { return node.src; }
        },
        register: blot => { registered = blot; }
    };
    registerNoteImageBlot(Quill);
    const image = registered.create(source);
    assert.equal(image.src, proxy);
    assert.equal(registered.value(image), source);
});
