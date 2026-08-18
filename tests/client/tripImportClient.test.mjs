import assert from 'node:assert/strict';
import test from 'node:test';
import { genericImportFailure, submitTripImport } from '../../wwwroot/js/Areas/User/Trip/tripImportClient.js';

const createHandlers = (fetchImpl) => {
    const result = { duplicates: 0, errors: [], notices: [], redirects: [] };
    return {
        result,
        handlers: {
            fetchImpl,
            onRedirect: url => result.redirects.push(url),
            onDuplicate: () => result.duplicates++,
            showNotices: notices => result.notices.push(...notices),
            showError: message => result.errors.push(message)
        }
    };
};

const sampleFile = new Blob(['kml'], { type: 'application/vnd.google-earth.kml+xml' });

test('reports bounded notices and follows the canonical success URL', async () => {
    let receiver;
    const { handlers, result } = createHandlers(function () {
        receiver = this;
        return Promise.resolve({
            json: async () => ({
                status: 'success',
                redirectUrl: '/User/Trip/Edit/test-trip',
                notices: [{ code: 'generic_route_simplified', originalCoordinateCount: 1500, resultingCoordinateCount: 400 }]
            })
        });
    });

    await submitTripImport(sampleFile, 'Auto', handlers);

    assert.equal(receiver, globalThis);
    assert.deepEqual(result.redirects, ['/User/Trip/Edit/test-trip']);
    assert.equal(result.notices.length, 1);
});

test('uses the fixed message when fetch rejects', async () => {
    const { handlers, result } = createHandlers(async () => { throw new Error('network detail'); });

    await submitTripImport(sampleFile, 'Auto', handlers);

    assert.deepEqual(result.errors, [genericImportFailure]);
});

test('uses the fixed message when JSON is malformed', async () => {
    const { handlers, result } = createHandlers(async () => ({ redirected: false, json: async () => { throw new SyntaxError('raw body'); } }));

    await submitTripImport(sampleFile, 'Auto', handlers);

    assert.deepEqual(result.errors, [genericImportFailure]);
});

test('uses the fixed message for a non-JSON response without reading text', async () => {
    const { handlers, result } = createHandlers(async () => ({
        redirected: false,
        json: async () => { throw new SyntaxError('html response'); },
        text: () => { throw new Error('response body must not be read'); }
    }));

    await submitTripImport(sampleFile, 'Auto', handlers);

    assert.deepEqual(result.errors, [genericImportFailure]);
});

test('keeps the duplicate import callback compatible', async () => {
    const { handlers, result } = createHandlers(async () => ({ redirected: false, json: async () => ({ status: 'duplicate', tripId: 'ignored-by-client' }) }));

    await submitTripImport(sampleFile, 'Auto', handlers);

    assert.equal(result.duplicates, 1);
    assert.deepEqual(result.errors, []);
});
