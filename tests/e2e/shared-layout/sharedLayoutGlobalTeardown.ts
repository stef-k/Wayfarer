import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const pidFile = path.join(process.env.TEMP ?? process.env.TMP ?? '.', 'wayfarer-shared-layout-e2e-host.pid');

/** Stops only the isolated host process recorded by the shared-layout launcher. */
export default async function sharedLayoutGlobalTeardown(): Promise<void> {
  if (!fs.existsSync(pidFile)) return;

  const processId = Number(fs.readFileSync(pidFile, 'utf8').trim());
  if (Number.isInteger(processId) && processId > 0) {
    const command = `$processId = ${processId}; if (Get-NetTCPConnection -State Listen -LocalPort 7150 -ErrorAction SilentlyContinue | Where-Object OwningProcess -eq $processId) { Stop-Process -Id $processId -Force }`;
    execFileSync('powershell', ['-NoProfile', '-Command', command], { stdio: 'inherit' });
  }

  fs.rmSync(pidFile, { force: true });
}
