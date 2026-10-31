import assert from 'node:assert/strict';
import test from 'node:test';
import { genericImportFailure, submitTripImport, createTripImporter, planningLossExplanation } from '../../wwwroot/js/Areas/User/Trip/tripImportClient.js';

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

// The production page renders this field; the caller must copy its value into the header.
globalThis.document = { querySelector: () => ({ value: 'authentic-page-token' }) };

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


test('multipart import and duplicate-mode retry retain file/mode and send the page token header', async () => {
    const calls = [];
    const { handlers } = createHandlers(async (url, options) => {
        calls.push({ url, options });
        return { json: async () => ({ status: 'duplicate' }) };
    });
    await submitTripImport(sampleFile, 'Auto', handlers);
    await submitTripImport(sampleFile, 'CreateNew', handlers);
    assert.equal(calls.length, 2);
    for (const [index, { url, options }] of calls.entries()) {
        assert.equal(url, '/User/Trip/Import');
        assert.equal(options.method, 'POST');
        assert.equal(options.headers.RequestVerificationToken, 'authentic-page-token');
        assert.equal(options.headers['Content-Type'], undefined);
        assert.equal(options.body.get('mode'), index === 0 ? 'Auto' : 'CreateNew');
        assert.equal(await options.body.get('file').text(), 'kml');
    }
});

// The selected-file owner is the one used by the actual duplicate/refusal modal.
test('planning-loss refusal offers fixed choices, retains the file for Create copy and cancellation sends nothing', async () => {
    for (const cancel of [false, true]) {
        const calls = [];
        const { handlers, result } = createHandlers(async (_, options) => {
            calls.push(options.body);
            if (calls.length === 1) return new Response(JSON.stringify({ status: 'duplicate' }));
            if (calls.length === 2) return new Response(JSON.stringify({ status: 'error', code: 'planning_loss_requires_review', message: '<script>arbitrary server text</script>' }), { status: 409 });
            return new Response(JSON.stringify({ status: 'success', redirectUrl: '/User/Trip/Edit/copy' }));
        });
        let refusals = 0;
        handlers.onPlanningLoss = () => { refusals++; };
        const importer = createTripImporter(handlers);
        await importer.upload(sampleFile);
        await importer.update();
        assert.equal(result.duplicates, 1);
        assert.equal(refusals, 1);
        assert.deepEqual(result.errors, []);
        if (cancel) importer.cancel();
        await importer.createCopy();
        assert.equal(calls.length, cancel ? 2 : 3);
        if (!cancel) {
            assert.equal(calls[2].get('mode'), 'CreateNew');
            assert.equal(await calls[2].get('file').text(), 'kml');
            assert.deepEqual(result.redirects, ['/User/Trip/Edit/copy']);
        }
    }
});

test('specific refusal fallback uses only the fixed local explanation', async () => {
    const { handlers, result } = createHandlers(async () => new Response(JSON.stringify({ status: 'error', code: 'planning_loss_requires_review', message: 'Unsafe arbitrary text' }), { status: 409 }));
    await submitTripImport(sampleFile, 'Upsert', handlers);
    assert.deepEqual(result.errors, [planningLossExplanation]);
    assert.doesNotMatch(result.errors[0], /try again|Unsafe arbitrary/);
});
