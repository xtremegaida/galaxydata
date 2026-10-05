import { defineConfig, devices } from '@playwright/test';
import { baseURL } from './settings';

export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.ts',
  globalSetup: './global-setup.ts',
  // One story, in order, on one application.
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env['CI'],
  timeout: 60_000,
  expect: { timeout: 10_000 },
  // Neither an HTML report nor traces: they would hold the passwords typed (Playwright names a step by what it
  // fills), made up for the run but written down. `--trace retain-on-failure` asks for traces all the same.
  reporter: 'list',
  outputDir: 'test-results',
  use: {
    baseURL,
    trace: 'off',
    screenshot: 'only-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1920, height: 1080 },
      },
    },
  ],
});
