import { defineConfig, devices } from '@playwright/test';

/**
 * The base URL of the application under test.
 *
 * * UNSET (the default): the Vite dev server on 127.0.0.1:5173, started by the
 *   `webServer` block below. This is the mode the `opsbridge-web-e2e` CI job
 *   uses, and it is deliberately the DEFAULT so that job keeps working
 *   unchanged.
 * * SET (e.g. http://127.0.0.1:8080): an ALREADY RUNNING deployment. The
 *   `webServer` block is then skipped entirely: the containerised stack (nginx
 *   in front of the API, see Dockerfile.web) has to be exercised as it is
 *   deployed. Starting a dev server in this mode would silently prove the
 *   WRONG artefact - a dev server has no nginx in front of it, and the dev
 *   server's own /api proxy would answer instead of the image.
 */
const externalBaseUrl = process.env.PLAYWRIGHT_BASE_URL?.trim();

export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  use: {
    baseURL: externalBaseUrl || 'http://127.0.0.1:5173',
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  // The web server is started ONLY when the suite runs against the dev
  // server. With an external base URL there is nothing to start.
  webServer: externalBaseUrl
    ? undefined
    : {
        command: 'npm run dev -- --host 127.0.0.1',
        port: 5173,
        reuseExistingServer: !process.env.CI,
      },
});
