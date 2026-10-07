import { defineConfig, devices } from '@playwright/test';

const baseUrl = process.env.WAYFARER_E2E_BASE_URL ?? 'http://localhost:5012';

export default defineConfig({
  testDir: './tests/e2e/trip-editor',
  outputDir: process.env.WAYFARER_E2E_OUTPUT ?? '.local/playwright/test-output',
  fullyParallel: false,
  // Keep shared runbook trip checks serialized across split spec files.
  workers: 1,
  forbidOnly: Boolean(process.env.CI),
  reporter: [['list'], ['html', { open: 'never', outputFolder: process.env.WAYFARER_E2E_REPORT ?? 'playwright-report' }]],
  use: {
    baseURL: baseUrl,
    // Managed runs keep credentials out of recorded requests/typing; attached mode stays distinct.
    trace: process.env.WAYFARER_E2E_MANAGED === '1' ? 'off' : 'retain-on-failure',
    video: process.env.WAYFARER_E2E_MANAGED === '1' ? 'off' : 'retain-on-failure',
    screenshot: 'only-on-failure'
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] }
    }
  ]
});
