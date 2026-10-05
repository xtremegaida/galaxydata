# GalaxyData server

`GalaxyData.Web` is the application: an API under `/api`, and the client (the Angular app, built into `wwwroot`)
for every other path. This guide covers running and configuring it, and how its API behaves. The query language
is in [language.md](language.md), and developing the client in [client.md](client.md).

## Running

```bash
dotnet run --project src/GalaxyData.Web
```

In development it listens on `http://localhost:5180` (`Properties/launchSettings.json`). Elsewhere, set the
address with `ASPNETCORE_URLS` or Kestrel's settings as for any ASP.NET Core application.

**The client, in development**, runs on a development server of its own (`npm start` in `src/client`, at
`http://localhost:4200`), which passes `/api` on to this one. See [client.md](client.md).

**Publishing.**

```bash
dotnet publish src/GalaxyData.Web -c Release -o out
```

- **The client is built as it is published.** `npm ci` and `npm run build` run in `src/client`, and what the build
  leaves in `src/client/dist/browser` is published as `wwwroot`. This needs Node.js 24.15 or later (or 22.22.3 or
  later).
- **Stop the client's development server first** (`npm start`, and `npm run test:watch`). `npm ci` removes
  `node_modules` before installing, and on Windows those hold files open there: the publish fails (`EBUSY`) with
  packages missing. Publishing again with them stopped puts them back.
- **Skipping it.** `-p:SkipClientBuild=true` publishes the server alone; so does a checkout without the client.
  `-p:ClientRoot=<folder>` (relative to `src/GalaxyData.Web`) builds a client from elsewhere, and fails when there
  is none there. A `wwwroot` in `src/GalaxyData.Web` would be published mixed with the client's build, the
  client's files replacing its files of the same names without a word. Publishing the client therefore fails
  while there is one.
- **Microsoft Defender on Windows** may crash the build as it copies files. MSBuild prints `Stack overflow.`, then
  hangs. Defender's copy accelerator, which it loads into processes that copy files, overflows the small stacks of
  MSBuild's copying threads. Copying on one thread avoids it: set `MSBUILDCOPYTASKPARALLELISM=1` in the
  environment of the build or publish.
- **One platform.** Without a runtime, the databases' native libraries come for every platform (about 370 MB).
  `-r linux-x64` (or `win-x64`, ...) publishes that platform's alone (about 60 MB).
- **Running it.** The published folder runs with `dotnet GalaxyData.Web.dll`, or the executable. Its data directory
  (`data`, beside it, unless configured) must be writable, and backed up with its `keys`.
- **In front of it.** Serve it over HTTPS: set Kestrel's certificate, or put a reverse proxy in front (see
  [Security](#security)). Set `AllowedHosts` to the host names it is reached by.

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
| `Bootstrap:AdminUserName` | `admin` | The first administrator's user name. |
| `Bootstrap:AdminPassword` | none | The first administrator's password: required while there are no users. See [The first administrator](#the-first-administrator). |
| `Bootstrap:RequirePasswordChange` | `true` | Whether the first administrator must change the password at the first sign-in. |
| `Bootstrap:ResetAdminPassword` | `false` | Sets the administrator's password to `AdminPassword` again, to get back in. |
| `Auth:MinimumPasswordLength` | `12` | The fewest characters a password may have (8 to 128). |
| `Auth:MaxFailedSignIns` | `5` | Failed sign-ins in a row that lock a user out. |
| `Auth:LockoutDuration` | `00:15:00` | How long a lockout lasts. |
| `Auth:SessionIdleTimeout` | `08:00:00` | How long a session lasts without requests; each request extends it. |
| `Auth:SignInsPerMinute` | `10` | Sign-in attempts (and password changes) a client address may make a minute. |
| `Connections:AllowedFileRoots` | `files` in the data directory | The folders connections' files and folders must be in. Relative to the data directory unless absolute. A list given replaces the default. |
| `Connections:TestTimeout` | `00:00:10` | How long trying a connection may take. |
| `Connections:RefreshTimeout` | `00:10:00` | How long reading a connection's schema may take. |
| `Connections:ParallelRefreshes` | `2` | How many connections' schemas are read at once (1 to 16). |
| `Query:Timeout` | `00:01:00` | How long a query may run, reading its rows included. |
| `Query:CountTimeout` | `00:00:03` | How long counting a grid's rows may take; the grid's rows come without the count after that. |
| `Query:MaxFetchedRows` | `10000000` | The most rows a query may fetch from its sources to combine them. |
| `Query:MaxPageSize` | `1000` | The most rows a page of a grid may have. |
| `Changes:MaxChanges` | `10000` | The most changes a user may have pending. |
| `Changes:PlanLifetime` | `00:30:00` | How long a preview of changes may be committed. |
| `Security:ContentSecurityPolicy` | see [Security](#security) | The client's content security policy; empty for none. |
| `Security:Hsts` | `true` | Whether answers over HTTPS tell browsers to keep to HTTPS (outside development). |
| `Security:HstsMaxAge` | `180.00:00:00` | How long browsers keep to HTTPS. |
| `Security:RequireHttps` | `false` | Whether requests over HTTP are redirected to HTTPS. |
| `Security:HttpsPort` | the server's | The port to redirect to HTTPS on. |
| `Proxy:Enabled` | `false` | Whether a reverse proxy passes requests on, saying who the client is. |
| `Proxy:KnownProxies` | none | The trusted proxies' addresses. With neither these nor networks, only a proxy on the same machine is trusted. |
| `Proxy:KnownNetworks` | none | The networks trusted proxies are in (`10.0.0.0/8`). |
| `Proxy:ForwardLimit` | `1` | How many proxies in a row are believed. |
| `RateLimits:RequestsPerMinute` | `600` | Requests to the API a user (or, signed out, an address) may make a minute; `0` for no limit. |
| `RateLimits:ConcurrentQueries` | `4` | Requests that run queries or reach sources a user may have running at once. |
| `RateLimits:QueuedQueries` | `16` | Such requests that may wait for one to end; more are refused. |

Settings that don't make sense (a minimum password length of 3, a user name with spaces) stop the application at
startup, naming the setting.

## The data directory

The data directory holds the application's state. Back it up.

**One instance per directory.** One instance of the application at a time may use a data directory. Running two
on one directory would split state that is kept in memory, such as the catalog and previewed changes. While
running, the application holds `galaxydata.lock` in the directory, and a second instance that tries to use the
directory doesn't start: it says the directory is in use. The application is meant to run as a single instance;
there is no shared state for a farm of them.

**Startup.** If the directory can't be made, or can't be held, the application stops at startup and says why.

| In the data directory | What it is |
|---|---|
| `galaxydata.db` | The application's own database (SQLite, in WAL mode): users, the audit, and what later features keep. Its `-wal` and `-shm` files belong with it. |
| `keys/` | The keys that protect session cookies and stored secrets (ASP.NET Data Protection). On Windows they are encrypted with DPAPI for the account the application runs as, so only that account can use them. Elsewhere they are stored as they are, so keep the directory readable by that account alone. **Losing them signs everyone out and loses stored secrets.** |
| `backups/` | Copies of the database made before a new version changes its schema, the newest ten. |
| `files/` | The folder connections' files may be in, unless `Connections:AllowedFileRoots` says otherwise. Put SQLite and DuckDB databases and folders of workbooks here. |
| `galaxydata.lock` | Held while the application runs (see above). |

**Upgrades.** At startup the application applies the schema changes (migrations) a new version brings, after
copying the database to `backups/galaxydata-{when}-before-{change}.db`. A database that a newer version has
changed is refused: the application says so and doesn't start. To go back a version, restore the backup made
before the upgrade.

## Users and signing in

**Roles.** A user has one of three roles:

| Role | May |
|---|---|
| `read` | read data and run queries |
| `dataManager` | also change data |
| `admin` | also manage users, connections and the overlay, and see the audit |

**Sessions.** Signing in starts a session: the `gd.auth` cookie, which is HttpOnly and SameSite=Strict. It is a
browser-session cookie that ends after `Auth:SessionIdleTimeout` without requests.

- **Changes that end sessions.** A user's sessions end within a request when their password is reset, their role
  changes, or they are disabled or deleted. Changing one's own password ends one's other sessions.
- **Signing out** removes the cookie. A copy of it made before stays valid until it expires or one of those
  changes happens: sessions aren't kept on the server.

**Passwords.** Passwords are hashed (ASP.NET Core Identity's PBKDF2). A password must:

- have at least `Auth:MinimumPasswordLength` characters, and at most 256;
- not contain the user name.

`GET /api/auth/password-policy` gives the lengths, for forms to say before a password is sent.

**New and reset passwords.** A new user, and one whose password an administrator resets, must change the
password at their next sign-in. Until they do, they may do nothing else: every other request is answered 403
`password-change-required`.

**Lockout.** `Auth:MaxFailedSignIns` failed sign-ins in a row lock a user out for `Auth:LockoutDuration`, even
with the right password. An administrator may unlock them sooner.

**What a failed sign-in says.**

- A wrong password and an unknown user get the same answer.
- A disabled user is told so only when the password is right.
- A locked-out user is told so, which shows that the user exists.

**Rate limit.** A client address may try `Auth:SignInsPerMinute` sign-ins (and password changes) a minute. Behind
a reverse proxy, every client has the proxy's address, unless the proxy is trusted to say whose it is (`Proxy`, see
[Security](#security)).

**Administrators.**

- **Who they may manage.** Administrators manage users, but no administrator may demote, disable, delete or reset
  themselves.
- **The last administrator.** No change may leave the application without an enabled administrator.
- **The audit.** Every change to a user is in the admin audit (`GET /api/audit/admin-events`), with who made it
  and what it changed. Passwords are never in it.

### The first administrator

When the database has no users, the application makes an administrator named `Bootstrap:AdminUserName`, with the
password `Bootstrap:AdminPassword`.

- **Without that password the application doesn't start**, and says what to set.
- **Remove the password from the configuration** once the administrator has signed in. With
  `Bootstrap:RequirePasswordChange` (the default), the administrator must change it at the first sign-in.

**Getting back in.** When no administrator can sign in:

1. Set `Bootstrap:ResetAdminPassword` to `true` and `Bootstrap:AdminPassword` to a new password.
2. Restart. The administrator `Bootstrap:AdminUserName` gets that password, to be changed at the next sign-in, and
   is enabled, an administrator, and unlocked. If the user had been deleted, it is made again.
3. Remove both settings.

A reset is done once for each password given, so a restart with the settings still there doesn't undo the change
the administrator made. To reset again, give another password.

### Anti-forgery

Every request that changes anything (POST, PUT, PATCH, DELETE) needs an anti-forgery token, signing in included;
one without it is answered 400 `xsrf-token-invalid`.

1. `GET /api/auth/session` puts the token in the `XSRF-TOKEN` cookie.
2. The client sends it back in the `X-XSRF-TOKEN` header, as Angular's HttpClient does by itself.

A token belongs to the user it was made for. Signing in, signing out and changing one's password give a new one;
after another change of user (a session that ended), ask for the session again.

### Who may call what

Every endpoint says which policy it needs:

- **Anyone:** health, the session, signing in and signing out.
- **Signed in:** changing one's own password, and what a password must be, even while it must be changed.
- **Signed in with no password to change:** everything else, by role. The OpenAPI document needs any role.

A test (`PolicyTests`) fails when an endpoint under `/api` doesn't say. Anything that doesn't say needs a signed-in
user with no password to change.

## Connections

A connection is a source the application queries. Administrators manage connections under `/api/connections`.

| Kind | Source | Settings |
|---|---|---|
| `postgres` | PostgreSQL 12 or later | Npgsql's keywords: `Host`, `Port`, `Database`, `Username`, `Password`, `SSL Mode`, ... |
| `sqlserver` | SQL Server 2016 or later | SqlClient's: `Data Source`, `Initial Catalog`, `Integrated Security`, `User ID`, `Password`, `Encrypt`, ... |
| `sqlite` | a SQLite database file | `Data Source`, `Default Timeout`, ... |
| `duckdb` | a DuckDB database file | `Data Source`, and DuckDB's settings (`threads`, `memory_limit`) |
| `excel` | a folder of `.xlsx` workbooks | `Folder` |

**Settings.** A connection's settings are its provider's connection-string keywords and values, named as the
provider names them.

- **Synonyms are renamed.** `Server` becomes `Host` for PostgreSQL; `uid` becomes `User ID` for SQL Server.
- **Unknown keywords are refused.** A keyword or value the provider doesn't take is refused with its message.
- **Two ways to edit.** Administrators edit the settings field by field (the form each kind describes at
  `GET /api/connection-kinds`) or as a connection string. `POST /api/connection-kinds/{kind}/convert` turns one
  into the other. Folders of workbooks have no connection string.
- **Bool values.** Values are text, as in a connection string. A bool is `true` or `false` in any case; SQL
  Server's are written `True`/`False`.

**The alias.** Each connection has an alias, which is how queries name it (`shop.customers`). An alias:

- is a plain name: a letter or `_`, then letters, digits and `_`, at most 64;
- isn't a word the language has (`and`, `or`, `not`, `in`, `true`, `false`, `null`);
- is unique, ignoring case;
- never changes.

**Read-only.** Connections are read-only unless made otherwise, and folders of workbooks always are.

- **Opening files.** The application opens SQLite and DuckDB files read-only or read-write itself (so `Mode` and
  `ACCESS_MODE` aren't settings), and never makes a missing file.
- **PostgreSQL** sessions start read-only (`default_transaction_read_only`).
- **SQL Server** has no such setting: give a read-only connection a login that can't write.
- **Server-side files.** SQL Server's `AttachDbFilename` isn't a setting either.

**Files and folders.** Database files, folders of workbooks and PostgreSQL's certificate and key files must be in
one of `Connections:AllowedFileRoots` (by default the data directory's `files` folder), given by their full
paths.

- **Links.** A link in those folders that leads outside them is refused.
- **Why.** This keeps an administrator's account from reading whatever else the machine has.

**Secrets.** Passwords, and settings named like secrets (`token`, `secret`), are kept apart from the other
settings, protected with the application's data protection keys, for that connection alone.

- **Never sent back.** A connection says which secrets have a value, never what it is, and its connection string
  shows them as `********`.
- **Editing secrets.** An edit keeps, sets or clears each secret. In a connection string, `********` keeps it, a
  value written out sets it, and leaving the keyword out clears it.
- **Never logged.** Secrets aren't in the audit, which names them only, nor in the log. A database's answer that
  repeats one has it masked.
- **Lost keys.** If the data protection keys are lost, connections say `secretsUnreadable` and their secrets must
  be entered again. Keeping one is refused: a "keep" in the form, or a `********` in a connection string that the
  request's secrets don't set (one set in the form, then converted into the string, is set, not kept).

**Its schema** is read when it is made, when its settings, secrets or options change, and when an administrator
asks: see [Schemas and the catalog](#schemas-and-the-catalog).

**Trying a connection.** `POST /api/connections/{id}/test` tries a saved connection, and `POST /api/connections/test`
tries settings before they are saved (with `connectionId`, keeping that connection's secrets).

- **What it does.** It checks that the file or folder is there, connects within `Connections:TestTimeout`, and runs
  a statement.
- **What it answers.** It says what it found (the server's version, the database, how many tables or workbooks),
  or the database's own words.
- **Any address.** It connects to whatever host an administrator gives; restrict the machine's outbound network
  where that matters.

## Schemas and the catalog

The catalog is what queries can name: each connection's tables and views, as its schema was last read.

**Reading schemas.** A connection's schema is read in the background.

- **When.** When the connection is made; when its settings, secrets or options change; when an administrator asks
  (`POST /api/connections/{id}/refresh`, answered 202); and at startup, for connections never read, or being read
  when the application stopped.
- **How it stands.** A connection's `schemaStatus` is `loading` (being read, or waiting to be), `ready`, or `failed`,
  with `schemaError` saying why (the database's words, its secrets masked). When a read fails, the schema read
  before is still used. `schemaRefreshedAt` is when it was last read.
- **Limits.** A read may take `Connections:RefreshTimeout`. `Connections:ParallelRefreshes` connections are read at
  once, and each connection one read at a time: one asked for while it is read is read again after.
- **No conflicts.** Reading a schema leaves the connection's version as it was, so an administrator editing it
  isn't in conflict with the read.
- **As queries connect.** A schema is read the way queries connect: read-only or not, as the connection is set. A
  read uses connections of its own, outside the pool queries use.

**Snapshots.** The schemas read are kept in the metadata database.

- **Structure only.** A read that finds the structure as it was (row counts aside, as they change all the time)
  brings the newest snapshot's row counts up to date.
- **What changed.** A read that finds the structure changed adds a snapshot, with what changed: tables and views
  added and removed, and their columns, keys, indexes and foreign keys.
- **The newest five** of each connection are kept, and deleted with it.
- **What it was read with.** A snapshot keeps the settings and options it was read with (never secrets). A folder of
  workbooks loads its sheets the way its snapshot was read until it is read again, so changing its options doesn't
  change what queries find before then.
- **Endpoints**, for administrators:
  - `GET /api/connections/{id}/snapshots` lists them, newest first, with how many things each added, removed and
    changed.
  - `GET /api/connections/{id}/snapshots/{snapshotId}` gives one's schema and what changed.

**The catalog.** It is built from each connection's newest snapshot and the overlay when first needed, and again
after anything it is built from changes. A request after a change waits for it, so none reads the old one.

- **Its version** is a hash of what it was built from: the same after a restart, different after any change.
- **The header.** API answers carry the version in the `X-Catalog-Version` header, so clients know when to read the
  catalog again. Answers that don't read the catalog carry it too, while it is up to date.
- `GET /api/catalog` gives the version, the sources (how their schemas stand, how many entities each has), and
  what building it found (a table whose shortcut a schema's name hides). What it found about an item of the
  overlay names it (`item`: its kind and id).

**The tree.** `GET /api/catalog/tree/children?parent={id}` gives a node's children; without `parent`, the sources.

- **As queries name things.** A source's own nodes are the tables and views of its default schema (`shop.orders`),
  and its other schemas (`shop.sales`), with their tables and views under them. A folder of workbooks has a schema
  for each workbook. A table whose name is also a schema's is under its own schema.
- **Ids are paths**, as queries write them: `shop`, `shop.sales`, `shop.orders`, `xl['Budget 2024']['Sheet 1']`.
- **What nodes say.** A source tells its kind, its status and whether it is read-only. An entity tells its row
  count (the database's estimate) and whether the user may change its rows.
- **Search.** `GET /api/catalog/tree/search?text=...&take=50` finds nodes by name: named so first, then starting so,
  then containing it.
  - Text with a `.` or `[` is looked for in paths too: the node at that path first, then those under it or starting
    so (`shop.ord`), then those whose paths have it.
  - Entities are also found by their columns (`credit` finds `customers` by `credit_limit`).
  - Each hit has the ids of its ancestors, to open them. At most 200 are given; `more` says there were more.

**Entities.** `GET /api/catalog/entity?name=shop.orders` describes one: its kind (table, view, virtual), its columns
with their logical types, its keys and navigations, and what the user may do with its rows. For administrators, it
also names the overlay's items that make and set it (`overlay`: the virtual entity it is, its settings) and each
navigation (`overlay`: the relation that makes it, the override that renames or hides it), by their ids; for
others, `overlay` is null. An entity knows its settings, and a navigation its override, even when some of what
they say can't be applied.

- **Changing rows** takes three things: a table with a primary key of its own, a connection that isn't read-only,
  and a role that edits data (data managers and administrators). A key with a column of a type the language has no
  values for can't find rows, so such a table only takes new ones.
  - Views, virtual entities and folders of workbooks are read-only.
  - A table without a primary key takes new rows, but its rows can't be changed or deleted.
  - Whatever can't be done says why.
- **Columns** say whether they can be changed, and whether a new row needs a value (`required`), may have one
  (`optional`), or takes none (`never`).
  - Keys are given in new rows only.
  - Computed and row version columns take no value.
  - Identity columns take none on SQL Server; the other databases take a value given.
  - Columns of types the language has no values for are read-only, and so, in this version, are binary columns.

## Browsing

`POST /api/browse/page` gives a page of rows to browse, for any role. It is a POST, for its body, so it needs the
anti-forgery token.

```json
{
  "source": { "entity": "shop.orders" },
  "grid": {
    "filters": [ { "column": "status", "conditions": [ { "op": "eq", "value": "open" } ] } ],
    "where": "customer.city == 'Cape Town'",
    "sort": [ { "column": "total", "desc": true } ],
    "offset": 0, "limit": 100
  },
  "includeSchema": true,
  "includeCount": true
}
```

**What to browse.** An entity's rows (`"entity"`), or those a navigation leads to from a row (`"from": {"entity":
"shop.customers", "key": ["42"]}, "navigation": "orders"`).

- **A query of the target's own rows.** The rows a navigation leads to are filtered rows of its target, so they can
  be changed as its rows can: `shop.orders.where(customer_id == $key1)`.
- **When the key isn't enough.** If the key doesn't give the values the navigation matches on (the `customer` of an
  order), the target's rows are those the row matches: `shop.customers.where(t => shop.orders.any(o => o.id ==
  $key1 and o.customer_id == t.id))`.

**The grid.**

- **Filters.** Each filter has conditions on one column, all of which (or, with `"any": true`, any of which) a row
  must meet. Each value is a parameter of the column's type.

  | `op` | Rows whose value |
  |---|---|
  | `eq`, `ne` | equals the value (text exactly); `ne` also takes nulls |
  | `lt`, `le`, `gt`, `ge` | is less, at most, more, at least |
  | `between` | is from `value` to `valueTo`, both included |
  | `contains`, `notContains` | has the text in it, ignoring case; `notContains` also takes nulls |
  | `startsWith`, `endsWith` | starts or ends with the text, ignoring case (`%` and `_` are themselves) |
  | `blank`, `notBlank` | is null (for text, or empty), or isn't |

- **Dates in date-time columns.** A date alone, compared with a date-time column, stands for its day: `eq` finds the
  day's rows, `gt` those after it. With offsets, days are in UTC. Nothing is after the last day, 9999-12-31.
- **Where.** `where` is one condition in the query language over the rows, navigations included
  (`customer.city == 'Cape Town'`). It must parse as one expression, not a statement. A problem with it is answered
  with its place in the expression and `"field": "grid.where"`.
- **Sort.** `sort` replaces the query's own. Pages are always in the same order: the engine adds the entity's key
  after the sort.
- **Paging.** `limit` is at most `Query:MaxPageSize`; without one, a page has 100 rows (or `Query:MaxPageSize`, when
  that is less).
- **Problems by field.** A column the rows don't have, an operation its type doesn't take (`contains` of a number),
  or a value that isn't one of its type, is a 400 naming the field (`grid.filters[0].conditions[0].value`).

**The answer.**

- **The query.** `queryText` and `parameters` are the query that gives the rows, to open as a query.
- **Rows.** Rows are positional, since names may be any text: `v` holds the values by column, `k` the key's values,
  `id` the key as JSON text (null when the rows have no key), and `r` the display values of the rows each refers to.
  A row's `id` is the same however its key is written: whole numbers are text, decimals have no trailing zeros
  (`["1.5"]` for `1.50`), and date-times with offsets are in UTC.
- **Values.** Values travel as JSON holds them exactly. Whole numbers past `int32`, decimals, dates, times,
  date-times (ISO 8601; offsets in UTC), guids, binary (base64) and doubles that aren't numbers (`NaN`) are text.
  Values sent (keys, filter values) are read the same way; numbers may also be JSON numbers.
  - Decimals have a point, and no thousands separators.
  - Booleans may also be the text `true` or `false` (in any case), as an address holds them.
  - Date-times with an offset end with it, with `Z`, or with neither (UTC).
- **`schema`** (with `includeSchema`):
  - the columns, with their types, whether each is part of the key, whether the user may change it and give it a
    value in a new row, and where its values come from;
  - the references (navigations along foreign keys): the columns that hold them (all of a composite key's, in its
    order, though one of them may show another reference of its own), the target's columns they match (in the same
    order), whether every column that holds it is shown (`complete`), and the target's column that shows its rows
    (none when it has none), so a client can set a reference from a row of the target it chose;
  - the collections that refer to each row;
  - what the user may do with the rows.
- **Counting.** `hasMore` says whether more rows follow. `total` (with `includeCount`) is counted alongside the
  page within `Query:CountTimeout`. When counting takes longer, it is null, unless the page is the last.

**Trails.** `POST /api/browse/trail` takes the crumbs of a path through the data and says where each leads:

- **The crumbs.** The first names an entity, and each after it a navigation from the row chosen in the one before.
- **What it says of each.** The entity it reaches, and the display value of the row chosen in it, if that row is
  among the rows the crumb leads to (`found`). A crumb that can't be followed says why (`problem`, no `entity`),
  and is the last. So is a crumb whose row isn't one (a key that can't be read, or an entity without a key): it has
  its entity, `found` is false, and `problem` says why. A trail has at most 50 crumbs.

**Where a row is.** `POST /api/browse/position` takes an entity, some of its columns and their values (a reference's
target columns, and what it holds), and finds the row with those values: its id (as pages give rows' ids), and how
many rows come before it as a grid shows them unsorted, in the key's order (`index`): those less in the key's first
column (NULLs, which a declared key's columns may hold, are less: they sort first), or equal in it and less in the
next, and so on. A grid of pages of `n` rows shows it on page `index / n`.

- **No id** when no row has the values, a value is NULL, or the entity has no key.
- **No index** when the key's values don't compare in order (a GUID's or binary), the row's own key holds a NULL,
  the rows have an order of their own (a virtual entity's sort, which pages keep, the key only breaking its ties),
  the rows are put together in the merge engine and the key holds text (the merge engine sorts text as DuckDB
  compares it, while the count's comparisons may run in a source that ignores case, or follows a locale), or
  counting takes longer than `Query:CountTimeout`.
- Columns are named in any case; at most 32. A column it hasn't, or a value that isn't of its column's type, is a
  400 by field (`columns[0]`, `values[0]`). Readers may ask, as they browse.

## Queries

Anyone who reads data may write queries in the language (see the language reference). Each is POSTed with its
`text` and its `parameters`, and runs within `Query:Timeout`.

**Parameters.** Each is `{name, type, value}`, the name with or without its `$`.

- **With a type** (`int64`, `date`, `decimal(12,2)`, as the language writes them), the value is read as rows' values
  are sent (whole numbers past int32 and decimals as text, dates as ISO text).
- **Without one**, a JSON value is typed as `gdq -p` types it: a boolean, a whole number (`int64`), another number
  (`decimal`), text, or null. Text adapts to what it meets, so `"2026-03-01"` compares with a date column; null takes
  the type it meets.

**Checking a query.** `POST /api/query/validate` says what is wrong with a query as it is written: every diagnostic,
placed in its text. It runs nothing, and answers 200 whatever is wrong with the query.

- **Its parameters:** those the text uses, whether each has a value, and the `type` the query takes for it (a
  parameter compared with a date column is a date), to ask for a value of: the type it takes without a value,
  whatever value was given (a whole number given for a decimal column is still a decimal). Where the query can't be
  checked without values (`-$n`), the type is the value's.
- **Parameters without values** are taken as null to check the rest. Where a null doesn't fit (`-$n`, `$a == $b`),
  the problem is `info`, not an error, saying the parameter wants a value: `success` says there is no error, and
  `complete` whether the query was checked to the end.
- **Its columns**, when it would run.

**Running a query.** `POST /api/query/execute` gives a page of its rows, as a grid shows them: `grid` filters, where,
sort and pages them as browsing does, and `includeSchema` and `includeCount` ask for the columns and the count.

- **The columns** are every column of the rows, in order, with `hidden` ones too (keys added to the query, for links
  and changes). Each has its type and lineage, and:
  - `link`: where its values lead. `row` is one row of an entity (a foreign key, a navigation); `collection` the rows
    of a collection (`orders.count()`); `drillDown` the rows an aggregate of a group was worked out from. `ordinals`
    are the values (by column) it needs.
  - `editTarget`: where a change of its value goes, the column of a table's row whose key is at `keyOrdinals`, and
    whether the user may change it.
- **What the rows are.** `rowIdentity` names the entity the rows are rows of, when the query only filters, sorts,
  pages or extends one entity's rows. It gives its key's ordinals, the rows that refer to each row (`related`), and
  what the user may do with them. Each row's `id` is then its key, as JSON.
- **The rows.** Each row's `v` has every value, hidden ones included. A query of one value (`shop.orders.count()`)
  or of a first row is a page of one row.
- **And with them:** `queryText` and `parameters`, the query as it ran (the grid's composed onto it, with the
  parameters of its filters); `stats`, its time, the rows fetched from sources, keys sent to them, and each
  fragment's part (a query of one source has none); `warnings`, placed in the query as written (a planning warning,
  such as a large fetch, is about all of it).

**Following a link.** `POST /api/query/link` takes a page's `queryText`, `parameters` and a row's `v`. It also takes a
`column`, whose link to follow, or a `related` (an index into `rowIdentity.related`). With `catalogVersion` (the
page's `X-Catalog-Version`), a catalog built since is a 409, as the columns may have moved.

- **The query of the rows it leads to** comes back with its parameters (none when it leads nowhere, as a null foreign
  key). A drill-down's query is the query's own before it grouped, with the parameters it uses.
- **For a grid.** When a grid can browse the rows, `browse` says what: an entity, with the `key` of the row to choose
  (a row link to a key), or a navigation from a row (a collection).

**Explaining a query.** `POST /api/query/explain` says how a query would run, without running it. With `grid`, it
explains the page the grid would fetch (one row more than the page, to know whether more follow). A query that
binds is explained even when it can't be planned; the summary and diagnostics say why.

- **What it gives:** the summary, the diagnostics, the columns, and the plan. The plan is a list of `nodes` (operator,
  detail, site, columns, estimated rows, `inputs` by id), whose root is `plan`.
- **The SQL:** each fragment's SQL with its parameters and how its rows are fetched, and the merge engine's SQL.
- **With `verbose`**, the plan after each phase of the optimizer.
- **As text.** All of it is in `text` too, as `gdq explain` writes it.

**Problems.** A query that doesn't parse is a 400 (`query-syntax`), and one that doesn't bind or plan is a 422
(`query-invalid`); either way `diagnostics` places each problem in its text. A problem in a grid's where has
`field: grid.where`, placed in the where. A query that fails as it runs is a 422 (`query-failed`), one that takes too
long a 504, and a source that can't be reached a 502.

**Saved queries.** `/api/saved-queries` keeps queries: a `name`, a `description`, the `text`, its `parameters`' values
and whether it `isShared`.

- **Whose they are.** Each is its owner's, and names are the owner's own, ignoring case (`409 query-name-taken`).
  Shared ones everyone sees; the others only their owner.
- **Who may change them.** The owner changes and deletes theirs. Administrators also change and delete shared ones
  (but not rename them, which would tell the names of the owner's others), and those of deleted users: a deleted
  user's queries stay, shared ones for everyone, the others for administrators to tidy away. They are no one's, so
  their names don't clash.
- **Endpoints:** `GET` (summaries, with `isMine` and `canEdit`), `GET {id}`, `POST`, `PUT {id}` (`{query, version}`),
  `DELETE {id}?version=`. Another user's query that isn't shared is a 404; one the user may only read is a 403 to
  change.
- **As written.** A query is saved as written, whether it runs or not; its parameters must read as values. Numbers
  are kept as a browser writes them back (`50.0` as `50`), so saving a query as it was read changes nothing.

## The overlay

The overlay adds to what the databases declare (see the language reference, 2.8). Administrators edit it, item by
item, under `/api/overlay`:

| Kind | Path | What it is |
|---|---|---|
| Relations | `relations` | A many-to-one relation the databases don't declare, in one source or across two: `from` and `fromColumns`, to the key or a unique key of `to` (`toColumns`). Its navigations are named `name` and `inverseName`, or by the convention. |
| Navigation overrides | `navigations` | A navigation renamed (`renameTo`) or hidden, found on `entity` by the name the convention gives it (`navigation`). |
| Virtual entities | `virtual-entities` | An entity defined by a `query`, named with a namespace (`reports.big_orders`), with a declared `key` when the query's isn't the one wanted. |
| Entity settings | `entity-settings` | For an `entity`: a declared `key` (for views and tables without one; it serves navigation, never changing rows), its `displayColumn`, whether it is `hidden`, and its `columns`' settings (`hidden`, a `label`, and a `type` it is read as, such as `date` for a SQLite text column). |

- **Entities are paths**, as queries write them (`shop.orders`, `xl['Budget 2024']['Sheet 1']`), so items outlive
  schema refreshes. Paths are compared exactly, as `pg.Orders` and `pg.orders` may be two tables.
- **Endpoints**, for each kind: `GET {path}/{id}`, `POST {path}`, `PUT {path}/{id}` (`{<item>, version}`, the
  version read), `DELETE {path}/{id}?version=`, and `POST {path}/validate?id=`. `GET /api/overlay` gives every item;
  `GET /api/overlay/export` gives the overlay as a JSON file, as `gdq --overlay` reads it.
- **Kept, with its issues.** An item is kept whatever the catalog finds wrong with it, and comes with its `issues`:
  each a `code` (the language reference's GDQ5xxx and GDQ2025), a `severity` and a `message`. An error leaves the
  item, or the part of it at fault, out of the catalog: a relation whose column isn't there, a column's settings, a
  rename to a name taken (the override still hides, if it says to). A warning doesn't (a navigation name taken, so
  another was given). `GET /api/overlay` counts the items with errors, and those with warnings only.
- **Checked again with every catalog.** An item that worked stops when its column goes, or its source is deleted,
  and says so the next time the catalog is built: after the schema is read, it is told of in the log at warning
  level. An entity of a source whose schema isn't read says that is why it isn't there.
- **Trying an item.** `validate` builds the catalog with the item, in place of item `id` when given, and saves
  nothing: its issues, and what it makes. A relation gives its navigations' names; a virtual entity the entity it
  makes, and its query's diagnostics placed in its text; settings and overrides the entity as they make it. `breaks`
  lists the other items that work now and wouldn't with it (a virtual entity renamed that others use).
- **Requests.** What can be told without the catalog is a 400 by field: a path that isn't one, a virtual entity
  without a namespace, a relation without columns or with more on one side, a column twice, a type that isn't one,
  an override that neither renames nor hides. Names are trimmed. An update's fields are named under its item
  (`relation.from`).
- **One for each thing.** An entity has one item of settings, a navigation one override, a name one virtual
  entity, and a relation is made once: another is `409 overlay-item-exists`. One reaching the same thing another way
  (`shop.main.orders`, columns in another order, names alike but for case, a relation the database declares) is
  kept, and left out of the catalog (GDQ5016).
- **Order.** The catalog takes items in the order they were made, so of two relations wanting a navigation name,
  the first gets it.
- **Audited**, each change with what changed, and in the catalog's version, so clients see it.

## Changes

Data managers and administrators change rows. Each user's changes are kept on the server, under `/api/changes`,
until they are committed or reverted, so they outlast refreshes, tabs and restarts.

**Pending changes.** `GET /api/changes` gives the user's changes, in the order they were first made, and the
`version` of the set, which goes up with each change to it. Each change is to one row:

- `update`: new `values` for columns of a row, named by its `entity` and `key` (its key's values, in key order).
  `rowId` is the key as JSON, as browsing gives rows' `id`s.
- `insert`: a new row, named by the client's `tempId`, with its `values`.
- `delete`: a row to delete.
- `original`: the values the row had when it was first changed (of the columns changed, or those given for a row to
  delete). They must still hold when the change is committed.
- `display`: what to show for the rows that new foreign key values refer to, by navigation (`customer`), as the
  client gave it.

Values are as rows' values are sent (see [Browsing](#browsing)).

**Operations.** `POST /api/changes/ops` applies its `ops` together, or none of them:

| Op | Takes | Does |
|---|---|---|
| `set` | `entity`; `key` and `original` (for each column set), or `tempId`; `values`; `display` | Sets values of a row, or of a new row. |
| `insert` | `entity`, `tempId`, `values`, `display` | Adds a new row. |
| `delete` | `entity`; `key` and `original` (at least one column), or `tempId` | Deletes a row; a new row is just dropped. |
| `revert` | `entity` and `key` or `tempId`, or `change` (its id); `columns` | Drops the row's change, or only its values for `columns`. |

- **They merge.**
  - A row has one change, and a column's original is the value it had when it was first changed, however often it
    changes again: `original` is needed only then.
  - A value set back to its original is no change (compared as values of the column's type: `5000` is the `5000.00`
    it had). A row with no change left is dropped.
  - Deleting a changed row drops its new values and keeps its originals, and a row to delete must be reverted
    before it is changed again. Deleting a new row drops it.
  - What to show for a navigation's row goes when none of the navigation's columns has a new value.
- **They are checked** against the catalog as it stands. The entity must be a table the user may change: in a
  writable source, with a primary key of its own for changes to rows that are there. Each column must be one they
  may give a value: not part of the key (in an update), computed, a row version, an identity column the database
  numbers, binary, or of an unknown type. Each value must be of its column's type, and a column that can't be null
  takes no null. A value has at most 1,000,000 characters as it is sent. What is wrong is a 400 by the operation's
  field, named as it was given (`ops[1].values.total`), and nothing is applied.
- **Versions.** With a `version`, the operations apply only to the changes at that version (`409
  concurrency-conflict`). Without one, they apply to the changes as they are, so two tabs' operations both do.
- **At most** `Changes:MaxChanges` changes are kept for a user.
- `DELETE /api/changes` drops every change, or those of a `source` (an alias) or an `entity`, with `version` as
  for operations.
- **Deleting a connection** drops every user's changes to its rows, as a connection given its alias later would be
  another database.

**Preview.** `POST /api/changes/preview` checks every change against the catalog again (an entity or a column may
have gone, a value may not be of its column's type any more), and plans them as the engine does (see the language
reference, 11). It gives:

- `scripts`: one for each connection, in the order their changes first appear. Each has its `source`, `kind` and
  `dialect`; its `text`, with the values written in, which may be edited (`editable`: by administrators, and by
  others but for databases that run in the application, DuckDB's, see Commit); and its `statements`, each with the
  `change` it carries out. Inserts run first, the tables they refer to first, then updates, then deletes.
- `issues`: why changes can't be made (a new row without a value a column needs, a value that doesn't fit its
  column), each with its `change` and `column`. The other changes' statements are shown all the same.
- `multiConnection`: whether the changes write to more than one connection. Each connection then commits on its
  own, one after another: should a commit fail after another succeeded, the changes are left partly written.
- `planId`: a plan to commit, when there are no issues, until `expiresAt` (`Changes:PlanLifetime` after the preview).
  A preview replaces the user's plan before. `version` and `catalogVersion` say which changes and catalog it is of.

**Commit.** `POST /api/changes/commit` takes the `planId` and `version` of a preview, and the `scripts` the user
edited, each `{source, text}`.

- **Stale plans.** A plan that expired or was replaced, changes changed since the preview, or a catalog built since
  (a schema read, the overlay changed) is `409 plan-stale`: preview again. A plan is committed once.
- **One at a time.** While a user's commit runs, their changes can't be previewed or committed again (`409
  commit-in-progress`): until those it writes are cleared, a plan of them would write them twice.
- **As planned.** A script as the preview gave it runs as planned. Each statement must change one row, and changes it
  only if the row's originals still hold; a row changed since it was read is a conflict.
- **Values' line breaks** are written by their codes in a script's text (`('a' || char(10) || 'b')`, PostgreSQL's
  as escape strings, `E'a\nb'`), so an editor that makes the text's line breaks one kind changes no value.
- **As edited.** A script whose text differs (line breaks and white space at its end aside) runs as edited. It is
  split into statements, which may only insert, update, delete or merge, unless an administrator allows any
  (`allowAnyStatement`; another user gets a 403). Transactions are the engine's in any case. How many rows edited
  statements change isn't checked, but those that changed none are told of. A script that can't run is a 422
  (`script-invalid`, with its problems), and the plan may still be committed.
- **DuckDB's scripts** are edited by administrators only (a 403 for others). DuckDB runs in the application, with
  its rights, and reads any file or URL a statement names, which no guard can keep it from; other databases have
  logins of their own, whose rights keep a statement to what the login may do.
- **Together.** Each connection's statements run in a transaction of its own. The connections commit only when every
  statement has run, and a failure rolls every one of them back (see the language reference, 11). The commit runs
  to its end even if the client goes away, within `Query:Timeout`.
- **What came of it.** The answer has:
  - the `outcome`: `committed`, `rolledBack` or `partiallyCommitted`;
  - each script's `status`, and its statements with the rows they changed;
  - the `failure`: its kind (`connection`, `statement`, `conflict`, `commit` or `timeout`), its source, the change
    whose statement failed, and why. A connection that couldn't be opened is named, without the database's words,
    which are logged;
  - the new rows as the database made them (`inserted`): the `tempId`, the `key` and `rowId`, and the values,
    generated keys and defaults included;
  - `warnings`: edited statements that changed no rows, and what couldn't be done once the changes were written
    (the audit, clearing them). The answer tells what was written whatever fails after it.
- **Cleared.** The changes each connection that committed wrote are cleared (for an edited script, all of its
  connection's), unless they were changed again since the preview. `changes` gives those left: all of them, when
  nothing was committed.

**The commit audit.** Every commit is recorded before it runs: its user, its scripts as they run (with the values
written in, or as edited), and then what came of each. A commit the application didn't live to finish is `unknown`
when it starts again, as each connection may have committed or not. Administrators read it: `GET
/api/audit/commits` (newest first, paged with `before` and `take` as the admin audit is), and `GET
/api/audit/commits/{id}`, with its scripts.

## Security

**Headers.** Every answer has these, those made over for a failure too:

- `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Cross-Origin-Opener-Policy` and
  `Cross-Origin-Resource-Policy: same-origin`, and a `Permissions-Policy` that turns devices off;
- `Referrer-Policy: no-referrer`, as the client's addresses name entities, filters and rows;
- a `Content-Security-Policy`. The API's loads nothing (`default-src 'none'; frame-ancestors 'none'`). The client's
  is `Security:ContentSecurityPolicy`, by default:

  ```text
  default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:;
  worker-src 'self' blob:; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'
  ```

  The client's own scripts only; inline styles, which the grid and the editor set; the editor's workers. The client
  is built without inlined critical CSS (Angular's `inlineCritical: false`), whose loader is an inline script.

**Caching.** The API's answers are `Cache-Control: no-store`, as they hold data. The client's page is `no-cache`, so
a new version is picked up at once. Its files named by their content's hash are kept for a year, and its other files
are checked each time. A hash is eight capitals and digits (`main-LKPGWKWT.js`: the client's own files, media and
workers) or, for its lazy chunks, eight of base64url's letters, digits, `_` and `-` (`chunk-BhQOlwLr.js`). It has a
capital, and a capital or a digit after its first character, so words and dates of eight aren't taken for hashes
(`settings-overview.txt`, `settings-Overview.txt`, `notes-20250101.txt`).

**HTTPS.** Answers over HTTPS have `Strict-Transport-Security` (`Security:Hsts`, for `Security:HstsMaxAge`) outside
development, and never for `localhost`. With `Security:RequireHttps`, requests over HTTP are redirected to HTTPS:
on `Security:HttpsPort`, or the port the server listens on for HTTPS. Cookies are `Secure` when the request came
over HTTPS.

**Behind a reverse proxy.** With `Proxy:Enabled`, a proxy's `X-Forwarded-For` and `X-Forwarded-Proto` say who the
client is and how it connected. That client's address is what rate limits count by, and HTTPS through the proxy is
HTTPS to the application. Only trusted proxies are believed: those in `Proxy:KnownProxies` and
`Proxy:KnownNetworks`, or, with neither, one on the same machine (an address written as IPv6, `::ffff:10.0.0.1`,
is the IPv4 one). What another says is ignored, as is everything without `Proxy:Enabled`. ASP.NET Core's own
switch, `ASPNETCORE_FORWARDEDHEADERS_ENABLED`, believes every client, so it stops the application.

**Rate limits.** Refusals are `429 too-many-requests`, with `Retry-After` when there is a time to give.

- **Requests:** a user may make `RateLimits:RequestsPerMinute` requests to the API a minute, each user apart. Signed
  out, each address is counted apart, an IPv6 address by its /64 (which one client may have), and requests refused
  for want of a session count too. A minute's requests may come at once, and are given back at that rate, about a
  second's worth at a time. Health checks aren't counted.
- **Queries:** a user may run `RateLimits:ConcurrentQueries` requests that run queries or reach sources at once,
  with `RateLimits:QueuedQueries` more waiting their turn. These are browsing, running queries, commits of changes,
  trying connections, and trying overlay items. Checking, explaining and following a query's links run nothing,
  so an editor's checks don't wait for queries; nor do previews of changes.
- **Sign-ins:** see [Users and signing in](#users-and-signing-in).

**The log.** Whatever writes to the log, secrets in its entries are masked as `********`: in messages, values and
exceptions. These are masked:

- secrets the application knows (connections' passwords and tokens, as they are saved, read or tried, and the
  bootstrap password), of six characters or more, where they stand alone: not within a longer word or number;
- the value of any setting that looks like a secret, as connection strings and JSON write them (`Password=...`,
  `Pwd: ...`, `"token": "..."`, `...Key=...`), to the setting's end;
- passwords in URLs (`postgres://user:********@host`).

An entry with a secret goes on with its values masked, and its exception as its text masked (its type, message and
stack, as it reads). Scopes aren't masked; the application's hold request paths and ids. Nothing the application
logs should hold a secret in the first place: this is the last line.

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
- **OpenAPI:** the API is described at `/api/openapi/v1.json` (OpenAPI 3.1), for signed-in users.
  - The test `HostTests.TheOpenApiDocumentDescribesTheApi` keeps a reviewed copy in
    `tests/GalaxyData.Web.Tests/Hosting/Snapshots/`, so a change to the API shows in review.
  - The client's types are made from it (`npm run api` in `src/client`; see [client.md](client.md#the-apis-types)).

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
| 400 | `invalid-request` | The request's values aren't valid. `errors` names each one as the request's JSON does (`relation.toColumns`, `columns[0].name`), with what is wrong. |
| 400 | `xsrf-token-invalid` | A request that changes anything came without a valid anti-forgery token. |
| 401 | `unauthenticated` | No one is signed in, or the session has ended. |
| 401 | `invalid-credentials` | No user has that name and password. |
| 401 | `locked-out` | Too many failed sign-ins; `detail` says when to try again. |
| 403 | `forbidden` | The user's role doesn't allow it. |
| 403 | `account-disabled` | The user is disabled (given only with the right password). |
| 403 | `password-change-required` | The user must change their password first. |
| 409 | `concurrency-conflict` | It was changed by someone else since it was read: its `version` differs. |
| 409 | `user-name-taken` | Another user has the name (names ignore case). |
| 409 | `own-account` | Administrators can't demote, disable, delete or reset themselves. |
| 409 | `last-admin` | The change would leave no enabled administrator. |
| 409 | `alias-taken` | Another connection has the alias (aliases ignore case). |
| 409 | `query-name-taken` | The owner has a saved query of the name (names ignore case). |
| 409 | `overlay-item-exists` | The overlay has an item for that already: settings for the entity, an override of the navigation, a virtual entity of the name. |
| 409 | `plan-stale` | A preview of changes can't be committed: it expired or was replaced, or the changes or the catalog changed since. Preview again. |
| 409 | `commit-in-progress` | The user's changes are being committed: they can't be previewed or committed again until that has finished. |
| 422 | `wrong-password` | The current password given to change it isn't right. |
| 422 | `weak-password` | A new password doesn't meet the policy; `detail` says how. |
| 429 | `too-many-requests` | Too many requests: sign-ins from the address, or a user's requests or queries (see [Security](#security)). `Retry-After` says when to try again, when it can. |
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

The client is served from `wwwroot`, which publishing fills from the client's build (it isn't in the
repository). Every path that isn't the API's is the client's page, `index.html`, served with `Cache-Control:
no-cache`, so a new version is picked up at once. That includes paths with dots and matrix parameters, such as
`/browse/shop.customers;f=country:eq:ZA/orders`.

**Asset files are the exception.** A path whose last segment has no matrix parameters and ends in an extension of
the kinds the client is built into is a file, and a 404 when it's missing. Those extensions are: `.js`, `.mjs`,
`.css`, `.map`, `.json`, `.txt`, `.webmanifest`, `.wasm`, images, and fonts. Paths under `/browse` are always the
client's: their segments are entities' names, which may end as files do (`/browse/geo.map`).

**Paths under `/api`** that no endpoint has are the API's 404s, and a method an endpoint doesn't take is a 405.
They are never the client's page.

**Signed out.** To anyone not signed in, a path that no endpoint has is answered 401, whatever it is: a missing
API path, a method an endpoint doesn't take, a missing file. That way they see nothing of the API's shape. The
client's page, its files and the anonymous endpoints are served to anyone.
