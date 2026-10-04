# GalaxyData client

The client is the Angular application the server serves to browsers, in `src/client`. This guide covers
developing it. Running and publishing the whole application is in [server.md](server.md).

## Developing

It needs Node.js 24.15 or later (or 22.22.3 or later), as Angular's tools do; `npm ci` refuses others
(`engines` in `package.json`, with `engine-strict` in `.npmrc`). Install its packages once, in `src/client`:

```bash
npm ci
```

Run the server, then the client's development server beside it:

```bash
dotnet run --project src/GalaxyData.Web
```

```bash
npm start
```

The client is then at `http://localhost:4200`. It is rebuilt as its files change, and it passes requests for
`/api` on to the server at `http://localhost:5180` (`proxy.conf.json`). The server's security headers don't
apply to the development server; they apply to the published client.

**Commands**, run in `src/client`:

| Command | What it does |
|---|---|
| `npm start` | The development server. |
| `npm run build` | Checks that the API's types are current, then makes a production build into `dist/browser`, checked against the content security policy (see [Building](#building)). |
| `npm test` | Checks that the API's types are current, runs the build scripts' tests (`scripts/*.test.mjs`, Node's test runner), then the unit tests, once. |
| `npm run test:watch` | Runs the unit tests as files change. |
| `npm run lint` | ESLint, with Angular's rules for code and templates (accessibility included). |
| `npm run format` | Formats every file with Prettier. |
| `npm run api` | Makes the API's types again (see [The API's types](#the-apis-types)). |

**Install scripts.** npm 11 warns about packages whose install scripts no `allowScripts` setting covers
(`@parcel/watcher`, `esbuild`, `lmdb`, `msgpackr-extract`). It runs them all the same: the warning is a notice, and
nothing needs approving.

## The API's types

`src/app/core/api/schema.d.ts` holds the API's types. They are made by
[openapi-typescript](https://openapi-ts.dev) from the OpenAPI document the server's tests keep
(`tests/GalaxyData.Web.Tests/Hosting/Snapshots`). The test `HostTests.TheOpenApiDocumentDescribesTheApi`
checks that document against the API.

**When the API changes:**

1. Run the server's tests. The OpenAPI test fails, leaving the new document beside the old one as
   `.received.json`.
2. Review the new document, and accept it: it replaces the old one.
3. Run `npm run api`.

`npm test` and `npm run build` fail while the types are older than the document, so the client is built against
the API as it is.

**Calling the API.** `ApiClient` calls the API's operations, named by their method and path as the document
writes them. Each call gives what its operation takes: the path's values, the query's, and the body. Each call's
answer has the operation's type. A call the document doesn't describe doesn't compile.

```ts
const api = inject(ApiClient);
api.get('/api/users').subscribe((users) => ...);                                  // UserDto[]
api.post('/api/users', { body: { userName, role, password } }).subscribe(...);    // CreateUserRequest, giving a UserDto
api.delete('/api/users/{id}', { path: { id }, query: { version } }).subscribe(); // void
```

As with Angular's `HttpClient`, a request is sent when its answer is subscribed to. A path value that is empty,
`.` or `..` is refused, as it would be read as a step along the path.

The API's schemas are types of their own (`UserDto`), and `Schema<'UserDto'>` names them too.

## The theme

**Material 3.** The theme is Angular Material's (`src/styles.scss`): azure and blue palettes, and Roboto. Material
sets its system colors as `light-dark()` values, so the page's `color-scheme` makes it light or dark.

**Light or dark.** `ColorScheme` holds the scheme chosen: the system's (the default), light or dark. It sets the
page's `color-scheme`, and keeps the choice in the browser. Its `dark` signal says whether the application is dark,
with the system's setting resolved. It is for what Material doesn't theme: the grid and the editor.

**Fonts.** Fonts are served with the client, as the content security policy loads none from elsewhere: Roboto,
Roboto Mono for code (`--gd-code-font-family`), and Material Symbols (outlined) for icons. `<mat-icon>` shows the
symbols by their names, as its text or its `fontIcon`: `<mat-icon>dark_mode</mat-icon>`,
`<mat-icon fontIcon="dark_mode" />`.

## Building

`npm run build` builds into `dist/browser`. Publishing the server runs it, and publishes what it leaves there as
the server's `wwwroot` (see [server.md](server.md#running)). Files are named by a hash of their contents, so the
server lets browsers keep them.

**The content security policy.** The server's policy runs the client's own script files and nothing inline, and
loads nothing from other origins. After each build, `scripts/check-csp.mjs` fails the build when it finds what the
policy refuses:

- in `index.html`: inline scripts (data blocks, such as `application/json`, aside), event handler attributes,
  `javascript:` URLs, and scripts, stylesheets and other resources from other origins;
- in the stylesheets: URLs and imports from other origins.

This is why the build doesn't inline critical CSS (`inlineCritical: false` in `angular.json`). Angular would load
the rest of the stylesheet with an inline script, which the policy refuses.

## Conventions

- **Angular 22:** standalone components, zoneless change detection, OnPush by default, and signals.
- **Names:** the selector prefix is `gd`. File names have no type suffix (`color-scheme.ts`), as Angular's
  current style guide has it.
- **Format:** Prettier, with 2-space indentation and lines of up to 100 characters.
- **Tests:** beside their code (`*.spec.ts`), run by Vitest in jsdom. The test build type-checks the tests, so
  `expectTypeOf` and `@ts-expect-error` check types.
- **Layout:** `src/app/core` holds what the whole application uses: the API and the theme.
