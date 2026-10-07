import { defineConfig, devices } from '@playwright/test';

// Managed environment is complete before discovery; there is no second host/teardown owner.
if (process.env.WAYFARER_E2E_MANAGED !== '1') {
  throw new Error('Use npm run test:e2e:shared-layout to provision run-owned guarded credentials.');
}
const baseURL = process.env.WAYFARER_E2E_BASE_URL!;

export default defineConfig({
  testDir: './tests/e2e/shared-layout',
  outputDir: process.env.WAYFARER_E2E_OUTPUT!,
  fullyParallel: false,
  workers: 1,
  reporter: [['list'], ['html', { open: 'never', outputFolder: process.env.WAYFARER_E2E_REPORT! }]],
  use: {
    baseURL,
    ignoreHTTPSErrors: true,
    // The isolated HTTPS host intentionally loads the local HTTP Vite server for Editor coverage.
    launchOptions: { args: ['--disable-web-security'] },
    // The connection test reveals credentials; automatic failure captures would contain them.
    trace: 'off',
    screenshot: 'off',
    video: 'off'
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }]
});
