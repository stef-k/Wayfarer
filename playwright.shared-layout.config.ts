import { defineConfig, devices } from '@playwright/test';

const baseURL = process.env.WAYFARER_E2E_BASE_URL ?? 'https://localhost:7150';

export default defineConfig({
  testDir: './tests/e2e/shared-layout',
  outputDir: '.local/playwright/shared-layout-output',
  fullyParallel: false,
  workers: 1,
  reporter: [['list'], ['html', { open: 'never', outputFolder: '.local/playwright/shared-layout-report' }]],
  use: {
    baseURL,
    ignoreHTTPSErrors: true,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure'
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }]
});
