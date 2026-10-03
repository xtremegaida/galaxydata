# GalaxyData server

`GalaxyData.Web` is the application: an API under `/api`, and the client (the Angular app, built into `wwwroot`)
for every other path. This guide covers running and configuring it, and how its API behaves. The query language
is in [language.md](language.md).

## Running

```bash
dotnet run --project src/GalaxyData.Web
```

In development it listens on `http://localhost:5180` (`Properties/launchSettings.json`). Elsewhere, set the
address with `ASPNETCORE_URLS` or Kestrel's settings as for any ASP.NET Core application.

## Configuration

Settings are in the `GalaxyData` section of `appsettings.json`, or environment variables with `__` between the
parts (`GalaxyData__DataDirectory`).

| Setting | Default | Meaning |
|---|---|---|
| `DataDirectory` | `data` | Where the application keeps what it stores. Relative to the content root (the application's directory) unless absolute. Made when it's missing. |
| `Merge:MemoryLimit` | DuckDB's (80% of RAM) | How much memory the merge engine, which runs the parts of queries that combine sources, may use: `2GB`, `512MB`. |
| `Merge:Threads` | one per core | The threads the merge engine may use. |
| `Merge:TempDirectory` | a directory of its own under the system's temp directory | Where the merge engine puts data that doesn't fit in memory. Relative to the data directory unless absolute. |
| `Merge:ExtensionDirectory` | DuckDB's (`~/.duckdb/extensions`) | Where DuckDB looks for extensions. The application needs none. |
| `Merge:DownloadExtensions` | `false` | Whether DuckDB may download an extension a statement needs. |

## The data directory

The data directory holds the application's state. Back it up.

**One instance per directory.** One instance of the application at a time may use a data directory. Running two
on one directory would split state that is kept in memory, such as the catalog and previewed changes. While
running, the application holds `galaxydata.lock` in the directory, and a second instance that tries to use the
directory doesn't start: it says the directory is in use. The application is meant to run as a single instance;
there is no shared state for a farm of them.

**Startup.** If the directory can't be made, or can't be held, the application stops at startup and says why.

## Health

`GET /api/health` reports whether the application can do its work:

- 200 when every check passes, 503 when one fails.
- Each check is listed by name and status only. Anyone may ask, so failures are explained in the log, not in the
  answer.

```json
{"status":"healthy","checks":[{"name":"dataDirectory","status":"healthy"},{"name":"mergeEngine","status":"healthy"}]}
```

| Check | Passes when |
|---|---|
| `dataDirectory` | a file can be made in the data directory, and removed |
| `mergeEngine` | the merge engine runs a statement |

## The API

- **Location:** every endpoint is under `/api`.
- **JSON:** names are camelCase, and so are enum values (`"healthy"`). Numbers are JSON numbers; strings that
  look like numbers aren't read as them.
- **OpenAPI:** the API is described at `/api/openapi/v1.json` (OpenAPI 3.1).
  - The test `HostTests.TheOpenApiDocumentDescribesTheApi` keeps a reviewed copy in
    `tests/GalaxyData.Web.Tests/Hosting/Snapshots/`, so a change to the API shows in review.
  - The client's types are made from it.

### Problems

Errors are problem details (RFC 9457, `application/problem+json`). Every problem has:

- `status`;
- `title`;
- `detail`, when there is something to add;
- `traceId`, which finds the request in the log;
- `code`, which is what clients tell problems apart by.

| Status | Code | When |
|---|---|---|
| 400 | `query-syntax` | A query's text doesn't parse. `diagnostics` says where. |
| 422 | `query-invalid` | A query parses, but doesn't bind or plan. `diagnostics` says where. |
| 422 | `query-failed` | A query failed as it ran: a database error, or a value that didn't convert. |
| 422 | `script-invalid` | An edited script can't run. `problems` says where, and `source` names its source. |
| 502 | `source-unavailable` | A source couldn't be connected to. `source` names it. |
| 504 | `query-timeout` | A query ran longer than it may, and was stopped. |
| 500 | `internal-error` | Something unexpected. The answer says nothing of it outside development; the log has it. |
| others | by status | Problems the application raises have codes of their own. The rest have their status's: `bad-request`, `unauthenticated`, `forbidden`, `not-found`, `method-not-allowed`, `conflict`, `unsupported-media-type`, `unprocessable`, `too-many-requests`. |

**`diagnostics`** lists every diagnostic of the query, warnings included. Each has a `code` (`GDQ1001`, see the
language reference), a `severity` (`error`, `warning` or `info`), a `message`, and the range `[start, end)` of
the query's text it is about.

**`problems`** gives each problem of a script: its `message`, its `line`, and the range `[start, start + length)`
of the script's text.

**Unreachable sources.** A source that can't be reached is named, but the database's own words aren't passed on,
since they may name its server or login. They are logged instead, at warning level.

**Requests the client abandons.** A request whose client went away before it was answered is logged at debug
level, with status 499.

## The client

The client is served from `wwwroot`, which the client's build fills (it isn't in the repository). Every path that
isn't the API's is the client's page, `index.html`, served with `Cache-Control: no-cache`, so a new version is
picked up at once. That includes paths with dots and matrix parameters, such as
`/browse/shop.customers;f=country:eq:ZA/orders`.

**Asset files are the exception.** A path whose last segment has no matrix parameters and ends in an extension of
the kinds the client is built into is a file, and a 404 when it's missing. Those extensions are: `.js`, `.mjs`,
`.css`, `.map`, `.json`, `.txt`, `.webmanifest`, `.wasm`, images, and fonts.

**Paths under `/api`** that no endpoint has are the API's 404s, and a method an endpoint doesn't take is a 405.
They are never the client's page.
