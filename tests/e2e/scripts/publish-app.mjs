// Publishes the application, its client built in, into .app, which the tests run (GD_E2E_APP names another). The
// publish runs `npm ci` in src/client, which replaces its node_modules: stop its development server first.
import { spawnSync } from 'node:child_process';
import { rmSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const project = resolve(here, '../../../src/GalaxyData.Web');
const output = resolve(here, '../.app');
// Anew: a publish leaves files of the one before (chunks by other names) in a folder it publishes into.
rmSync(output, { recursive: true, force: true });

const result = spawnSync(
  'dotnet',
  [
    'publish',
    project,
    '-c',
    'Release',
    '--use-current-runtime',
    '--self-contained',
    'false',
    '-o',
    output,
  ],
  {
    stdio: 'inherit',
    // Microsoft Defender's copy accelerator overflows MSBuild's copying threads' stacks (docs/server.md).
    env: { ...process.env, MSBUILDCOPYTASKPARALLELISM: '1' },
    shell: false,
  },
);
if (result.error) {
  throw result.error;
}
if (result.status !== 0) {
  process.exit(result.status ?? 1);
}
console.log(`Published to ${output}`);
