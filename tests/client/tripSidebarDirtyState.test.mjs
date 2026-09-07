import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { parse, compileScript, compileTemplate } from '@vue/compiler-sfc';
import { build } from 'esbuild';
import { renderToString } from 'vue/server-renderer';
import { createSSRApp, h, effectScope, nextTick, proxyRefs, shallowReactive, ref } from 'vue';

// Exercise the sidebar's real child event wiring and the aggregate passed to its unload owner.
const filename = 'ClientApps/trip-editor/src/components/TripSidebar.vue';
const { descriptor } = parse(await readFile(filename, 'utf8'), { filename });
const script = compileScript(descriptor, { id: 'sidebar-dirty-test' });
const template = compileTemplate({ source: descriptor.template.content, filename, id: 'sidebar-dirty-test',
  compilerOptions: { bindingMetadata: script.bindings } });
const bundled = await build({ stdin: { contents: `${script.content}\n${template.code}`, loader: 'ts',
  resolveDir: 'ClientApps/trip-editor/src/components' }, bundle: true, write: false, format: 'esm', platform: 'node',
  external: ['vue'], plugins: [{ name: 'child-boundaries', setup(builder) {
    builder.onLoad({ filter: /\.vue$/ }, args => ({ contents: `export default { name: '${args.path.split(/[\\/]/).pop().replace('.vue', '')}' };` }));
  } }] });
const code = bundled.outputFiles[0].text.replaceAll('from "vue"', `from "${import.meta.resolve('vue')}"`);
const { default: component, render } = await import(`data:text/javascript;base64,${Buffer.from(code + '\n//# sourceURL=sidebar-dirty-test.mjs').toString('base64')}`);

test('saved segment clears the page warning while independent region edits remain protected', async () => {
  const props = shallowReactive({ hiddenSegmentIds: new Set(),
    editorSurface: { activeTarget: ref(null), isMapWorkActive: ref(false) },
    state: { tagOrder: [], segmentOrder: [], segmentsById: {}, regionOrder: [], regionsById: {},
      placeOrderByRegionId: {}, areaOrderByRegionId: {}, options: {}, metadata: { name: "Trip" }, permissions: {} } });
  const input = props;
  const scope = effectScope();
  const bindings = scope.run(() => proxyRefs(component.setup(input, { expose() {}, emit() {} })));
  const find = (node, name) => {
    if (node?.type?.name === name) return node;
    for (const child of Array.isArray(node?.children) ? node.children : []) {
      const match = find(child, name);
      if (match) return match;
    }
  };
  const tree = async () => {
    let vnode;
    await renderToString(createSSRApp({ render() {
      vnode = render({}, [], input, bindings, {}, {});
      return h('div');
    } }));
    return vnode;
  };
  const dirty = async (name, value) => {
    find(await tree(), name).props.onDirtyStateChanged(value);
    await nextTick();
  };
  const warning = async () => find(await tree(), 'MetadataEditor').props['has-region-draft-changes'];
  try {
    await dirty('SegmentManager', true);
    assert.equal(await warning(), true);
    await dirty('SegmentManager', false);
    assert.equal(await warning(), false, 'successful segment Save must clear the navigation warning');
    await dirty('RegionManager', true);
    await dirty('SegmentManager', true);
    await dirty('SegmentManager', false);
    assert.equal(await warning(), true, 'region edits must survive segment Save');
    await dirty('SegmentManager', true);
    await dirty('RegionManager', false);
    assert.equal(await warning(), true, 'segment edits must survive region Save');
    await dirty('SegmentManager', false);
    assert.equal(await warning(), false);
  } finally { scope.stop(); }
});
