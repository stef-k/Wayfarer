import { readFile } from 'node:fs/promises';
import { parse, compileScript } from '@vue/compiler-sfc';
import { build } from 'esbuild';
import { createRenderer, h, nextTick, proxyRefs, shallowReactive, shallowRef } from 'vue';

// Run the production exit actions and unload guard with controlled user/API responses.
const filename = 'ClientApps/trip-editor/src/components/MetadataEditor.vue';
const { descriptor } = parse(await readFile(filename, 'utf8'), { filename });
const script = compileScript(descriptor, { id: 'editor-exit-test' });
const bundled = await build({ stdin: { contents: script.content + '\nexport { providePlanningWorkspace } from "../planning/planningState";', loader: 'ts',
  resolveDir: 'ClientApps/trip-editor/src/components' }, bundle: true, write: false, format: 'esm', platform: 'node',
  external: ['vue'], plugins: [{ name: 'exit-boundaries', setup(builder) {
    builder.onLoad({ filter: /\.vue$/ }, () => ({ contents: 'export default {};' }));
    builder.onLoad({ filter: /notesHtml\.ts$/ }, () => ({ contents: 'export const normalizeNotesHtml = value => value ?? "";' }));
    builder.onLoad({ filter: /useConfirmDialog\.ts$/ }, () => ({ contents: 'export const confirm = options => globalThis.exitConfirm(options);' }));
  } }] });
const code = bundled.outputFiles[0].text.replaceAll('from "vue"', `from "${import.meta.resolve('vue')}"`);
export const { default: component, providePlanningWorkspace } = await import(`data:text/javascript;base64,${Buffer.from(code + '\n//# sourceURL=editor-exit-test.mjs').toString('base64')}`);

// Model the server's compatible display projection plus exact original-value evidence.
export const withOriginals = metadata => ({ contributionPolicy: 'CC-BY-4.0', credits: [], ...metadata, coverImage: metadata.coverImage ?? null, originalValues: { name: metadata.name, notesHtml: metadata.notesHtml ?? null,
  contributionPolicy: metadata.contributionPolicy ?? 'CC-BY-4.0', credits: metadata.credits ?? [],
  isPublic: metadata.isPublic, coverImage: metadata.coverImage ? { rawUrl: metadata.coverImage.rawUrl } : null,
  center: metadata.center ?? { latitude: null, longitude: null }, zoom: metadata.zoom ?? null } });

// Mount the real draft owner under a reactive parent so Vue schedules child prop updates.
export const mountDraft = (withPlanning = false) => {
  let editor;
  let planning;
  const props = shallowReactive({ metadata: withOriginals({ id: 'trip', name: 'Trip', isPublic: false, shareProgressEnabled: false, notesHtml: '',
    center: { latitude: 37.12345601, longitude: 23 }, zoom: 9 }),
    capturedMapView: null, tagsBySlug: {}, tagOrder: [], tagOptions: {}, hasRegionDraftChanges: false,
    tripIndexUrl: '/User/Trip', editorEndpoint: '/editor', antiforgeryToken: 'test-token', editorSurface: {
      registerTargetHandler: () => () => {}, isTargetActive: () => false
    }, autoOpen: false });
  // Only rendering is stubbed; production setup, watchers and emitted events belong to the child.
  const child = { ...component, setup(childProps, context) {
    editor = proxyRefs(component.setup(childProps, context));
    return () => null;
  } };
  const renderer = createRenderer({ createComment: () => ({}), insert() {}, remove() {},
    parentNode: () => ({}), nextSibling: () => null });
  const app = renderer.createApp({ setup() {
    if (withPlanning) planning = providePlanningWorkspace(shallowRef({
      tripId: props.metadata.id, metadata: { ...props.metadata, planning: null }
    }), props.editorEndpoint, props.antiforgeryToken);
    return () => h(child, { ...props,
      onSaved: metadata => { props.metadata = metadata; },
      onMutationApplied: result => {
        if (result.affected.metadata) props.metadata = result.affected.metadata;
        if (!Array.isArray(result.data)) return;
        props.tagOrder = result.data.map(tag => tag.slug);
        props.tagsBySlug = Object.fromEntries(result.data.map(tag => [tag.slug, tag]));
      }
    });
  } });
  app.mount({});
  return { editor, planning, props, dispose: () => app.unmount(), capture: async (latitude, zoom = 10) => {
    props.capturedMapView = { center: { latitude, longitude: 24 }, zoom };
    await nextTick();
  } };
};
