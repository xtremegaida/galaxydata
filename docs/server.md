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
| `admin` | also manage users, connections, the overlay, and see the audit |

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
