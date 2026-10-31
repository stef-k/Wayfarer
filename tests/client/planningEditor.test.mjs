import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { parse, compileScript } from '@vue/compiler-sfc';
import { build } from 'esbuild';
import { createRenderer, h, nextTick, provide, proxyRefs, ref } from 'vue';

// Mount production draft/control owners; only DOM rendering and the existing confirmation dialog are substituted.
const result = await build({ stdin: { contents: `export { default as Editor } from './PlanningEditor.vue';
  export { default as Value } from './PlanningValueInput.vue'; export * from './planningState';`, loader: 'ts',
  resolveDir: 'ClientApps/trip-editor/src/planning' }, bundle: true, write: false, format: 'esm', platform: 'node', external: ['vue'],
  plugins: [{ name: 'planning-components', setup(builder) {
    builder.onLoad({ filter: /\.vue$/ }, async args => {
      const { descriptor } = parse(await readFile(args.path, 'utf8'), { filename: args.path });
      return { loader: 'ts', contents: compileScript(descriptor, { id: args.path }).content };
    });
    builder.onLoad({ filter: /useConfirmDialog\.ts$/ }, () => ({ contents: 'export const confirm = async () => true;' }));
  } }] });
const code = result.outputFiles[0].text.replaceAll('from "vue"', `from "${import.meta.resolve('vue')}"`);
const { Editor, Value, createPlanningWorkspace, planningWorkspaceKey } = await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`);
const target = { key: 'metadata', identity: 'metadata', kind: 'metadata', mode: 'edit' };
const renderer = createRenderer({ createComment: () => ({}), insert() {}, remove() {}, parentNode: () => ({}), nextSibling: () => null });
const mount = (component, props, workspace) => {
  let controls;
  const child = { ...component, setup(values, context) { controls = proxyRefs(component.setup(values, context)); return () => null; } };
  const app = renderer.createApp({ setup() { if (workspace) provide(planningWorkspaceKey, workspace); return () => h(child, props); } });
  app.mount({});
  return { get controls() { return controls; }, dispose: () => app.unmount() };
};
const fresh = () => {
  const graph = ref({ tripId: 'trip', metadata: { id: 'trip', planning: null } });
  const workspace = createPlanningWorkspace(graph, '/api/trips/trip/editor', 'anti');
  const editor = mount(Editor, { target }, workspace);
  return { graph, workspace, editor };
};
const acknowledgement = (request, overrides = {}) => new Response(JSON.stringify({ contract: 'planning-1', success: true, applied: true,
  targetKind: 'trip', targetId: 'trip', parentId: 'trip', planning: request.value ?? null, ...overrides }));

test('quiet rough date saves, then actual Add time and another entry retain date and identity', async () => {
  const oldFetch = globalThis.fetch;
  const { graph, workspace, editor } = fresh();
  try {
    assert.equal(editor.controls.draft.entries.length, 0);
    assert.equal(workspace.hasChanges.value, false);
    editor.controls.add();
    const entry = editor.controls.draft.entries[0];
    entry.when.value.local = '2027-03-02';
    const id = entry.id;
    let submitted;
    globalThis.fetch = async (_, init) => { submitted = JSON.parse(init.body); return acknowledgement(submitted); };
    await workspace.save(editor.controls.draft);
    assert.equal(graph.value.metadata.planning.entries[0].kind, undefined);
    assert.equal(submitted.expected.planning, null);
    assert.equal(workspace.hasChanges.value, false);
    const value = mount(Value, { value: entry.when.value, label: 'At' });
    await value.controls.precision('dateTime');
    value.controls.timePart = '10:15';
    assert.equal(entry.when.value.local, '2027-03-02T10:15');
    assert.equal(entry.id, id);
    assert.equal(entry.when.value.zone, undefined);
    editor.controls.add();
    editor.controls.draft.entries[1].when.value.local = '2027-03-04';
    await workspace.save(editor.controls.draft);
    assert.equal(graph.value.metadata.planning.entries.length, 2);
    assert.equal(graph.value.metadata.planning.entries[0].id, id);
    value.dispose();
  } finally { editor.dispose(); globalThis.fetch = oldFetch; }
});

test('known rejection keeps complete draft and original; late success advances only the submitted base', async () => {
  const oldFetch = globalThis.fetch;
  const { workspace, editor } = fresh();
  try {
    editor.controls.add();
    const draft = editor.controls.draft;
    draft.entries[0].when.value.local = 'invalid draft';
    globalThis.fetch = async () => new Response(JSON.stringify({ code: 'planning-value-invalid', detail: 'Correct the value.' }), { status: 400 });
    await workspace.save(draft);
    assert.equal(draft.entries[0].when.value.local, 'invalid draft');
    assert.equal(draft.original, null);
    assert.equal(draft.uncertain, false);
    draft.entries[0].when.value.local = '2027-03-02';
    let reply;
    let submitted;
    globalThis.fetch = async (_, init) => { submitted = JSON.parse(init.body); return new Promise(resolve => { reply = resolve; }); };
    const saving = workspace.save(draft);
    draft.entries[0].when.value.local = '2027-03-03';
    reply(acknowledgement(submitted));
    await saving;
    assert.equal(draft.entries[0].when.value.local, '2027-03-03');
    assert.equal(draft.original.entries[0].when.value.local, '2027-03-02');
    assert.equal(workspace.hasChanges.value, true);
    await nextTick();
  } finally { editor.dispose(); globalThis.fetch = oldFetch; }
});

test('malformed success holds replay across remount and public-looking equality never settles the attempt', async () => {
  const oldFetch = globalThis.fetch;
  const { graph, workspace, editor } = fresh();
  let second;
  try {
    editor.controls.add();
    const draft = editor.controls.draft;
    draft.entries[0].when.value.local = '2027-03-02';
    let calls = 0;
    globalThis.fetch = async () => { calls++; return new Response('{}'); };
    await workspace.save(draft);
    assert.equal(draft.uncertain, true);
    editor.dispose();
    graph.value.metadata.planning = { format: 1, entries: JSON.parse(JSON.stringify(draft.entries)) };
    second = mount(Editor, { target }, workspace);
    assert.equal(second.controls.draft, draft);
    assert.equal(draft.original, null);
    await workspace.save(draft);
    await workspace.review(draft);
    assert.equal(calls, 1);
    assert.equal(workspace.hasChanges.value, true);
  } finally { second?.dispose(); globalThis.fetch = oldFetch; }
});

test('duplicate-member or false-applied success cannot retire the submitted original', async () => {
  const oldFetch = globalThis.fetch;
  try {
    for (const duplicate of [true, false]) {
      const { workspace, editor } = fresh();
      editor.controls.add();
      const draft = editor.controls.draft;
      draft.entries[0].when.value.local = '2027-03-02';
      globalThis.fetch = async (_, init) => {
        const request = JSON.parse(init.body);
        const normal = await acknowledgement(request, duplicate ? {} : { applied: false }).text();
        return new Response(duplicate ? normal.replace('"success":true', '"success":false,"success":true') : normal);
      };
      await workspace.save(draft);
      assert.equal(draft.uncertain, true);
      assert.equal(draft.original, null);
      editor.dispose();
    }
  } finally { globalThis.fetch = oldFetch; }
});
