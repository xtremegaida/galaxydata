# GalaxyData

GalaxyData is two things built together:

- **GalaxyData.Query**, a .NET library and its command line, `gdq`: a query language of LINQ-like method chains
  over PostgreSQL, SQL Server, SQLite, DuckDB and folders of Excel workbooks, one query reading several of them at
  once. Results say where each column comes from (lineage) and how rows lead to other rows (links), and changes to
  rows are written back as each database's SQL.
- **GalaxyData Manager**, a web application on the library (ASP.NET Core and Angular): browsing the data and
  following its links, changing rows (previewed as SQL, committed together), writing and saving queries, and
  looking after users, connections and the catalog's overlay (relations, virtual entities, settings).

## What it needs

- The .NET SDK 10.0.103 or a later feature band (`global.json`).
- Node.js 22.22.3 or a later 22, 24.15 or a later 24, or 26 or later, for the client and the smoke test.
- For the tests against servers: Docker, to run PostgreSQL and SQL Server (on Windows, Docker in WSL).

## Running it

Run the server, and the client's development server beside it (in `src/client`, after `npm ci`). The first time,
give the first administrator a password, to be changed at the first sign-in; later runs don't need it:

```bash
GalaxyData__Bootstrap__AdminPassword='<12 characters or more>' dotnet run --project src/GalaxyData.Web
```

```bash
npm start
```

Sign in as `admin` at `http://localhost:4200`, and add a connection under Administration. To publish the
application with its client built in:

```bash
dotnet publish src/GalaxyData.Web -c Release -r win-x64 -o out
```

Running, configuring and publishing it are in [docs/server.md](docs/server.md); on Windows, set
`MSBUILDCOPYTASKPARALLELISM=1` first (see there).

**`gdq`** runs queries from the command line:

```bash
dotnet run --project src/GalaxyData.Query.Cli -- run -s shop=sqlite:tests/fixtures/shop.sqlite.sql "shop.orders.where(total > 50)"
```

## Testing it

| What | How |
|---|---|
| The library, the server | `dotnet test --solution GalaxyData.slnx` |
| Against PostgreSQL and SQL Server | `sh tests/GalaxyData.Query.ContainerTests/servers.sh up` first (skipped without them; `GDQ_TEST_POSTGRES` and `GDQ_TEST_SQLSERVER` name other servers) |
| The client | `npm test` in `src/client` |
| The whole application in a browser | `npm ci`, then `npm run e2e` in `tests/e2e`: publishes it (stop the client's development server first), then runs the smoke test in Playwright's Chromium (`npx playwright install chromium` where it isn't installed). See [docs/client.md](docs/client.md#the-smoke-test). |

## Documentation

- [docs/language.md](docs/language.md): the query language, `gdq`, and the library's API.
- [docs/server.md](docs/server.md): running and configuring the application, and its API.
- [docs/client.md](docs/client.md): developing the client, and what each page does.
- [plans/federated-query-engine-and-kind-flame.md](plans/federated-query-engine-and-kind-flame.md): the design, and
  what each milestone built.

## Layout

```text
src/GalaxyData.Common        the expression parser
src/GalaxyData.Query         the engine: catalog, binder, planner, federation, SQL for each database, changes
src/GalaxyData.Query.*       each database's provider (Sqlite, DuckDb, PostgreSql, SqlServer, Excel), and the CLI
src/GalaxyData.Web           the server
src/client                   the client (Angular)
tests/                       each project's tests, the fixtures, and the end-to-end smoke test (e2e)
```
