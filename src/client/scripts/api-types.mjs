// Makes the API's types (src/app/core/api/schema.d.ts) from the OpenAPI document the server's tests keep, which
// HostTests.TheOpenApiDocumentDescribesTheApi checks against the API. With --check, it changes nothing, and fails
// when the types are out of date (npm test runs it first).
import { readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import openapiTS, { astToString } from 'openapi-typescript';

const documentUrl = new URL(
  '../../../tests/GalaxyData.Web.Tests/Hosting/Snapshots/HostTests.TheOpenApiDocumentDescribesTheApi.json',
  import.meta.url,
);
const typesUrl = new URL('../src/app/core/api/schema.d.ts', import.meta.url);

const header = `// The API's types, made by \`npm run api\` from the OpenAPI document in
// tests/GalaxyData.Web.Tests/Hosting/Snapshots. Don't edit them: change the API, accept its document, and run it again.

`;

const ast = await openapiTS(documentUrl, {
  // Objects of any members (values by column) are records of unknown values, not empty ones.
  emptyObjectsUnknown: true,
  // Each schema as a type of its own: UserDto for components['schemas']['UserDto'].
  rootTypes: true,
  rootTypesNoSchemaPrefix: true,
  silent: true,
});
const types = header + astToString(ast);

if (process.argv.includes('--check')) {
  const current = await readFile(typesUrl, 'utf8').catch(() => '');
  if (current.replaceAll('\r\n', '\n') !== types) {
    console.error(
      `${fileURLToPath(typesUrl)} is out of date with the API's document: run \`npm run api\`.`,
    );
    process.exit(1);
  }
} else {
  await writeFile(typesUrl, types);
  console.log(`Wrote ${fileURLToPath(typesUrl)}.`);
}
