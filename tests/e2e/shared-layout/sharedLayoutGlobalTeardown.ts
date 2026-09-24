import { execFileSync } from 'node:child_process';

/** The launcher and teardown share exact-PID ownership validation and idempotent tree removal. */
export default async function sharedLayoutGlobalTeardown(): Promise<void> {
  try {
    execFileSync(process.platform === 'win32' ? 'powershell' : 'pwsh', [
      '-NoProfile', '-File', 'tools/start-shared-layout-e2e-host.ps1', '-Teardown'
    ], { stdio: 'inherit' });
  } catch (error) {
    // Preserve the primary Playwright failure; leave the PID authority/residue for diagnosis.
    throw new Error(`Shared-layout cleanup failed: ${String(error).slice(0, 500)}`);
  }
}
