import { defineConfig, devices } from '@playwright/test';

const baseUrl = process.env.WAYFARER_E2E_BASE_URL ?? 'http://localhost:5012';

export default defineConfig({
  testDir: './tests/e2e/trip-editor',
  outputDir: '.local/playwright/test-output',
  fullyParallel: false,
  // Keep shared runbook trip checks serialized across split spec files.
  workers: 1,
  forbidOnly: Boolean(process.env.CI),
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]],
  use: {
    baseURL: baseUrl,
    trace: 'retain-on-failure',
    video: 'retain-on-failure',
    screenshot: 'only-on-failure'
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] }
    }
  ]
});
