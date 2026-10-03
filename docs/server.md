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
| `admin` | also manage users and connections (and, with later features, the overlay), and see the audit |

**Sessions.** Signing in starts a session: the `gd.auth` cookie, which is HttpOnly and SameSite=Strict. It is a
browser-session cookie that ends after `Auth:SessionIdleTimeout` without requests.

- **Changes that end sessions.** A user's sessions end within a request when their password is reset, their role
  changes, or they are disabled or deleted. Changing one's own password ends one's other sessions.
- **Signing out** removes the cookie. A copy of it made before stays valid until it expires or one of those
  changes happens: sessions aren't kept on the server.

**Passwords.** Passwords are hashed (ASP.NET Core Identity's PBKDF2). A password must:

- have at least `Auth:MinimumPasswordLength` characters, and at most 256;
- not contain the user name.

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
a reverse proxy, every client has the proxy's address until forwarded headers are set up.

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
- **Signed in:** changing one's own password, even while it must be changed.
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
  be entered again.

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

**The catalog.** It is built from each connection's newest snapshot when first needed, and again after anything it
is built from changes. A request after a change waits for it, so none reads the old one.

- **Its version** is a hash of what it was built from: the same after a restart, different after any change.
- **The header.** API answers carry the version in the `X-Catalog-Version` header, so clients know when to read the
  catalog again. Answers that don't read the catalog carry it too, while it is up to date.
- `GET /api/catalog` gives the version, the sources (how their schemas stand, how many entities each has), and
  what building it found (a table whose shortcut a schema's name hides).

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
with their logical types, its keys and navigations, and what the user may do with its rows.

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
  `id` the key as JSON text (the same each time; null when the rows have no key), and `r` the display values of the
  rows each refers to.
- **Values.** Values travel as JSON holds them exactly. Whole numbers past `int32`, decimals, dates, times,
  date-times (ISO 8601; offsets in UTC), guids, binary (base64) and doubles that aren't numbers (`NaN`) are text.
  Values sent (keys, filter values) are read the same way; numbers may also be JSON numbers.
  - Decimals have a point, and no thousands separators.
  - Date-times with an offset end with it, with `Z`, or with neither (UTC).
- **`schema`** (with `includeSchema`):
  - the columns, with their types, whether each is part of the key, whether the user may change it and give it a
    value in a new row, and where its values come from;
  - the references (navigations along foreign keys, and the columns that hold them);
  - the collections that refer to each row;
  - what the user may do with the rows.
- **Counting.** `hasMore` says whether more rows follow. `total` (with `includeCount`) is counted alongside the
  page within `Query:CountTimeout`. When counting takes longer, it is null, unless the page is the last.

**Trails.** `POST /api/browse/trail` takes the crumbs of a path through the data and says where each leads:

- **The crumbs.** The first names an entity, and each after it a navigation from the row chosen in the one before.
- **What it says of each.** The entity it reaches, and the display value of the row chosen in it, if that row is
  among the rows the crumb leads to (`found`). A crumb that can't be followed says why, and is the last. A trail has
  at most 50 crumbs.

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
| 400 | `invalid-request` | The request's values aren't valid. `errors` names each one, with what is wrong. |
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
| 422 | `wrong-password` | The current password given to change it isn't right. |
| 422 | `weak-password` | A new password doesn't meet the policy; `detail` says how. |
| 429 | `too-many-requests` | Too many sign-ins from the address; `Retry-After` says when to try again. |
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

**Signed out.** To anyone not signed in, a path that no endpoint has is answered 401, whatever it is: a missing
API path, a method an endpoint doesn't take, a missing file. That way they see nothing of the API's shape. The
client's page, its files and the anonymous endpoints are served to anyone.
