// Checks the built page and stylesheets against the server's content security policy (see csp.mjs), and fails
// when they have what it refuses: Angular's inlined critical CSS, for one, loads its stylesheet with an onload
// handler (so the build turns it off). npm run build runs it after building.
import { readdir, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { pageProblems, styleProblems } from './csp.mjs';

const outputUrl = new URL('../dist/browser/', import.meta.url);

const problems = [];
const page = new URL('index.html', outputUrl);
for (const problem of pageProblems(await readFile(page, 'utf8'))) {
  problems.push(`index.html: ${problem}`);
}
for (const entry of await readdir(outputUrl, { recursive: true })) {
  if (entry.endsWith('.css')) {
    for (const problem of styleProblems(await readFile(new URL(entry, outputUrl), 'utf8'))) {
      problems.push(`${entry}: ${problem}`);
    }
  }
}

if (problems.length > 0) {
  console.error(
    `The build in ${fileURLToPath(outputUrl)} has what the content security policy refuses:\n` +
      problems.map((problem) => `- ${problem}`).join('\n'),
  );
  process.exit(1);
}
