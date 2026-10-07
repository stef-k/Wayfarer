/** Selects Python and forwards the npm command; Python owns every browser lifecycle phase. */
import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const script = fileURLToPath(new URL('./browser_e2e.py', import.meta.url));
const executable = process.platform === 'win32' ? 'python' : 'python3';
const child = spawn(executable, [script, ...process.argv.slice(2)], {
  cwd: path.dirname(path.dirname(script)), stdio: 'inherit', env: process.env
});
// Windows console events already reach Python. Node's Windows kill would bypass finally.
for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => { if (process.platform !== 'win32') child.kill(signal); });
}
child.once('error', error => {
  console.error(`Unable to launch Python browser supervisor: ${error.code ?? error.name}`);
  process.exitCode = 1;
});
child.once('exit', (code, signal) => { process.exitCode = code ?? (signal === 'SIGINT' ? 130 : 1); });
