// What the global setup and the tests share. Values made up for a run travel in the environment: the global setup
// sets them before the test workers start, and they inherit them.

/** The port the application listens on (GD_E2E_PORT). */
export const port = Number(process.env['GD_E2E_PORT'] ?? 5199);

export const baseURL = `http://localhost:${port}`;

/** The first administrator, whom the application makes as it first starts. */
export const adminUserName = 'admin';

/** The first administrator's password: made up for each run, given to the application, never written down. */
export function firstPassword(): string {
  return fromSetup('GD_E2E_PASSWORD');
}

/** The folder connections' files are allowed in, holding the shop's database (shop.db). */
export function filesFolder(): string {
  return fromSetup('GD_E2E_FILES');
}

function fromSetup(name: string): string {
  const value = process.env[name];
  if (!value) {
    throw new Error(
      `${name} isn't set: the tests run under the global setup (npx playwright test).`,
    );
  }
  return value;
}
