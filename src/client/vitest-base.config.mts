import { defineConfig } from 'vitest/config';

// What the Angular builder's Vitest takes beyond its own options (`runnerConfig` in angular.json).
export default defineConfig({
  test: {
    // Pages tested through the router, and AG Grid in jsdom, take a few seconds a test; with every file run at
    // once they take longer than Vitest's 5. The limit is there for tests that hang.
    testTimeout: 15_000,
  },
});
