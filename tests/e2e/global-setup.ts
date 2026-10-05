// Starts the published application on a data folder of its own, with the shop's database to connect to, and
// stops it (removing the folder) once the tests are done.
import { spawn, type ChildProcess } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import {
  createWriteStream,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
} from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { fileURLToPath } from 'node:url';
import { adminUserName, baseURL, port } from './settings';

const here = dirname(fileURLToPath(import.meta.url));

export default async function globalSetup(): Promise<() => Promise<void>> {
  const app = resolve(process.env['GD_E2E_APP'] ?? join(here, '.app'));
  const dll = join(app, 'GalaxyData.Web.dll');
  if (!existsSync(dll)) {
    throw new Error(
      `There is no published application in ${app}: run "npm run publish-app" (or set GD_E2E_APP).`,
    );
  }
  // Another application on the port (one a run cut short left, say) would answer for this one.
  await portFree();

  const data = mkdtempSync(join(tmpdir(), 'gd-e2e-'));
  const removeData = () =>
    rmSync(data, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 });
  const files = join(data, 'files');
  try {
    mkdirSync(files);
    makeShop(join(files, 'shop.db'));
  } catch (error) {
    removeData();
    throw error;
  }

  const password = randomBytes(18).toString('base64url');
  const logs = join(here, 'logs');
  mkdirSync(logs, { recursive: true });
  const log = createWriteStream(join(logs, 'server.log'));
  const server = spawn('dotnet', [dll], {
    cwd: app,
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Production',
      ASPNETCORE_URLS: baseURL,
      GalaxyData__DataDirectory: data,
      GalaxyData__Merge__TempDirectory: join(data, 'merge'),
      GalaxyData__Bootstrap__AdminUserName: adminUserName,
      GalaxyData__Bootstrap__AdminPassword: password,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  // The log ends once both streams have closed, not as the first of them ends.
  server.stdout?.pipe(log, { end: false });
  server.stderr?.pipe(log, { end: false });
  const closed = new Promise<void>((resolve) => server.once('close', () => resolve()));
  // Without `dotnet` there is no process: its 'error' is said, rather than thrown.
  let failed = null as Error | null;
  server.once('error', (error) => (failed = error));

  const stop = async () => {
    await stopped(server, closed);
    log.end();
    removeData();
  };
  try {
    await healthy(server, () => failed);
  } catch (error) {
    await stop();
    throw error;
  }

  process.env['GD_E2E_PASSWORD'] = password;
  process.env['GD_E2E_FILES'] = files;
  return stop;
}

/** The shop fixture the engine's tests use, and rows enough for pages. */
function makeShop(path: string): void {
  const shop = new DatabaseSync(path);
  try {
    shop.exec(readFileSync(resolve(here, '../fixtures/shop.sqlite.sql'), 'utf8'));
    shop.exec(readFileSync(join(here, 'fixtures/more-orders.sql'), 'utf8'));
  } finally {
    shop.close();
  }
}

/** Whether the port is free at both addresses `localhost` is, as the application listens on both. */
async function portFree(): Promise<void> {
  for (const host of ['127.0.0.1', '::1']) {
    await new Promise<void>((resolve, reject) => {
      const probe = createServer();
      probe.once('error', (error: NodeJS.ErrnoException) =>
        // A machine without IPv6 has no ::1 to listen on, nor anything listening there.
        error.code === 'EADDRNOTAVAIL'
          ? resolve()
          : reject(
              new Error(
                `Port ${port} is in use at ${host}: stop what listens there (an application a run cut short ` +
                  'left?), or set GD_E2E_PORT.',
              ),
            ),
      );
      probe.listen({ port, host }, () => probe.close(() => resolve()));
    });
  }
}

async function healthy(server: ChildProcess, failed: () => Error | null): Promise<void> {
  const until = Date.now() + 60_000;
  while (Date.now() < until) {
    const error = failed();
    if (error) {
      throw new Error(`The application couldn't be started: ${error.message}`);
    }
    if (server.exitCode !== null || server.signalCode !== null) {
      throw new Error(
        `The application stopped as it started (exit code ${server.exitCode}): see logs/server.log.`,
      );
    }
    try {
      const answer = await fetch(`${baseURL}/api/health`, { signal: AbortSignal.timeout(5_000) });
      // Ours, the port having been free, if it still runs.
      if (answer.ok && server.exitCode === null) {
        return;
      }
    } catch {
      // Not listening yet.
    }
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  throw new Error(
    `The application wasn't healthy within a minute at ${baseURL}: see logs/server.log.`,
  );
}

/** Stops the application (killed outright if it hasn't gone in 10 seconds), and waits for its streams to close. */
async function stopped(server: ChildProcess, closed: Promise<void>): Promise<void> {
  if (server.pid === undefined) {
    // It never started.
    return;
  }
  if (server.exitCode === null && server.signalCode === null) {
    server.kill();
  }
  const late = setTimeout(() => server.kill('SIGKILL'), 10_000);
  await closed;
  clearTimeout(late);
}
