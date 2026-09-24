/** Filesystem boundary for repository-owned test artifacts; links are never traversed. */
import fs from 'node:fs';
import path from 'node:path';

/** Checks every existing component, including the anchor, before inspecting a descendant. */
export function assertOrdinaryPath(root, candidate) {
  const anchor = path.resolve(root);
  const target = path.resolve(candidate);
  const relative = path.relative(anchor, target);
  if (!relative || relative.startsWith(`..${path.sep}`) || relative === '..' || path.isAbsolute(relative)) {
    throw new Error(`Not a child of the ownership root: ${target}`);
  }
  let current = path.parse(anchor).root;
  for (const part of target.slice(current.length).split(path.sep)) {
    current = path.join(current, part);
    const item = fs.lstatSync(current, { throwIfNoEntry: false });
    if (!item) break;
    if (item.isSymbolicLink() || !item.isDirectory()) throw new Error(`Not an ordinary directory: ${current}`);
  }
  return target;
}

/** Measures without following links and refuses an entire tree containing links or special files. */
export function inspectTree(directory) {
  const item = fs.lstatSync(directory);
  if (item.isSymbolicLink() || (!item.isDirectory() && !item.isFile())) {
    throw new Error(`Linked or special entry: ${directory}`);
  }
  let bytes = item.isFile() ? item.size : 0;
  let newest = item.mtimeMs;
  if (item.isDirectory()) {
    for (const name of fs.readdirSync(directory)) {
      const child = inspectTree(path.join(directory, name));
      bytes += child.bytes;
      newest = Math.max(newest, child.newest);
    }
  }
  return { bytes, newest };
}

/** Deletes only a validated ordinary child, rechecking immediately before removal. */
export function removeOwnedDirectory(root, candidate) {
  const target = assertOrdinaryPath(root, candidate);
  if (!fs.existsSync(target)) return;
  inspectTree(target);
  fs.rmSync(target, { recursive: true, force: true });
}
