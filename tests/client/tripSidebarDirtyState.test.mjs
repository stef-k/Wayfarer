import assert from 'node:assert/strict';
import test from 'node:test';
import { nextTick } from 'vue';
import { withApp } from './placeEditorHost.mjs';

// Exercise real sidebar child-event wiring and injection inside the existing mounted App seam.
test('saved segment clears the page warning while independent region edits remain protected', () => withApp(async ({ sidebar, parent, sidebarTree }) => {
  assert.equal(sidebar.planningWorkspace, parent.planningWorkspace, 'planning injection belongs to the mounted parent workspace');
  const find = (node, name) => {
    if ((node?.type?.name ?? node?.type?.__name) === name) return node;
    for (const child of Array.isArray(node?.children) ? node.children : []) {
      const match = find(child, name);
      if (match) return match;
    }
  };
  const dirty = async (name, value) => {
    find(sidebarTree(), name).props.onDirtyStateChanged(value);
    await nextTick();
  };
  const warning = async () => find(sidebarTree(), 'MetadataEditor').props['has-region-draft-changes'];
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
}));
