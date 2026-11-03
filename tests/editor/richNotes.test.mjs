import assert from 'node:assert/strict';
import test from 'node:test';
import { build } from 'esbuild';
import { chromium } from '@playwright/test';

/** Execute native commands in a real DOM, without a backend, recovery wrapper or alternate editor model. */
test('shared native editor commands preserve text/references and finite styles', async t => {
    const bundle = await build({ stdin: { contents: `export * from './ClientApps/rich-notes/editor';
        export { normalizeNotesHtml } from './ClientApps/trip-editor/src/notes/notesHtml';`, resolveDir: process.cwd(), loader: 'ts' },
        bundle: true, write: false, format: 'iife', globalName: 'Notes' });
    const browser = await chromium.launch();
    t.after(() => browser.close());
    const page = await browser.newPage();
    let direct = 0;
    await page.route('**/*', route => {
        if (!route.request().url().startsWith('http://notes.test/')) direct++;
        return route.fulfill({ contentType: 'text/html', body: '<label id="label">Notes</label><div id="notes"></div>' });
    });
    await page.goto('http://notes.test/');
    await page.addScriptTag({ content: bundle.outputFiles[0].text });
    const run = async (html, action) => page.evaluate(({ html, action }) => {
        window.handle?.dispose();
        document.getElementById('notes').replaceChildren();
        window.feedback = [];
        window.handle = Notes.createNotesEditor(document.getElementById('notes'), html, 'label', message => feedback.push(message));
        return Function('handle', action)(handle);
    }, { html, action });
    await t.test('native parse differences do not become authored changes; undo restores exact input', async () => {
        const input = '<ol><p>readable intro</p><li data-list="bullet">item</li></ol>';
        const result = await run(input, `const native = handle.editor.getHTML();
            handle.editor.commands.focus('end'); const untouched = handle.getValue();
            handle.editor.commands.insertContent('!'); const edited = handle.getValue();
            handle.editor.commands.undo(); return { native, untouched, edited, undone: handle.getValue() };`);
        assert.equal(result.untouched, input); assert.equal(result.undone, input);
        assert.match(result.native, /readable intro/); assert.match(result.native, /<ol>/);
        assert.doesNotMatch(result.native, /data-list/); assert.match(result.edited, /item!/);
        console.log('Native historical-list observation:', result.native);
    });
    await t.test('range Remove formatting preserves overlapping text, links and images', async () => {
        const result = await run('<h2 style="text-align: right"><a href="/safe"><strong><em><u><s><span style="font-family: serif">Καλημέρα 日本語</span></s></u></em></strong></a><img src="https://images.example.test/a"></h2>', `
            handle.editor.commands.selectAll(); handle.removeFormatting(); return handle.editor.getHTML();`);
        assert.match(result, /<p><a href="\/safe">Καλημέρα 日本語<\/a><img src="https:\/\/images.example.test\/a">/);
        assert.doesNotMatch(result, /<h2|<strong|<em>|<u>|<s>|font-family|text-align/);
    });
    await t.test('caret Remove formatting clears only pending visual marks', async () => {
        const result = await run('<p><a href="/safe"><strong>old</strong></a></p>', `
            handle.editor.commands.setTextSelection(2); handle.removeFormatting();
            handle.editor.commands.insertContent('new'); return handle.editor.getHTML();`);
        assert.match(result, /<strong>o<\/strong>new<strong>ld<\/strong>/);
        assert.match(result, /href="\/safe"/);
    });
    await t.test('native font/alignment and client comparisons retain only finite declarations', async () => {
        const result = await run('<p>text</p>', `handle.editor.commands.selectAll();
            handle.editor.chain().setFontFamily('monospace').setTextAlign('justify').toggleUnderline().run();
            return { html: handle.getValue(), unsafe: Notes.normalizeNotesHtml('<p style="text-align: right; background:url(x)"><span style="font-family:serif;color:red">safe</span></p>') };`);
        assert.match(result.html, /text-align: justify;/); assert.match(result.html, /font-family: monospace;/);
        assert.match(result.html, /<u>/);
        assert.equal(result.unsafe, '<p style="text-align: right;"><span style="font-family: serif;">safe</span></p>');
    });
    await t.test('selection and pending marks survive dialogs; explicit adoption resets history', async () => {
        const result = await run('<p>text</p>', `handle.editor.commands.focus('end'); handle.editor.commands.setBold();
            handle.saveSelection(); handle.editor.commands.setTextSelection(1); handle.restoreSelection();
            handle.editor.commands.insertContent(' bold'); const html = handle.getValue();
            handle.adopt('<p>adopted</p>'); handle.editor.commands.undo(); return { html, adopted: handle.getValue() };`);
        assert.match(result.html, /text<strong> bold<\/strong>/); assert.equal(result.adopted, '<p>adopted</p>');
    });
    // Pair actual Chromium declaration parsing with RichNotesTests.StandardStylesAreFiniteAndCanonical.
    await t.test('finite priority whitespace is canonical while malformed and quoted values stay forbidden', async () => {
        const inputs = [
            ['span', 'font-family:serif ! important', 'font-family: serif;'],
            ['span', 'font-family:serif !\tIMPORTANT; font-family:monospace', 'font-family: serif;'],
            ['p', 'text-align:right ! important; text-align:left', 'text-align: right;'],
            ['span', 'font-family:serif ! important extra', null],
            ['span', "font-family:'serif ! important'", null]
        ];
        for (const [tag, style, expected] of inputs) {
            const html = `<${tag} style="${style}">a</${tag}>`;
            const result = await page.evaluate(html => Notes.normalizeNotesHtml(html), html);
            assert.equal(result, expected ? `<${tag} style="${expected}">a</${tag}>` : `<${tag}>a</${tag}>`, html);
        }
    });
    await t.test('native HTML paste accepts image-free prose mentioning data:image', async () => {
        const result = await run('', `const data = new DataTransfer();
            data.setData('text/html', '<p>Use data:image/png in this explanation.</p>');
            const event = new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true });
            handle.editor.view.dom.dispatchEvent(event);
            return { html: handle.getValue(), feedback };`);
        assert.equal(result.html, '<p>Use data:image/png in this explanation.</p>');
        assert.deepEqual(result.feedback, []);
    });
    // Native capture must reject the whole transfer before ProseMirror can insert its surviving text.
    for (const [name, image] of [
        ['entity-encoded data image', '<img src="data&#58;image/png;base64,AA">'],
        ['file URL image', '<img src="file:///tmp/a.png">'],
        ['mixed safe and unsafe images', '<img src="https://images.example.test/ok"><img src="data&#58;image/png;base64,AA">']
    ]) {
        for (const type of ['paste', 'drop']) {
            await t.test(`native ${type} rejects ${name} without partial insertion`, async () => {
                const html = `<p>before${image}after</p>`;
                const result = await run('<p>unchanged</p>', `const before = handle.editor.getHTML();
                    let changes = 0; handle.onChange(() => changes++);
                    const data = new DataTransfer(); data.setData('text/html', ${JSON.stringify(html)});
                    const event = ${type === 'paste'
                        ? "new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true })"
                        : "new DragEvent('drop', { dataTransfer: data, bubbles: true, cancelable: true })"};
                    handle.editor.view.dom.dispatchEvent(event);
                    return { before, after: handle.editor.getHTML(), value: handle.getValue(), changes,
                        rejected: event.defaultPrevented, feedback };`);
                assert.equal(result.rejected, true);
                assert.equal(result.after, result.before);
                assert.equal(result.value, '<p>unchanged</p>');
                assert.equal(result.changes, 0);
                assert.ok(result.feedback.some(message => message.includes('HTTP(S) image URL')));
            });
        }
    }
    await t.test('native external-image paste retains canonical URLs and proxied previews', async () => {
        const result = await run('', `const data = new DataTransfer();
            data.setData('text/html', '<p>before<img src="/Public/ProxyImage?url=https%3A%2F%2Fimages.example.test%2Fpasted">after</p>');
            const event = new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true });
            handle.editor.view.dom.dispatchEvent(event);
            return { html: handle.getValue(), sources: [...handle.editor.view.dom.querySelectorAll('img[src]')].map(image => image.getAttribute('src')) };`);
        assert.equal(result.html, '<p>before<img src="https://images.example.test/pasted">after</p>');
        assert.deepEqual(result.sources, ['/Public/ProxyImage?url=https%3A%2F%2Fimages.example.test%2Fpasted']);
    });
    await t.test('native paste, split/sink/lift list commands and safe link schemes', async () => {
        const result = await run('<p>start</p>', `handle.editor.commands.focus('end');
            handle.editor.view.pasteHTML('<p><strong>rich</strong> <a href="javascript:bad()">unsafe</a></p>');
            handle.editor.view.pasteText(' plain');
            handle.editor.commands.toggleBulletList(); handle.editor.commands.splitListItem('listItem');
            handle.editor.commands.insertContent('nested'); handle.editor.commands.sinkListItem('listItem');
            const nested = handle.editor.getHTML(); handle.editor.commands.liftListItem('listItem');
            const data = new DataTransfer(); data.setData('text/html', '<img src="data:image/png;base64,AA">');
            const event = new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true });
            handle.editor.view.dom.dispatchEvent(event);
            const files = new DataTransfer(); files.items.add(new File(['bytes'], 'local.png', { type: 'image/png' }));
            const drop = new DragEvent('drop', { dataTransfer: files, bubbles: true, cancelable: true });
            handle.editor.view.dom.dispatchEvent(drop);
            return { droppedFileRejected: drop.defaultPrevented, nested, html: handle.editor.getHTML(), rejected: event.defaultPrevented,
                safe: ['/relative', 'https://example.test', 'mailto:a@b.test', 'tel:+1', 'custom:bad', 'java\\tscript:bad'].map(Notes.isSafeNotesLink) };`);
        assert.match(result.nested, /<ul>.*<ul>/); assert.match(result.html, /<strong>rich<\/strong>/);
        assert.doesNotMatch(result.html, /javascript:|data:image/); assert.equal(result.rejected, true); assert.equal(result.droppedFileRejected, true);
        assert.match(result.html, / plain/);
        assert.deepEqual(result.safe, [true, true, true, true, false, false]);
    });
    await t.test('native keyboard list nesting and meaningful whitespace remain editable', async () => {
        await run('<blockquote><h6>  A&nbsp;  B<br>C </h6></blockquote>', String.raw`
            const text = handle.editor.getText({ blockSeparator: '\n' });
            if (!text.includes('  A\u00a0  B\nC ')) throw new Error('Whitespace or hard break was lost');
            handle.adopt('<ul><li><p>first</p></li></ul>');`);
        // Keyboard events require a focused real surface, rather than the vendor's scheduled focus command.
        await page.locator('#notes [contenteditable=true]').focus();
        await page.keyboard.press('Control+End');
        await page.keyboard.press('Enter'); await page.keyboard.type('second'); await page.keyboard.press('Tab');
        assert.match(await page.evaluate(() => handle.editor.getHTML()), /<ul>.*<ul>/);
        await page.keyboard.press('Shift+Tab');
        assert.equal(await page.locator('#notes ul ul').count(), 0);
    });
    await t.test('failed image references remain canonical and never load directly', async () => {
        const result = await run('<p><img src="https://images.example.test/old"></p>', `
            const before = handle.getValue(); const rejected = handle.insertImage('data:image/png;base64,AA');
            handle.editor.commands.focus('end'); handle.insertImage('https://images.example.test/new');
            return { before, rejected, value: handle.getValue(), sources: [...document.querySelectorAll('#notes img[src]')].map(x => x.getAttribute('src')) };`);
        assert.equal(result.rejected, false); assert.match(result.before, /https:\/\/images.example.test\/old/);
        assert.doesNotMatch(result.value, /ProxyImage/); assert.equal(result.sources.length, 2);
        assert.ok(result.sources.every(source => source.startsWith('/Public/ProxyImage?url=')));
        await page.waitForFunction(() => feedback.some(message => message.includes('Image preview unavailable')));
        assert.equal(direct, 0);
    });
});
