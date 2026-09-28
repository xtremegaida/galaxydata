# GalaxyData — Federated Query Engine + Database Manager

## Context

The repo is empty except for an untracked `Parser/` folder: a Pratt expression parser (`GalaxyData.Common.Ast`) and a `DynamicNode` value type, copied from another codebase. We are building two products on top of it:

1. **GalaxyData.Query**, a reusable library. It is a LINQ-method-chain query language that federates Postgres, SQL Server, SQLite and DuckDB sources. It produces results with column lineage and walkable reference links, and generates dialect-specific DML.
2. **GalaxyData Manager**, an ASP.NET Core + Angular database explorer that uses the library. It provides browsing, navigation, editing (update, insert, delete), custom queries, and user/connection/overlay admin.

**Decisions confirmed with the user**
- **Build the engine first.** It becomes a tested library plus a CLI REPL, then the backend is built, then the Angular app.
- **Commits span connections through coordinated per-connection transactions.**
  - Begin a transaction on every touched connection and run all DML.
  - Commit only if every step succeeded; otherwise roll everything back.
  - There is no two-phase commit, so a failure during the final commit loop is reported as `Partial`.
  - The preview warns when a batch touches more than one connection.
- **Inserts are in scope**, alongside cell edits and row deletion.
- **UI stack:** Angular (zoneless, standalone, signals), Material/CDK, AG Grid Community and Monaco.
- **Editing the SQL preview:** Data Managers and Admins can edit it. Edited scripts are split into statements, and anything other than INSERT, UPDATE, DELETE or MERGE (including `WITH …` forms of those) is rejected. Admins can bypass the check per commit.

**Environment:** .NET SDK 10.0.103, Node 24, and Docker (Testcontainers runs the Postgres and SQL Server tests). There is no global Angular CLI, so we use `npx @angular/cli`. Code style follows the parser: 3-space indents, Allman braces, file-scoped namespaces.

---

## 1. Solution layout

```
global.json (10.0.103, rollForward latestFeature, MTP runner) · GalaxyData.slnx
Directory.Build.props (net10.0, Nullable, TreatWarningsAsErrors) · Directory.Packages.props (CPM) · .editorconfig
src/
  GalaxyData.Common/            parser + DynamicNode moved from Parser/ (namespaces unchanged)
  GalaxyData.Query/             core engine; System.Data.Common only; all 4 SQL dialects live here
  GalaxyData.Query.Sqlite/      Microsoft.Data.Sqlite      — connection, introspection, type mapping, readers
  GalaxyData.Query.DuckDb/      DuckDB.NET.Data.Full       — source provider + merge engine
  GalaxyData.Query.PostgreSql/  Npgsql
  GalaxyData.Query.SqlServer/   Microsoft.Data.SqlClient
  GalaxyData.Query.Excel/       Excel Folder provider (on .DuckDb; no Excel library)
  GalaxyData.Query.Cli/         `gdq` CLI/REPL (System.CommandLine, Spectre.Console)
  GalaxyData.Web/               ASP.NET Core host, feature folders, serves Angular from wwwroot
  client/                       Angular workspace (builds into GalaxyData.Web/wwwroot)
tests/
  GalaxyData.Common.Tests/  GalaxyData.Query.Tests/ (no DBs: binder, rules, golden SQL)
  GalaxyData.Query.IntegrationTests/ (SQLite+DuckDB+Excel, federation, differential)
  GalaxyData.Query.ContainerTests/ (Testcontainers PG+MSSQL, same conformance suite)
  GalaxyData.Web.Tests/ (WebApplicationFactory) · fixtures/ · e2e/ (Playwright)
```

**Package versions.** Pin the versions already in the local NuGet cache:

| Package | Version |
|---|---|
| DuckDB.NET.Data.Full | 1.5.5 |
| Npgsql | 10.0.3 |
| Microsoft.Data.SqlClient | 7.0.3 |
| Microsoft.Data.Sqlite | 10.0.12 |
| xunit.v3 | current in cache |
| Verify.XunitV3 | current in cache |
| Shouldly | current in cache |
| Testcontainers | 4.x |
| EF Core Sqlite | 10 |
| DocumentFormat.OpenXml | current (test fixtures only) |

---

## 2. Parser changes (M0)

Move `Parser/**` to `src/GalaxyData.Common/`. Every change is additive:

1. **Replace the missing helper.** `ExpressionLexer.LexHex` uses `GalaxyData.Common.Helpers.Colour.Nibble`, which isn't in the repo. Replace it with a private `TryHexDigit(char, out int)`, reuse it in `IsHex4` and `ToHex`, and drop the `using`.
2. **Add span tracking.** Add `Start`/`End` to `SyntaxNode`; `At` keeps its current meaning.
   - Record `LastEnd` in `ParseState` during `Advance`.
   - Capture `start` before `ParsePrimary` in `ParseExpression`.
   - `Bounded(node, start, state)` sets the span.
   - Spans are needed for diagnostics, default column names, lineage `ExpressionText`, drill-down link text and Monaco markers.
3. **Add `ExpressionParserOptions`.**
   - `MaxDepth`: default 64. The query parser uses 256, because each `.method()` adds about 2 levels.
   - `FractionalLiteralsAsDecimal`: `3.5` becomes a decimal, `3.5e0` stays a double.
4. **Accept string-named arguments** in `ParseArgument`, so `select("Total Spend": sum(total))` works.
5. **Fix `DynamicNode.AsScalar()`.** The Decimal, Guid and DateTimeOffset branches return the wrong values.
6. **Add `QueryOperatorTable`** in `GalaxyData.Query/Language/`. Start from `CreateDefault()`, then:
   - remove `++` and `--`;
   - add `<>` (at 90), `and` (50), `or` (40), `not` (prefix, 55) and `in` (100);
   - keep `=`, because `let` needs it. The binder rejects `=` in expressions with the hint "use ==".

`QueryParser` wraps `ExpressionParser` and converts `SyntaxErrorException` into `QueryDiagnostic(code, severity, message, start, end)`.

---

## 3. Language semantics (binder contract)

**Syntax mapping**

| Parsed form | Meaning |
|---|---|
| `Call(Binary(".", recv, m), args)` | method `m` on `recv` |
| `Call(Identifier f, args)` | global function |
| `Binary(".")` | member access |
| `Index(recv, ["name"])` | member access for any name (e.g. `xl["Budget 2024"]["Sheet 1"]`) |
| `Postfix(".*")` | spread; valid only in `select` or `extend` |
| `x := e;` or `let x = e;` | named subtree; top level only |
| `$name` | external parameter |
| `ns::x` | forces a lookup from the catalog root |

Rejected: `For`, `If`, `Break`, `Continue`, nested blocks, `=` in expressions, and (in v1) object literals.

**Entity names.** Entities are named `<alias>.[<schema>.]<table>`. Default-schema tables are also linked directly under the alias. If a schema name equals a default-schema table name, that shortcut is suppressed and a warning is raised. Matching is case-insensitive; an exact-case match wins, otherwise it's an ambiguity error.

**Scopes**, searched innermost first:
1. lambda parameters;
2. the implicit row: `it`, `outer`/`inner` inside join arguments, `key` in group scope, then the row's columns, navs and record members, then a column unique across record members;
3. the group scope;
4. outer rows (correlation);
5. named subtrees and `$params`;
6. the catalog.

A lambda argument disables the implicit row scope. Join conditions must use `outer.`/`inner.` or a 2-parameter lambda. Select aliases aren't visible to sibling items in the same select; use `extend`.

**Methods** (`Binding/MethodTable.cs`):
- Filtering and shaping: `where`, `select`, `extend`, `groupBy`, `join`/`leftJoin(seq, cond, name: outer, name: inner)`, `selectMany`, `distinct`.
- Ordering and paging: `orderBy`/`orderByDescending`/`thenBy`/`thenByDescending`, and `desc(x)` inside `orderBy`. `take`/`skip` accept a constant or a `$param`.
- Set operations: `union`, `concat`, `intersect`, `except`. Columns are matched by name.
- Aggregates and quantifiers: `count`/`sum`/`avg`/`min`/`max`/`countDistinct`, `any`/`all`/`contains`, `first`/`firstOrDefault`.
- Scalar functions work UFCS-style: `x.f(a)` means `f(x, a)`.

**Functions** (`FunctionRegistry`): string, numeric, date, null-handling, conversion, and `like`/`ilike`/`icontains`. Invariant: every function must be implementable in DuckDB, so fragmentation never fails because of a function.

**Group scope** (examples 2 and 3):
- Element members become collections.
- A collection used as a scalar must structurally match a key part; it then becomes `BoundKeyRef`. Otherwise the error is "not part of the group key; aggregate it or add it to groupBy".
- `sum(total)` binds its argument in element scope. `orders.sum(total)` binds `total` in the `orders` member scope.
- `where` after `groupBy` is HAVING.
- `groupBy(customer)` keys on the FK.

**Semantic contract.** Results must be the same wherever an operator runs; this is emulated per dialect and covered by differential tests.
- `== null` means IS NULL, and SQL three-valued logic applies.
- Nulls sort smallest.
- int / int gives a double.
- `substring` is 1-based.
- `sum` over an empty set is 0.
- String comparison follows the executing engine's collation. This is the documented exception; `ilike` and `icontains` give consistent case-insensitive matching.

---

## 4. Engine design (`GalaxyData.Query`)

### 4.1 Catalog and introspection
- **`ISchemaIntrospector.IntrospectAsync(DbConnection, IntrospectionOptions)`** returns a `SourceSchema`: tables and views, columns with a native type plus a logical `ScalarType` (identity, computed, default, rowversion flags), PK, unique keys, indexes, FKs (with an `IsEnforced` flag), row-count estimates and triggers. The model serializes to JSON with source generation.
  - Per provider: PG `pg_catalog`, MSSQL `sys.*`, SQLite `sqlite_schema` + `pragma_*`, DuckDB `duckdb_*()`.
  - The SQL lives in embedded `.sql` resources.
- **`ScalarKind`:** Boolean, Int16/32/64, Decimal(p,s), Single, Double, String (with `IsAnsi`/`Length`), Binary, Guid, Date, Time, DateTime, DateTimeOffset, Interval, Json, Unknown. Unknown can be selected but only null-tested.
- **Model:** `ICatalog` with `CatalogNamespace`, `EntityDef` (`TableEntity` / `VirtualEntity`), `ColumnDef`, `RelationDef` and `NavigationDef`. `CatalogBuilder.Build(sourceSchemas, CatalogOverlay, FunctionRegistry)` produces it, so the naming conventions stay in the library.
- **Navigation naming.**
  - Forward: strip `_id`/`id`/`_fk`/`_key` from the FK column name, falling back to the target table name.
  - Inverse: a collection named after the source table, or a single reference when the FK is unique.
  - Several FKs to the same table, or self-references, are named `<table>_by_<forward>`.
  - A collision falls back to the constraint name, then to `_2`, `_3`, …
  - The overlay can rename or hide any nav.
- **Display column:** from the overlay if set, else a column named `name`/`title`/`code`/`description`, else the first string column, else the key.
- **Overlay** (`CatalogOverlay`):
  - cross-source `relations`;
  - `virtualEntities` (query text plus an optional key);
  - per-entity settings (declared key, display column, hidden, column type overrides);
  - nav renames.

  The builder works in three phases: physical entities, then virtual entities in dependency order (cycles become diagnostics), then relations that touch virtual entities.

### 4.2 Binder → bound tree
- **Types:** `ScalarType`, `RecordType` (with entity identity), `SequenceType`, `GroupSequenceType`, `CollectionType`.
- **Range variables:** a `RowVariable` identifies each one. A reference to a `RowVariable` from an outer scope is a correlation.
- **Coercion:** a literal adopts the type of the column it's compared with, including MSSQL varchar and length, which keeps index seeks. String literals compared with date or guid columns are parsed at bind time.
- **Diagnostics:** codes GDQ1xxx (parse), GDQ2xxx (bind), GDQ3xxx (plan), each with a span.

### 4.3 Logical plan, lineage and links (`Planning/Logical/`)
- **Operators:** Scan, Filter, Project, Join (Inner/Left/Semi/Anti/Cross, with `JoinOrigin`), Aggregate, Sort, Limit, Distinct, SetOp, Apply, Values and Empty. Columns are identified by a stable `ColumnId`.
- **Lowering** keeps a per-row-variable nav-join cache, so `customer.name` and `customer.city` share one LEFT JOIN. Collection navs and correlated aggregates become `Apply`. Named subtrees are re-instantiated with fresh ids at each use. `now()` is evaluated once per query.
- **Lineage** is attached to `ResultSchema` at lowering, so explain can show it without executing:
  - `ColumnLineage` has a kind (Direct, Computed, Aggregated, Constant, Union or Unknown), a list of `PhysicalColumnRef`s, `ExpressionText` and `NavigationPath`.
- **Links:**

| Link | Produced for |
|---|---|
| `RowLink(target, keyOrdinals)` | FK columns, and nav-valued items with hidden target-PK columns |
| `CollectionLink(target, targetColumns, valueOrdinals)` | collection navs and nav aggregates |
| `DrillDownLink(queryText, keyExprs, valueOrdinals)` | groupBy aggregates |
| `EditTarget(entity, column, keyOrdinals)` | direct columns whose PK is available |

  `ResultSchema.RowIdentity` exposes the inverse navs for rows that belong to a single entity. Hidden key columns are added only along row-preserving paths.

### 4.4 Optimizer and federation (`Planning/Optimizer/`)
Rules implement `IRewriteRule` and run in phases to a fixpoint.

1. **Normalize.** Constant folding, predicate simplification, and **decorrelation**:
   - `any` becomes a Semi join; `all` and `not in` become Anti joins;
   - a correlated aggregate becomes a LEFT JOIN to a group-by (a group-join) with COALESCE;
   - `MergeGroupJoins` combines aggregates over the same nav;
   - non-equality correlations stay as correlated subqueries.
2. **Pushdown.**
   - filter through Project, Join, Aggregate and UnionAll;
   - transitive equi-join predicates;
   - outer-to-inner join conversion;
   - elimination of an unused many-to-one nav join (Inner only through an enforced FK);
   - PullUpSort, which drops a sort without a limit under Aggregate, Distinct or SetOp;
   - Limit/TopN through many-to-one Left joins when the sort keys are on the left (the grid-paging win).
3. **Prune.** Column pruning, protecting hidden root columns. Partial aggregate pushdown is a stretch goal.
4. **`SiteAssigner`**, bottom-up:
   - A Scan is assigned to its source. Excel scans are assigned to `merge`.
   - An operator stays on its input's site if `DialectCapabilities.CanTranslate` accepts all its expressions; otherwise it goes to `merge`.
   - At a boundary, translatable conjuncts of a filter stay in the fragment and the rest go to merge.
   - If the whole plan is on one site, the fast path skips DuckDB entirely.
5. **Cross-source joins:** full fetch by default. An **adaptive bind-join** runs the driver side first and reads its distinct keys:

| Key count k | Action |
|---|---|
| 0 | skip the target |
| ≤ 10k | fetch the target in batched IN-lists, sized to the dialect's parameter limit (MSSQL 2100) |
| more | full fetch |

   Uncorrelated cross-source scalar subqueries run first and become runtime parameters.
6. **Cardinality:** simple heuristics, using introspected estimates or 10k when there are none. Explain warns about full fetches estimated above 1M rows.

### 4.5 SQL generation (`Sql/`)
- **SQL AST:** Select, SetOperation, Join, DerivedTable, and Insert/Update/Delete with Returning.
- **`SqlBuilder`** fills clauses in order and wraps the current select as a derived table when chain order needs it (`take(10).where(…)`). Semi and Anti joins are written as `[NOT] EXISTS`. Table aliases are readable (`o`, `c`, `c2`).
- **`SqlDialect` hooks:** identifier quoting, parameter prefix, paging, boolean handling, string concatenation, LIKE escaping, date functions, integer division, null ordering, `MaxParameters`, and function templates. A missing template means the function isn't translatable.

| Hook | PG | MSSQL | SQLite | DuckDB |
|---|---|---|---|---|
| Paging | LIMIT/OFFSET | TOP, or OFFSET/FETCH | LIMIT/OFFSET | LIMIT/OFFSET |
| Booleans | native | `bit` | 0/1 | native |

  MSSQL parameters are typed from the column's `ScalarType`. Only NULL, booleans and limit integers are inlined; all other values are parameters. `FormatLiteralForDisplay` is used for explain and preview.

### 4.6 Execution
- **API:**
  - `QueryEngine(ICatalog, ISourceRegistry, IMergeEngine, options)`.
  - `.Prepare(QueryRequest{Text, Parameters, Paging})` is pure and returns a `PreparedQuery`, which offers `Schema`, `Explain()`, `ExecuteAsync(ct)`, and `ForCount()` (no sort or paging, nav joins eliminated).
  - `QueryResult` is an async row stream with `ResultSchema`, `ExecutionStats` and `Warnings`.
- **Providers:** `ISourceProvider` has Dialect, Affinity, Introspector, TypeMapper, BindParameters and typed `ColumnReader`s. The app implements `IConnectionFactory.OpenAsync(alias)`, so the library never holds secrets.
- **`QueryText` helpers:**
  - `QuoteName` and `IsBareIdentifier`, following the engine's operator table.
  - `Compose(text, filters, sort)`, which wraps the last statement as `(<last>).where(..).orderBy(..)` using `SplitStatements`.
  - With paging, the sort is stabilized by appending the row-identity key.
- **Merge engine:** one process-wide DuckDB in-memory instance.
  - It is configured with `memory_limit`, `temp_directory` and `threads`.
  - Each query gets its own schema `q_<n>`, dropped on dispose.
  - Fragments load in parallel (4 by default) on `Duplicate()` connections, through a typed Appender loader.
  - The final query runs in streaming mode.
  - Cancellation uses a linked CTS plus DuckDB `Interrupt()`. There are per-fragment and overall timeouts, and an optional `MaxFetchedRows`.
  - SQLite's dynamic typing is handled with parse converters: a failure is an error with row and column context, or null in lenient mode.

### 4.7 Explain
`QueryExplain` can be produced without executing anything. It contains:
- diagnostics;
- the `ResultSchema`, including lineage and links;
- a summary line;
- a `PlanNodeDto` tree with the site, detail (unparsed back into query syntax) and row estimates;
- a `FragmentDto` per fragment with the SQL, parameters, strategy and bind-join template;
- the merge SQL;
- in Verbose mode, the plan after each optimizer phase.

An `ExplainTextRenderer` draws it for the CLI.

### 4.8 DML (`Dml/`)
- **Input:** a `ChangeSet` of `InsertRow`, `UpdateRow(key, original, new)` and `DeleteRow(key, original)`.
- **Planning:** `DmlPlanner.Plan` returns a `DmlPlan` with one `DmlScript` per connection. Each script has `DmlStatement`s (SQL, parameters, expected rows, returns-rows) and `ToDisplayText(inlineParameters)`.
- **Rules:**
  - Only writable `TableEntity`s (a table with a PK, and a provider that supports DML) can be written.
  - UPDATE sets only the changed columns.
  - The WHERE clause is the PK plus `col = @orig`, or `IS NULL` for a null original. Float, text/blob and xml columns are left out; a rowversion or `xmin` column is used when there is one.
  - INSERT leaves out identity, computed and unset columns.
  - Returned rows: `RETURNING *` on PG, SQLite and DuckDB. MSSQL uses `OUTPUT INSERTED.*`, or `SCOPE_IDENTITY()` when the table has triggers.
  - Statement order: inserts parents-first, then updates, then deletes children-first.
- **`DmlExecutor`** opens all connections and begins a transaction on each (SQLite uses IMMEDIATE). It runs the statements and checks expected rows. Any failure rolls back all connections. Otherwise it commits in order and reports `PartialCommit` if a later commit fails.
- **Edited scripts:** `SqlScriptSplitter` is dialect-aware (quotes, comments, PG `$$`, MSSQL `GO`). The DML-only guard classifies each statement's leading keyword, skipping `WITH` CTE heads. Row-count checks are skipped for edited scripts.
- **`ResultRowEditor`** turns grid edits into `RowChange`s using the `EditTarget` ordinals.

### 4.9 Excel Folder (`GalaxyData.Query.Excel`)
- **Enumeration with no Excel library.** `WorkbookReader` uses `ZipArchive` and `XmlReader`:
  - `xl/workbook.xml` and its rels give the sheet names and state; chart and hidden sheets are skipped;
  - `<dimension>` gives a row estimate;
  - `~$` lock files are skipped;
  - only top-level `*.xlsx` files are read.
- **Mapping:** the folder is mounted in the merge engine (colocated) with `ATTACH ':memory:' AS "<alias>"`. Each file becomes a schema. Each sheet becomes a table created with `CREATE OR REPLACE TABLE … AS SELECT * FROM read_xlsx(path, sheet:=…, header:=true, all_varchar:=opt)`, materialized on access with an mtime and size check.
- **Introspection:** columns come from `DESCRIBE`.
- **Read-only.**
- **Offline extension.** `gdq duckdb install-extensions --dir` pre-installs the `excel` extension, and ICU if it isn't bundled. At runtime, set `extension_directory`, set `autoinstall_known_extensions=false`, then `LOAD`. A missing extension gives a clear error naming the expected path.

---

## 5. App backend (`GalaxyData.Web`)

**Architecture**
- Minimal APIs, with one `MapXxx(RouteGroupBuilder)` per feature under `/api`.
- TypedResults, ProblemDetails, `AddValidation()` and OpenAPI. The OpenAPI document feeds `openapi-typescript` for client types.

| Error | Status |
|---|---|
| Syntax | 400 with index |
| Bind | 422 |
| Concurrency or stale plan | 409 |
| Source unreachable | 502, with no secrets |
| Timeout | 504 |

- Single instance only; document this.

**Metadata: EF Core 10 on SQLite, with migrations**
- WAL mode, UTC `DateTime`, and a `Version` concurrency token on admin entities.
- The DB is backed up before migrations run.
- Tables:
  - Users (role, PasswordHash, SecurityStamp, MustChangePassword, lockout, disabled);
  - Connections (Alias immutable, Kind, Mode Form|Raw, SettingsJson, ProtectedSecret, IsReadOnly, OptionsJson, SchemaStatus);
  - SchemaSnapshots (gzip JSON of `SourceSchema`, hash, diff, last 5 kept);
  - OverlayRelations, OverlayNavOverrides, VirtualEntities, EntitySettings;
  - SavedQueries (owner, shared);
  - ChangeSets and PendingChanges (per user);
  - CommitAudits and CommitAuditScripts;
  - AdminAuditEvents.

**Secrets:** ASP.NET Data Protection, with keys in `{DataDir}/keys` protected by DPAPI on Windows. Connection strings never appear in logs or error responses.

**Auth**
- **Cookie:** `gd.auth`, HttpOnly, SameSite=Strict, 8-hour sliding expiry. API calls get 401/403 instead of redirects.
- **Passwords:** `PasswordHasher<AppUser>`.
- **Session invalidation:** a security-stamp validator with a 30-second cache, so a reset, disable or demotion ends sessions.
- **Lockout:** a lockout policy plus login rate limiting.
- **Antiforgery:**
  - `GET /api/auth/session` issues an `XSRF-TOKEN` cookie.
  - An endpoint filter validates the `X-XSRF-TOKEN` header on mutating calls.
  - The token is reissued after login, logout and password change.
- **Policies:**
  - Fallback: authenticated with a current password. A pending password change returns 403 `password-change-required`.
  - `CanRead`, `CanEditData` (Admin and DataManager), `CanAdmin`.
  - Anonymous endpoints are marked explicitly: health, session and login.
  - Guards stop an admin demoting or disabling the last Admin, or demoting themselves.
- **Seeding:** `GalaxyData:Bootstrap:{AdminUserName, AdminPassword, RequirePasswordChange, ResetAdminPassword}`. If there are no users and no password is configured, startup fails. A reset sets MustChangePassword.

**Connections**
- **`IConnectionKind`** (app-side) provides a descriptor with fields (text, number, password, bool, select, filePath, folderPath, keyValues; groups; `visibleWhen`). It builds connection strings with the provider's `DbConnectionStringBuilder`, parses raw strings, and knows which keywords are secret.
- **Secrets on the wire:**
  - A GET returns secret fields as `{hasValue}`.
  - An update sends `keep`, `set` or `clear` per secret.
  - In raw mode, secrets are masked as `********` and merged back on save.
- **Endpoints and rules:**
  - `POST /test` opens the connection with a 10-second timeout and runs a probe.
  - Aliases must be bare identifiers that are neither keywords nor word operators (checked through `QueryText.IsBareIdentifier`).
  - `AllowedFileRoots` restricts file and folder paths.
  - Excel connections are always read-only.

**Schema refresh and catalog**
- A refresh is a `Channel`-queued `BackgroundService` job; the API returns 202 and the UI polls the status.
  - The worker introspects the source and hashes the result.
  - If the hash changed, it stores a snapshot and a diff.
  - It then invalidates the catalog and re-validates the overlay, marking broken items.
- `CatalogService` is a singleton that builds lazily and is versioned. After an invalidation, the next caller waits for the rebuild, so there are no stale reads.
  - It caches deserialized snapshots by hash.
  - It builds the engine `ICatalog`, the tree index and the search index.
  - Every response carries `X-Catalog-Version`.
- `EntityCapabilities` makes an entity editable only when it is a physical table with a PK or non-null unique key, on a connection that isn't read-only, for a user with `CanEditData`. Views and virtual entities are read-only; a declared key there enables navigation only.

| Column kind | Editability |
|---|---|
| PK | on insert only |
| identity, computed, rowversion | never |
| binary, xml, unknown | read-only in v1 |

**API surface** (all under `/api`)

| Area | Endpoints |
|---|---|
| Auth | session, login, logout, change-password |
| Users | CRUD, reset-password |
| Connections | connection-kinds, convert; connection CRUD, test, refresh; snapshots and diff |
| Catalog | tree/children, tree/search, entities/{name} |
| Browse | browse/page, browse/trail |
| Query | validate, explain, execute; saved-queries CRUD |
| Changes | GET, ops, DELETE (scoped), preview, commit |
| Overlay | relations, nav-overrides, virtual-entities (+ validate), entity-settings, issues |
| Audit | commits, admin-events |

**Browse (`POST /api/browse/page`)**
- **Request:** `source` is `{entity}` or `{from:{entity,key}, nav}`, and `grid` holds filters, a where expression, sort and paging, plus `includeSchema` and `includeCount`.
- **`GridQueryComposer`** builds query text with `$params`, validating and quoting column names through `QueryText`.
  - It covers every AG Grid filter operation (contains and starts-with are case-insensitive).
  - The user's where expression is checked with `TryParse`, and statement-type roots are rejected.
  - The PK is appended as a sort tiebreaker.
- **`NavigationResolver`** turns (from entity, key, nav) into a plain target-entity query, so the target stays editable.
- **`CountStrategy`** runs `ForCount()` in parallel, capped at 3 s. On timeout the response reports `total:null` with `hasMore`.
- **Response:**
  - `queryText` and `parameters`;
  - entity capabilities;
  - columns with the logical type, editable, insertMode, lineage and reference;
  - rows as positional arrays `{id, k, v, r}`, because column names can contain dots;
  - the total count.
- **Value encoding:** int64 and decimal travel as strings, and dates as ISO strings.

**Pending changes, preview and commit**
- Changes are stored on the server, one changeset per user, and survive refreshes, tabs and restarts.
- **`POST /changes/ops`** applies a batch atomically: set, insert (by tempId), delete, revert. `ChangeSetMerger` rules:
  - `original` is recorded on the first edit only;
  - setting a value back to its original drops that change;
  - delete on a pending insert removes the insert;
  - nav edits send the FK column values plus display hints.
- **Preview:**
  1. Validate against the current catalog.
  2. Build the engine `ChangeSet` and `DmlPlan`.
  3. Cache the plan under a `planId` for 30 minutes.
  4. Return per-connection scripts, a `multiConnection` warning, and blocking issues.
- **Commit** sends `{planId, changesetVersion, scripts}`.
  - An unedited script runs the parameterized statements with row-count checks.
  - An edited script is split and passed through the DML-only guard (Admins can override); it runs without row-count checks.
  - A CommitAudit row is written before execution starts. Any still `InProgress` at startup are marked `Unknown`.
  - Changes are cleared only on success. A stale plan returns 409.

---

## 6. Angular app (`src/client`)

Scaffold with `npx @angular/cli@latest new … --zoneless --style=scss --ssr=false`. Add Material and CDK (M3 theme), `ag-grid-community` and `ag-grid-angular` (register only the modules used), `monaco-editor` (AMD assets loaded by a loader service), and self-hosted fonts. Tests use Vitest. In development, a proxy sends `/api` to `dotnet watch`. For publish, an MSBuild target builds the client into `wwwroot`.

**Structure**
- `core/`: API clients, `AuthStore`, guards, interceptors (errors, catalog version), `CatalogStore`, `PendingChangesStore`, value and key codecs.
- `shell/`: the app shell.
- `features/`: auth, tree, browse (grid, renderers, editors, nav-picker, inspector/lineage, breadcrumb), changes (drawer, SQL preview dialog), query, editor (Monaco + `gdq` Monarch grammar), admin (users, connections with a dynamic form, overlay, audit).

**Routes**
- `/login` and `/change-password`.
- `/browse/**`, using a custom UrlMatcher.
- `/query` and `/query/:id`, lazy-loaded.
- `/changes`.
- `/admin/...`, lazy-loaded and role-guarded.

**URL state** (`browse-url.codec.ts`, the only place the grammar lives)
- The first path segment is the entity, and each later segment is a nav. Each segment carries its crumb state as matrix params: `f` filters, `sort`, `page`, `row` (the selected key, `~` for composite keys) and `w` (a where expression). `?at=` marks the active crumb.
- Example: `/browse/shop.customers;f=country:eq:ZA;row=42/orders;sort=-order_date;row=1001/lines?at=1`
- Grid state changes use `replaceUrl`; crumb navigation pushes a history entry.
- Selecting a different row on an earlier crumb truncates the forward crumbs.
- The codec has fast-check round-trip tests.

**Grid** (AG Grid Community)
- Infinite row model with pagination. `getRowId` returns the canonical key JSON.
- A priming request fetches the schema and count; the datasource then maps sort and filter models to GridState.
- The URL and the grid sync in both directions, with a guard flag against loops.
- Columns use `colId:'c'+i` with `valueGetter`/`valueSetter`. Editors and filters follow the logical type, and NULL is shown distinctly.
- **Dirty overlay:**
  - `valueGetter` reads `PendingChangesStore` first, then the server value.
  - Cell classes: `gd-dirty`, `gd-conflict`, `gd-invalid`. Row classes: `gd-deleted`, `gd-inserted`.
  - An `effect` refreshes only the affected cells and rows.
- **Inserts:** pending inserts appear as pinned-top rows, followed by a blank "new row" line.
- **Actions:** Community edition has no context menu, so there's an action column (select, delete toggle, revert, inspect), toolbar buttons and keyboard shortcuts.
- **Reference cells:**
  - Many-to-one: display value plus a link. Collection: "orders ›".
  - Clicking navigates by adding a crumb.
  - The editor opens `NavPickerDialog`, a read-only entity grid in picker mode. Confirming sets the FK columns, plus display hints.
- **Lineage:** a popover on the column header, and a cell inspector panel.

**Other features**
- **Tree:** a flat, virtual-scrolled tree (CDK virtual scroll plus `FocusKeyManager`) with lazy children, server search that expands ancestors, and badges.
- **Pending-changes drawer:** changes grouped by connection, entity and row, with revert per cell or row and a scoped clear.
- **SQL preview dialog:** a Monaco tab per connection, editable for Data Managers and Admins. Guard violations are shown inline. It warns when more than one connection is involved, shows per-connection results, and previews again automatically on 409.
- **Query editor:**
  - Run with Ctrl+Enter; Explain; Save / Save as; Share URL.
  - Tabs: Results (the grid in query mode), Plan (a tree with site badges), SQL (one tab per fragment plus the merge SQL), Messages.
  - Markers come from `/query/validate`.
- **Dynamic connection form:** typed `FormRecord` built from the descriptor, with `visibleWhen`, a secret keep/set/clear control, a raw-mode toggle, Test, and polling of the schema status.
- **Overlay admin:** relation editor (entity autocomplete plus column pickers), virtual-entity editor (Monaco, validate, preview), entity settings, and the issues list.

---

## 7. Milestones (each ends green and demonstrable)

**Engine**

| # | Scope | Exit criteria |
|---|---|---|
| M0 | Solution skeleton, parser move and fixes, spans, options, `QueryOperatorTable` | Parser tests: spans, operators, depth, the 4 examples as snapshot trees |
| M1 | Types, introspection DTOs, SQLite and DuckDB introspectors, `CatalogBuilder`, nav naming, overlay (no virtual entities) | Introspection JSON snapshots; nav-naming test table; ambiguity tests |
| M2 | Binder core: where/select/extend/orderBy/take/skip/distinct, scopes, lambdas, `$params`, lets, coercion, functions, many-to-one navs | Bound-tree and diagnostic snapshots; example 1 binds |
| M3 | Lowering, SQL AST, `SqlBuilder`, 4 dialects, single-site execution, Direct lineage, `gdq` CLI | Golden SQL per dialect (Verify); example 1 end-to-end on SQLite and DuckDB |
| M4 | Group scope and HAVING, joins, collection navs, any/all/in/subqueries, set ops, decorrelation, pushdown and prune rules | Examples 2–4 golden and end-to-end; per-rule plan snapshots |
| M5 | Links, hidden keys, EditTarget, RowIdentity, full lineage, `QueryText.Compose`, `ForCount`, explain model and renderer | Link and lineage snapshots; composed filter reaches the scan |
| M6 | Federation: SiteAssigner, fragmenter, DuckDB merge engine, full fetch | Differential suite: same data all-DuckDB, all-SQLite and split, identical results; spill test at 64 MB; cancellation test |
| M7 | Adaptive bind-join, runtime scalar params, TopN through navs, cardinality estimates | A 50-row page sends ≤ 50 keys (`ExecutionStats`); k = 0 early-out; fallback above the key limit |
| M8 | Excel Folder provider | OpenXml-generated fixtures (multiple, hidden and spaced sheets); Excel ⋈ SQLite join |
| M9 | PG and MSSQL providers | Container suite runs the same conformance and differential tests; MSSQL varchar parameter typing |
| M10 | DML planner, script splitter, DML-only guard, coordinated executor | Per provider: insert with returned rows, concurrency conflict rolls back all, cross-connection success, simulated partial commit, guard rejects DDL |
| M11 | Hardening, timeouts, guardrails, language reference doc (`docs/language.md`) | |

**App backend**

| # | Scope |
|---|---|
| B0 | Host, ProblemDetails, OpenAPI, health |
| B1 | EF metadata, migrations and backup, seeding, cookie auth, antiforgery, policies, users |
| B2 | Connections, kinds and descriptors, secrets and masking, test-connection |
| B3 | Snapshots, refresh worker, diff, CatalogService, tree, search, entity descriptors and capabilities |
| B4 | GridQueryComposer, codecs, NavigationResolver, CountStrategy, browse endpoints |
| B5 | Overlay CRUD and validation |
| B6 | Query validate, explain and execute; saved queries |
| B7 | ChangeSets, merger, preview, commit, audit |
| B8 | Security headers and CSP, rate limits, log redaction, publish target |

**Frontend**

| # | Scope |
|---|---|
| F0 | Workspace, theme, generated types, Vitest |
| F1 | Auth, guards, interceptors, shell |
| F2 | Admin users and connections (dynamic form) |
| F3 | Tree and search |
| F4 | Read-only browse: URL codec, AG adapter, datasource, URL sync, lineage inspector |
| F5 | Reference cells and breadcrumb |
| F6 | Pending changes: store, dirty overlay, editors, delete, insert rows, nav picker, drawer |
| F7 | SQL preview and commit dialog, audit pages |
| F8 | Query editor |
| F9 | Overlay admin |
| F10 | Playwright smoke test and polish |

Engine work starts at **M0**. The first commit goes on a new branch off `master`, since the repo has no commits yet and its main branch is `main`.

---

## 8. Verification

- **Engine:**
  - `dotnet test tests/GalaxyData.Query.Tests tests/GalaxyData.Query.IntegrationTests` covers golden SQL for all 4 dialects, binder and optimizer snapshots, the SQLite+DuckDB differential federation suite, Excel, and DML.
  - `dotnet test tests/GalaxyData.Query.ContainerTests` needs Docker. It skips itself when Docker isn't running.
- **CLI smoke test:** `gdq repl --source shop=sqlite:tests/fixtures/shop.db --source wh=duckdb:... --overlay overlay.json`, then run the four spec examples plus a cross-source query with `:explain`. Check the fragments, the merge SQL and the lineage output.
- **Backend:** `dotnet test tests/GalaxyData.Web.Tests`. A `WebApplicationFactory` points at a temp data directory with bootstrap credentials and SQLite/DuckDB/Excel fixture sources, and a `TestApi` helper handles the XSRF token. The suite includes:
  - a test that every `/api` endpoint has a policy;
  - a test that no response body contains the fixture password;
  - role-matrix tests;
  - commit tests: concurrency conflict, multi-connection rollback, and the edited-script guard.
- **Frontend:** `npm test` (Vitest) covers the URL codec round trips, the AG filter adapter and `PendingChangesStore`.
- **End to end:** Playwright runs against the published app:
  1. log in and change the password;
  2. add a SQLite connection and wait for Ready;
  3. search the tree and open orders;
  4. filter, sort and page, then reload and check the state is restored;
  5. click the customer reference, go back, then forward;
  6. edit a cell, insert a row and delete a row;
  7. preview, commit, reload and check the values persisted;
  8. run and explain a query.

---

## 9. Key risks and mitigations

- **Results depending on where an operator runs** (collation, nulls, division, dates, SQLite LIKE): the semantic contract, per-dialect emulation and differential tests. Collation is the documented exception.
- **SQLite dates:** a per-source date storage setting (ISO text by default) and format-aware parameters.
- **DuckDB memory and spill:** confirm in M6 that in-memory tables spill. If they don't, switch the merge database to a temp file (a single option).
- **The DuckDB `excel`/ICU extensions offline:** ship an extension directory with the app, and give clear errors.
- **Group-key matching complexity:** a large table of positive and negative binder tests.
- **Reserved words** (`and`, `or`, `not`, `in`, `if`, `for`): escape with `it["in"]`. The app quotes names through `QueryText`.
- **AG Grid Community's infinite model with pagination when the total is unknown:** spike this early in F4. The fallback is `MatPaginator` driving the datasource; the URL format doesn't change.
- **Partial commits across connections:** the preview warning, audit statuses and a per-connection result view.
- **Losing Data Protection keys loses stored secrets:** document that the keys directory must be backed up. The UI shows "re-enter secret" for affected connections.
