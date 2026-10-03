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

**Environment:** .NET SDK 10.0.103, Node 24, and Docker in WSL (Alpine; Docker Desktop on Windows doesn't work here), where `servers.sh` runs the Postgres and SQL Server the container tests use. There is no global Angular CLI, so we use `npx @angular/cli`. Code style follows the parser: 3-space indents, Allman braces, file-scoped namespaces.

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
  GalaxyData.Query.Excel/       Excel Folder provider (on .DuckDb; no Excel library, no DuckDB extension)
  GalaxyData.Query.Cli/         `gdq` CLI/REPL (System.CommandLine; plain-text tables, no Spectre.Console)
  GalaxyData.Web/               ASP.NET Core host, feature folders, serves Angular from wwwroot
  client/                       Angular workspace (builds into GalaxyData.Web/wwwroot)
tests/
  GalaxyData.Common.Tests/  GalaxyData.Query.Tests/ (no DBs: binder, rules, golden SQL)
  GalaxyData.Query.IntegrationTests/ (SQLite+DuckDB+Excel, federation, differential)
  GalaxyData.Query.ContainerTests/ (PG+MSSQL from servers.sh or GDQ_TEST_*, same conformance suite)
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
| Verify.XunitV3 | not used: golden files go through the in-house `Golden` helper |
| Shouldly | current in cache |
| Testcontainers | not used: nothing on Windows reaches the Docker in WSL without exposing its socket; `servers.sh` starts the servers |
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
2. the implicit row: `it`, `outer`/`inner` inside join arguments, `key` in group scope, then the row's columns, navs and record members (a record's own columns are reached through it: `o.status` after a join);
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
- Also left to the database (tested, see 4.10): the digits of decimal division and averages, `toString` of date-times, times and doubles, division by zero (the servers fail, SQLite gives null, DuckDB infinity), `char(n)` padding, characters outside the BMP (SQL Server counts two without an `_SC` collation), and text that doesn't convert in `toInt`, `toLong`, `toDouble`, `toDecimal` and `toBool` (SQLite gives 0 or null, the others fail the query). See `docs/language.md` 9.3.

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
| `RowLink(target, targetColumns, keyOrdinals, navigation)` | FK columns, and nav-valued items (`select(customer)`, shown by the display column) with hidden target-key columns |
| `CollectionLink(navigation, valueOrdinals)` | aggregates and `any()` of a bare collection nav (`orders.count()`), and `RowIdentity.Related` |
| `DrillDownLink(queryText, keys)` | groupBy aggregates: the query before groupBy (named subtrees included), filtered on each key part as written (lambda keys keep their parameter) |
| `EditTarget(entity, column, keyOrdinals)` | table columns (not views) whose row's key is in the result |

  Every link has `Query(row, parameters)`, which composes the query for the rows it leads to (key values as `$key1`… parameters; a null group key becomes `== null`; a null foreign key leads nowhere). `ResultSchema.RowIdentity` exposes the key and the inverse navs for rows that belong to a single entity. Hidden key columns are added only along row-preserving paths: each scanned column carries its scan's row origin through renaming projections, joins, navigations and group keys; distinct, set operations and aggregates drop it, and never see hidden columns, so these can't change results.

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
4. **Site assignment and fragments** (`Planning/Federation/FederationPlanner`), top-down from the root:
   - A subtree becomes a fragment when it reads exactly one source, reads no columns from outside it (no correlation), its order can be restated over its own columns, and `SqlBuilder` can write it in the source's dialect (the builder is the translatability oracle; an optional capability hook can keep parts from a source that lacks a function its dialect has). An Excel folder is a source like the others, in DuckDB's dialect; its tables are in the merge engine's database, so its fragments are copied into their merge tables there (`INSERT … SELECT`), never crossing into .NET (see 4.9).
   - Otherwise the operator runs in `merge`, and its inputs and subquery plans are cut the same way; correlated subqueries run in the merge engine over their uncorrelated fragments.
   - At a boundary, the conjuncts of a filter the source can check stay in the fragment and the rest go to merge.
   - An ordered fragment (a sort, or a top-N) is sorted again in the merge engine, since loading rows into a table loses their order; its SQL keeps ORDER BY only under a limit.
   - Each fragment selects only the columns the merge SQL reads (recorded while the merge SQL is written); a placeholder column when it reads none.
   - If the whole plan is on one site, the fast path skips DuckDB entirely. A one-source query whose SQL can't be written for its source runs through the merge engine instead, when there is one.
   - `QueryEngineOptions.PushDown = false` fetches bare scans and runs everything else in the merge engine, to compare.
5. **Cross-source joins:** an **adaptive bind-join**. The planner marks the fragment on the side of a join whose rows only matter when they match (the right of a left, semi or anti join; the larger side of an inner join; joins in subqueries too), when the key comes straight from a fragment's column on each side (through filters, projections, sorts and DISTINCT on the target side). At run time the driver fragment loads first, and its distinct keys are read from its merge table (converted to the target key's type):

| Key count k | Action |
|---|---|
| 0 | skip the target: no connection is opened |
| ≤ `MaxBindKeys` (10k), and (adaptive) fewer than the target's estimated rows | fetch the target in batched IN-lists (`MaxBindBatch`, 2,000, and the dialect's parameter limit: MSSQL 2100; whole-number keys are written into the SQL) |
| more | full fetch |

   Keys must compare exactly in the target's source (`SqlDialect.ComparesExactly`): SQLite targets are only looked up by integer and text keys, since it keeps dates (with a time or a 'T'), guids (in either case) and decimals (unrounded) in forms that read as values they don't equal. Text keys go in one statement or the fragment is fetched in full: a collation that ignores case or trailing spaces could find a row for keys in two batches. Whole decimals are bound to SQLite as integers.
   `BindJoins = Always` binds whatever the estimates, and `Never` fetches in full, to compare. Fragments are scheduled by dependency (values first, drivers before the fragments bound to them), a few at a time.
   Uncorrelated scalar subqueries of one source, in an operator that reads others, run first and become runtime parameters (`ParameterSource.Runtime`, `s1`, `s2`, …), so the operator may run in a source itself. The source then compares the value as it would the same value written in the query (a SQLite date held with a time doesn't equal the date, as in a query of SQLite alone). Filters with uncorrelated subqueries now move into join sides too.
6. **Cardinality** (`Planning/Cardinality`): introspected sizes, or 10k when there are none; filters keep a share by kind of condition (one row for the key equal to a value); joins along a navigation (or to a grouped side on its keys) keep their left rows; limits cap. Explain shows the estimates that rest on reported sizes, and warns about full fetches estimated above 1M rows.
7. **Top-N** (optimizer): a limit, with the sort that picks its rows, moves below projections and below joins that give each left row once (left joins along a navigation or to a grouped side on its keys; inner joins along a navigation every row has). A page of 50 orders is picked in its source before the customers are joined, and across sources only its customers' keys are looked up. The limit goes down a chain of such joins at once, and a cap stays above them, in case a right side has a key twice after all (a declared key that isn't one, or group keys a source tells apart that the merge engine doesn't). Virtual entities whose rows may repeat (`concat`, `selectMany` of anything but a navigation) don't inherit their base entity's key.

### 4.5 SQL generation (`Sql/`)
- **SQL AST:** Select, SetOperation, Join, DerivedTable, and Insert/Update/Delete with Returning.
- **`SqlBuilder`** fills clauses in order and wraps the current select as a derived table when chain order needs it (`take(10).where(…)`). Semi and Anti joins are written as `[NOT] EXISTS`. Table aliases are readable (`o`, `c`, `c2`).
- **`SqlDialect` hooks:** identifier quoting, parameter prefix, paging, boolean handling, string concatenation, LIKE escaping, date functions, integer division, null ordering, `MaxParameters`, and function templates. A missing template means the function isn't translatable.

| Hook | PG | MSSQL | SQLite | DuckDB |
|---|---|---|---|---|
| Paging | LIMIT/OFFSET | TOP, or OFFSET/FETCH | LIMIT/OFFSET | LIMIT/OFFSET |
| Booleans | native | `bit` | 0/1 | native |

  MSSQL parameters are typed from the column's `ScalarType`. Only NULL, booleans and limit integers are inlined; all other values are parameters. `FormatLiteralForDisplay` is used for explain and preview. A parameter compared with a table's column is marked so, with the column's declared type (`SqlParameterSlot.ComparedWithColumn`, `ColumnType`), and providers bind from the slot (`SourceProvider.BindParameter(parameter, value, slot)`), see 4.10.

### 4.6 Execution
- **API:**
  - `QueryEngine(ICatalog, ISourceRegistry, IMergeEngine, options)`.
  - `.Prepare(QueryRequest{Text, Parameters, Paging})` is pure and returns a `PreparedQuery`, which offers `Schema`, `Explain(verbose)`, `ExecuteAsync(ct)`, and `ForCount()` (no final sort or paging; nav joins, unused group joins and unused dependent group keys eliminated; a `take` in the query still counts).
  - `QueryResult` is an async row stream with `ResultSchema`, `ExecutionStats` and `Warnings`.
- **Providers:** `ISourceProvider` has Dialect, Affinity, Introspector, TypeMapper, BindParameters and typed `ColumnReader`s. The app implements `IConnectionFactory.OpenAsync(alias)`, so the library never holds secrets.
- **`QueryText` helpers:**
  - `QuoteName` and `IsBareIdentifier`, following the engine's operator table.
  - `Compose(text, filters, sort, tiebreak)`, which wraps the last statement as `(<last>).where(..).orderBy(..)` using `SplitStatements`.
  - With paging, the sort is stabilized by appending the row-identity key (inside the query's own sort, under its filters and `take`).
- **Merge engine:** `IMergeEngine` (core) → `IMergeSession` → `IMergeTableWriter`; `DuckDbMergeEngine` is one process-wide DuckDB instance, in memory unless `DatabasePath` is set.
  - It is configured with `memory_limit`, `temp_directory` (by default a directory of its own under the system temp directory, removed on dispose) and `threads`. In-memory tables spill there (confirmed in M6: over 64 MB of temp files with a 64 MB limit).
  - Each query gets its own schema `q_<n>`, and the session connection's `search_path` finds the fragment tables (`f1`, `f2`, …) by name; dropped when the result is disposed.
  - Fragments load in parallel (`MaxParallelFetches`, 4 by default) on `Duplicate()` connections, through typed appenders; values cross as their logical CLR types. The first failure stops the others and is the error reported.
  - Fragment reads from DuckDB sources and the final query run in streaming mode.
  - Cancellation: the token stops fetches between rows, and a registration calls `DbCommand.Cancel()` (DuckDB interrupt, `sqlite3_interrupt`) for statements that are busy, including while a result is read. `MaxFetchedRows` caps what one query fetches; timeouts are in 4.11.
  - SQLite's dynamic typing is handled by `ValueConverter`: a failure is an error with source, row and column context, or null in lenient mode.
  - How values are held in the merge engine: every value is checked against its column before a row is appended (a partly appended row must be cleared, or DuckDB crashes). Decimals are `DECIMAL(38, scale)`, since SQLite doesn't hold values to their declared precision; decimals of no declared precision (SQLite reals, averages) are doubles (about 15 significant digits). Date-times are `TIMESTAMP`, to the microsecond (as PostgreSQL and DuckDB keep them) over every year .NET has: `TIMESTAMP_NS` (M6 to M8) reached only 1677 to 2262, and wrapped SQL Server's common 9999-12-31 around; date-times with offsets are UTC instants, as DuckDB sources give them. Values of unknown types are text to the merge SQL (so `toString(x)` works there), and result columns of unknown types give back the source's values.
  - `ExecutionStats.Fragments` gives each fragment's rows and time; a warning (GDQ3101) marks fragments expected to fetch more than `LargeFetchRows` (1M).

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
Built in M10.
- **Input:** a `ChangeSet` of `InsertRow(entity, values)`, `UpdateRow(entity, key, values) { Original }` and `DeleteRow(entity, key) { Original }`. Values are by column name: CLR values of the column's type, or text that converts as a database's value is read (`"2026-03-01"` for a date). `ResultRowEditor` makes them from a query's rows: `Update(schema, row, values by ordinal)` gives one update for each table row the edited values came from (through their `EditTarget`s; the values read are the originals), and `Delete(schema, row)` the delete of the row's `RowIdentity`.
- **Planning** (`DmlPlanner.Plan`, `QueryEngine.PlanChanges`) runs nothing: it checks each change and writes one `DmlScript` per source. What can't be done is a `DmlIssue` (change index, column, message), and a plan with issues doesn't run (its other statements are planned all the same, to show).
  - Only tables (not views or virtual entities) in sources that are writable and take changes. Updates and deletes need the whole primary key. A key the overlay declares serves navigation only, since it needn't be unique (`TableEntity.IsWritable`). Inserts into tables without a primary key are fine.
  - Values convert to the column's type without loss. These are issues: a date with a time of day, a decimal with more places than its scale or too large for its precision, text longer than the column as the database counts it (SQL Server's nvarchar in UTF-16 units), a whole number out of range, and a null for a non-null column. Date-times keep their offsets.
  - Inserts need a value for each non-null column without a default. Computed and row version columns take none, nor do identity columns on SQL Server (the others accept one: SQLite's rowid, PostgreSQL's `BY DEFAULT` and serial, DuckDB's sequences). Updates can't change key, identity, computed or row version columns. Columns of unknown types aren't written.
- **Statements:**
  - Inserts give the row back, every column as queries read it: `RETURNING` (PostgreSQL, SQLite, DuckDB), `OUTPUT INSERTED.…` (SQL Server). A SQL Server table with triggers takes no `OUTPUT`, so the row is read back by its key: `WHERE @@ROWCOUNT = 1 AND id = SCOPE_IDENTITY()`, or the key values given.
  - Updates set the columns given. Updates and deletes find the row by its key (`SqlDialect.KeyEquals`: SQLite compares guid, date and date-time keys through `upper()`, `date()` and `strftime()`, as it may keep them in other forms). They change it only if the original values given still hold: its row version alone when that is given (SQL Server's `rowversion`); otherwise each original whose type compares exactly in the database (`SqlDialect.ComparesOriginal`: not floating-point, binary, JSON or unknown types, nor SQL Server's `text`, `ntext` and `image`; in SQLite only whole numbers and text), and `IS NULL` for nulls. PostgreSQL's `xmin` isn't used, as the language can't read it.
  - Parameters are typed by their column and marked as meeting it, so PostgreSQL takes text and JSON as the column's type (enums, char(n), json and jsonb), and SQL Server sends `datetime` for its datetime columns.
  - Each statement must change one row. SQL Server's count is `SELECT @@ROWCOUNT` after the statement (the count it reports adds the rows its triggers changed, and NOCOUNT hides it); the others report the statement's own.
  - Each script runs inserts first, with the tables they refer to first, then updates, then deletes, with the tables that refer to theirs first. Tables that refer to each other, and rows of one table, keep the order given.
  - `ToDisplayText(inlineParameters)` gives the statements as people read them, without the rows they give back. With the values written in (`SqlDialect.WriteLiteral`, which knows the column's type: a SQL Server literal for a `datetime` column is a `datetime`), the text can be edited and run as a script.
- **Running** (`QueryEngine.CommitAsync(plan or scripts)`, `DmlExecutor`):
  - Every connection is opened and readied (`SourceProvider.PrepareWriteAsync`: SQLite's `PRAGMA foreign_keys` as `SourceInfo.EnforceForeignKeys` says, which is on, off, or as the connection has it, on with the SQLite that Microsoft.Data.Sqlite bundles). A transaction is begun on each (SQLite's is IMMEDIATE), and the scripts run in order.
  - A statement that fails, or changes another number of rows than one, rolls every connection back.
  - Each connection then checks what its commit would (`SourceProvider.PrepareCommitAsync`: PostgreSQL's deferred constraints, `SET CONSTRAINTS ALL IMMEDIATE`), so such a failure also comes before anything commits. Then they commit in order.
  - There is no two-phase commit: a commit that fails after another succeeded leaves the changes partly written (`DmlOutcome.PartiallyCommitted`), and the rest are rolled back.
  - Failures aren't thrown. The `DmlResult` gives the outcome, each script's status (committed, rolled back, commit failed), each statement's rows changed and returned row, and the failure (connection, statement, conflict or commit) with its message.
  - Cancelling before the commits rolls everything back; commits aren't cancelled. `DmlPlan.IsMultiConnection` warns when a change set spans connections.
- **Edited scripts** (`QueryEngine.ParseScript(source, text, allowAnyStatement)`):
  - `SqlScriptSplitter` splits at `;` outside strings, quoted names and comments, as each dialect writes them: PostgreSQL's and DuckDB's `E'…'` and `$tag$…$tag$`, SQL Server's and SQLite's `[…]`, SQLite's backticks, nested block comments (but SQLite's), and SQL Server's `GO` lines. A SQLite `CREATE TRIGGER` keeps its body; other procedural blocks are split. What isn't closed, and `GO n`, are problems.
  - `DmlGuard` lets only `INSERT`, `UPDATE`, `DELETE` and `MERGE` run (and SQLite's `REPLACE`), `WITH …` forms of those included, by the word after the common table expressions. In SQL Server, where statements needn't end with `;`, reserved words that start other statements or run code (`DROP`, `EXEC`, `DECLARE`, …) and `SELECT … INTO` are rejected anywhere in a statement. Functions that reach outside the database are rejected too: DuckDB's `read_*`, PostgreSQL's file and server functions, SQLite's `load_extension`.
  - An administrator may allow any statement, but never transaction control, which is the engine's. The guard isn't a sandbox; the login's rights are.
  - Edited statements don't check how many rows they change. Problems give their line and position.
- **Found by the review** (222 probes on the four databases, now `DmlEdgeTests`), fixed:
  - a key the overlay declares made a keyless table's rows changeable;
  - text was measured in characters, where SQL Server's nvarchar counts UTF-16 units;
  - an edited SQL Server statement with `OUTPUT` counted its first value as its rows.
- **`gdq`:** `changes -f changes.json [--commit]` shows the scripts of a JSON change file, and writes them. `script <alias> -f edits.sql [--commit] [--any-statement]` checks an edited script, and runs it. `-w <alias>` makes a source writable (its files open read-write).
- **Known limitations:**
  - DuckDB can't update an indexed column of a row that a foreign key refers to: it updates by deleting and inserting (DuckDB's own limitation), so such changes fail with its error.
  - A row inserted can't be referred to by another inserted in the same change set when its key is generated.
  - SQL Server identity values can't be given (there is no IDENTITY_INSERT).
  - Binary values' lengths aren't checked before they run; the database rejects what doesn't fit.

### 4.9 Excel Folder (`GalaxyData.Query.Excel`)
Built in M8 without DuckDB's `excel` extension (it would be a 22 MB download per DuckDB version, to ship for offline use, and `read_xlsx` types a column by its first row, failing later rows that differ). The cells are read here, and the rows loaded into DuckDB through appenders.
- **Reading with no Excel library** (`Workbook`): `ZipArchive` and `XmlReader` (DTDs prohibited), a row at a time.
  - The package's rels give the workbook part, and its rels the worksheets, shared strings and styles; chart, dialog and macro sheets have no cells, and hidden sheets are left out unless `IncludeHiddenSheets`.
  - Cells: shared strings (rich-text runs joined, phonetic hints left out, `_xHHHH_` unescaped), inline strings, formula results (the cached value), booleans, errors (no value), ISO dates (`t="d"`), and numbers; a number whose format shows a date or time (built-in ids, or custom codes: `y`/`d` dates, `h`/`s` times, `m` months unless with times; `[h]:mm` durations are numbers) is a date, date-time or time of day. The 1900 system's fictitious 1900-02-29 and the 1904 system are handled. An empty string, or one of spaces alone, is no value; so is an error.
  - The folder's own `*.xlsx` files, without `~$` lock files, `._` files and hidden files; a file that can't be read (not a zip, encrypted, damaged, a sheet part missing) is left out with a warning (`SourceSchema.Warnings`, new), as is a workbook named `information_schema` or `pg_catalog` (DuckDB has those schemas in every database). Part names are found as their targets are written or unescaped.
- **Sheets as tables** (`SheetLayout`): the first row with a value names the columns (`HeaderRow = false`: letters); a blank heading (or an error), and a column with values past the headings, is named by its letter; a heading that repeats one before it in any case gets `_2`, `_3`, whichever no heading has. Rows without a value (errors alone) are left out. A column's type is the one all its values share: whole numbers (to 2^53) int64, other numbers double, dates date, dates with date-times datetime, times time, booleans boolean; any mix, or text, is text (numbers as they read, dates in ISO form, TRUE/FALSE). `AllText` reads every column as text. Values convert to a column's type (after an overlay's) as the merge engine's would: decimals rounded to their scale (away from zero), numbers read as dates as serial dates of the workbook's date system, and text read as numbers, dates and times only in invariant (ISO) form, in UTC unless it has an offset.
- **Names:** each workbook is a schema, named by its file without `.xlsx`, each sheet a table: `xl["Budget 2024"]["Sheet 1"]`. No default schema, no keys (the overlay may declare them, and relations to and from them).
- **Kept in the merge engine** (`ExcelSourceProvider(DuckDbMergeEngine)`, `AddFolder(alias, options)`, `Source(alias)`, `IntrospectAsync(alias)`): each folder is a catalog of the merge engine's database, `ATTACH ':memory:' AS excel_<alias>`, and its SQL names tables in full (`SourceInfo.Catalog`, new). The provider opens its own connections (`SourceProvider.OpenConnectionAsync`, new; the application's factory isn't asked), and before a statement reads a sheet, `SourceProvider.PrepareReadAsync` (new) loads it when it isn't loaded, or its workbook's time or size changed, or the catalog's columns did (an overlay type, a refreshed schema): two passes over the sheet (layout, then values converted to the catalog's types) into a table of its own, swapped in (`DROP`/`RENAME` in one transaction), so queries reading the old rows go on seeing them. A value that doesn't convert fails the query naming its cell, or is null with `LenientConversion` (a sheet loaded with such nulls is loaded again for a strict query); a column, sheet or workbook that is gone says to refresh the schema (a missing workbook is looked for again a moment later, as Excel saves by renaming). Two catalogs that type a sheet's columns differently load it in turn, each time the other was last. Catalog names are unique ignoring case (`excel_xl`, `excel_xl_2` for `XL`), as DuckDB's are. Introspection reads every row (for the types) and loads nothing; it drops loaded tables that aren't in the schema any more.
- **Queries:** a query of one folder runs in its catalog as one statement. Merge tables are loaded on connections in UTC, like every other. DuckDB SQL casts a date-time compared with a date-time with offset to one (as UTC): a `TIMESTAMP_NS` (which the merge engine held until M9) doesn't compare with `TIMESTAMPTZ` otherwise. Across sources, a fragment of a folder is loaded by the merge engine's own `INSERT INTO f1 … SELECT` (`IMergeTableWriter.AppendQueryAsync`, new; `QueryFragment.InMergeEngine`; explain: "copied into f1 inside the merge engine"); bind joins and runtime values work as for any source. With a merge engine other than the provider's, its rows are fetched as a database's are.
- **Read-only** (`IsReadOnly`, no DML). `gdq`: `-s xl=excel:<folder>`.

### 4.10 Server sources (`GalaxyData.Query.PostgreSql`, `GalaxyData.Query.SqlServer`)
Built in M9 on Npgsql and Microsoft.Data.SqlClient; the dialects were M3's, first run against servers here.
- **PostgreSQL** (12 or later): introspection reads `pg_catalog`: tables, partitioned tables (partitions through their parent), views, materialized views and foreign tables the login may read, in every schema but the system ones; a domain maps as the type it is based on (through domains over domains) and an enum as text; arrays, composites, ranges, `money` (which compares with no number), `time with time zone`, network, geometric and `xml` types are unknown. Enums, and columns of unknown types, are read as text (`ColumnSchema.ReadAs`, new: the SQL reads `CAST(c AS text)`), so text functions, comparisons with text and bind joins by their keys work (an enum compared with a value that isn't a label finds nothing), and Npgsql reads every type (composites too); enums then sort by label, and their indexes aren't used. `NOT VALID` foreign keys aren't enforced; row counts are the planner's (`reltuples`, unknown until analyzed); a column's collation is given when it isn't its type's. Connections work in UTC (`SET TIME ZONE 'UTC'`). Parameters are typed (`NpgsqlDbType`, so nulls have a type, text when the value has none; date-times as `timestamp`, offsets as `timestamptz` in UTC), but text compared with a column is sent untyped, so PostgreSQL takes the column's type: a `char(n)` (compared padded) or `citext` (compared ignoring case). Doubles round as numeric (halves away from zero; `round(double)` rounds them to even), and booleans convert to numbers through integer. Npgsql reads enums, ranges and multiranges only from a data source with unmapped types enabled: `PostgreSqlSourceProvider.CreateDataSource` makes one (the CLI's and the tests' connections come from it; the app's will). An interval with months counts them as 30 days; a numeric a decimal can't hold (NaN, infinity, beyond 28 digits) fails as a value that doesn't convert, naming its row and column (null when lenient). Names are cut to 63 bytes, where PostgreSQL would cut them and make two long ones one.
- **SQL Server** (2016 or later): introspection reads `sys.*`: user tables and views in every schema; an alias type maps as its system type (declared as the alias); hidden columns are left out; foreign keys disabled or not trusted (`WITH NOCHECK`) aren't enforced; row counts are the partitions'; a column's collation is given when it isn't the database's. `tinyint` is int16, `money` decimal(19,4), `rowversion` binary(8) (`IsRowVersion`); `xml`, `sql_variant` and the CLR types are unknown, and CLR values (`hierarchyid`, `geometry`, `geography`) are read as bytes, as SqlClient can't read them without Microsoft.SqlServer.Types. Parameters have the types of the columns they meet, so SQL Server doesn't convert the column (and scan): text as `varchar(n)` or `nvarchar(n)` with the column's length (never shorter than the value, which SqlClient would cut; 8000/4000 or `max` when unbounded); decimal `$params` take the column's precision and scale (binder), widened when a value needs more; date-times as `datetime2`, or `datetime` when compared with a `datetime` column (SQL Server compares the two in 1/300 seconds, which few `datetime2` values equal). Text that isn't ASCII is sent as `nvarchar` even to a `varchar` column (SqlClient would send what the code page holds: `'a?b'` for `'a日b'`, which matched); `sysname` and alias types read as their system types; disabled indexes (and the keys they back) are left out. A `datetime` column compared with a date-time worked out in the query is compared as a `datetime`, and a column read through a derived table keeps its declared type. Counts are `COUNT_BIG` (COUNT is an int). The parts of a `datetimeoffset` (year() … second(), date(), toDate(), toDateTime(), daysBetween(), addDays(), addMonths()) are its UTC time's (`SWITCHOFFSET`), as in the databases that work in UTC. `length()` counts trailing spaces and text past 4,000 characters (`DATALENGTH` of it as `nvarchar(max)`, halved); startsWith and endsWith of a column's text count its trailing spaces; indexOf and contains find empty text at 1; a like() pattern's `[` is escaped (SQL Server reads a class); toBool of text reads true/t/yes/y/1 and false/f/no/n/0; toString of a guid is lower case and of a decimal (money too) has its scale. Names are cut to 128 characters. SQL Server takes at most 2,100 parameters: text keys of a bind join go in one statement or the fragment is fetched in full; whole-number keys are written into the SQL; a statement with more values than the database takes parameters has its constants written into the SQL.
- **Found running the conformance set on the servers, fixed for every dialect:** an aggregate whose argument reads the outer row in a correlated subquery (`orders.sum(credit_limit)`) is computed over a derived table first (SQL counts an aggregate of outer columns alone as the outer query's; SQL Server rejects one mixing them). Date-times with an offset are instants in UTC wherever they are read (`ValueConverter`), and SQLite compares and sorts them in UTC (`strftime`), as text they compare in their own time; the shop fixtures' orders have offset date-times now (`placed_at`), in three offsets, and the conformance set compares, sorts and takes their parts.
- **Found by the review** (321 probes against both servers, now `ServerEdgeTests`), fixed beyond the providers: the merge engine's date-times (see 4.6); a value the provider can't read (a PostgreSQL NaN, a time of 24:00) fails as a value that doesn't convert, naming its row and column, or is null when lenient (`QueryResult.TryRead`); a whole-number `$param` of any width converts (an int for a bigint column failed); SQLite's `like()` of a pattern that is a value is a GLOB (case-sensitive, as elsewhere; `PatternShape.AsWritten`), and its addMonths keeps to the month's end ('floor', SQLite 3.46); a connection string gdq's providers reject is the user's mistake (exit 2).
- **Collation:** the conformance runs on case-sensitive databases (C, Latin1_General_100_CS_AS), as SQLite compares; SQL Server databases usually ignore case, and queries that run there do too (the documented exception); ilike and icontains are the same everywhere.
- **Tests** (`tests/GalaxyData.Query.ContainerTests`): `servers.sh up` starts PostgreSQL (port 55432) and SQL Server Developer (51433) in Docker; `GDQ_TEST_POSTGRES` and `GDQ_TEST_SQLSERVER` name other servers (logins that may create databases). Each fixture database is made once per run under a name of its own and dropped after. Tests of a server that can't be reached are skipped (failed when it was named). The conformance set on each server, in the merge engine, split across the two servers (every bind-join mode, both ways) and with SQLite and DuckDB; introspection snapshots; a value of each type read directly and through the merge engine and sent back as a parameter; SQL Server's parameter declarations in its plan cache (no `CONVERT_IMPLICIT`); bind joins at the parameter limit; a case-insensitive database; cancelling a statement stops it on the server; the CLI.
- **Documented differences** (tested as what each gives): decimal division and averages have each database's digits (PostgreSQL 20 places, SQL Server 6 to 10, DuckDB a double's); toString of date-times, times, offsets and doubles is each database's text; division by zero fails on the servers (SQLite gives null, the merge engine infinity); PostgreSQL's text functions ignore char(n) padding, which the value read keeps; SQL Server counts a character outside the BMP as two without an `_SC` collation; `tinyint + tinyint` overflows past 255 in SQL Server (the language's int16 would not); the merge engine keeps date-times to the microsecond (datetime2(7)'s last digit goes).
- **Known limitations:** SQLite groups and takes min/max of offset date-times by their text.

### 4.11 Hardening (M11)
- **Timeouts:** `QueryEngineOptions.Timeout`, or a request's own `QueryRequest.Timeout`, bounds a query from its start until its last row is read: fetches, merging and reading all count. Counts (`ForCount`) keep to the request's timeout.
  - When the time is up, what is running is stopped as cancelling stops it, and the query fails with `QueryTimeoutException`, a `QueryExecutionException` (the backend maps it to 504). A caller's own cancellation is still `OperationCanceledException`.
  - Changes keep to the same timeout until they commit: past it they are rolled back, with `DmlFailureKind.Timeout`.
  - `CommandTimeout` is passed on as it was, but SQLite takes it as how long to wait for a lock, and DuckDB ignores it; only `Timeout` bounds every database.
- **Stopping statements:** `SourceProvider.CancelCommand` stops a busy command, around executing as well as reading (single-source queries, fragments, values, the merge query, changes).
  - Microsoft.Data.Sqlite's `Cancel` does nothing, and it checks a token only before a statement starts, so a busy SQLite statement ran to its end (found in M11). Its provider interrupts the connection instead (`sqlite3_interrupt`).
- **Excel sheets held until read:** `SourceProvider.PrepareReadAsync` gives a lease, which the engine disposes once the statement has started (for a fragment, once it is fetched). An Excel folder holds each sheet as loaded until then; a load for a catalog that types the sheet differently waits for the leases to go, and sheets are leased in name order, so two queries never wait for each other.
  - Found in M11: such a load could land between one query readying the sheet and its statement starting, which then read the column as the other catalog types it. A test of it had passed by timing, and failed on most runs once M11 changed what happens as a statement is readied and started.
- **Guardrails:**
  - Query text is at most `MaxQueryLength` characters (100,000; GDQ1002).
  - The parser nests at most 256 levels, and each operator in a chain is a level of its own.
  - A plan makes at most 100,000 columns (GDQ3005). Named subtrees and virtual entities are planned afresh at each use, so before M11 eighteen names, each using the one before twice, made 108 MB of SQL in 7.5 s, and a few more would have used up memory.
  - `MaxFetchedRows` caps the rows a query fetches into the merge engine, copies inside it included.
- **DuckDB offline:** the merge engine downloads no extension unless `DuckDbMergeOptions.DownloadExtensions` is set; `ExtensionDirectory` says where DuckDB looks for installed ones. Both take effect from the database's start (connection string), as DuckDB loads extensions then.
  - ICU, for time zones, is built into DuckDB.NET's native library (1.5.5 reports it statically linked); the copy in `~/.duckdb` was downloaded by an earlier version.
  - The DuckDB provider sets the time zone to UTC only when ICU is loaded or installed. Otherwise setting one would have DuckDB download ICU, or wait for the network to say it can't, and DuckDB works in UTC anyway.
- **Tests:**
  - **Timeouts:** in SQLite, DuckDB, the merge engine, while reading, for requests and counts, for changes, on both servers, and in the CLI (`--timeout`).
  - **Fuzzing:** about 10,000 conformance queries, mangled token by token with a fixed seed, are prepared, explained, counted and run; about one in six still plans. Only diagnostics or `QueryExecutionException`s may come out. Odd texts (empty, `\0`, deep nesting, overflowing numbers) are tested the same way.
  - **Concurrency:** every conformance query eight times at once on one engine gives what it gives alone, and changes written while queries read are all or nothing to them.
  - **Offline:** the merge engine with no extensions and downloads off gives the conformance results.
  - **Too large:** queries too long, or with plans too large, are refused.
- **Language reference:** `docs/language.md`. Tests keep it in step: every function, query method and diagnostic code is in it, every `gdq` example runs, and every `gdq-error` example is refused.
- **Found while writing the reference**, fixed:
  - an overlay's JSON that left a list out failed to load (the serializer set init-only lists to null); lists may now be left out, and a relation, virtual entity or setting without the names it needs says which;
  - a relation the overlay added took the name the database's own foreign key gave (`shop.customers.orders` led to another source's orders). The sources' foreign keys are named first now, and an overlay navigation to another source whose name is taken gets the source's alias before it (`wh_orders`);
  - `any()` of a group's values with no condition, and a condition over them naming anything but `it`, failed with exceptions;
  - GDQ2101 pointed to `::shop`, which doesn't parse (it is `shop::orders`);
  - `concat` wrote true/false values as each database does (1, t, true); it now writes `true` and `false`, as `toString` does.

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

- Single instance only: enforced (a lock file in the data directory) and documented (`docs/server.md`).

**Built in B0**
- **Host:** `Program` calls `AddGalaxyData()` (services), `UseGalaxyData()` (the pipeline) and `MapGalaxyData()` (the API group, OpenAPI, the client). Tests host the same three with endpoints of their own.
- **Settings:** `GalaxyData:DataDirectory` (`data`, relative to the content root) and `GalaxyData:Merge:*`, which configure the process's one `DuckDbMergeEngine` (memory, threads, temp and extension directories; downloads off).
- **Data directory:** made at startup and held through `galaxydata.lock` (`FileShare.None`) until the application stops. A second instance on the same directory doesn't start, and says why; so does one that can't make it.
- **Problems:** an `IExceptionHandler` maps the engine's exceptions to problem details. Every problem has a `code` (`ProblemCodes`, or its status's) and a `traceId`.

| Exception | Status | Code |
|---|---|---|
| `QueryException`, parse errors (GDQ1xxx) | 400 | `query-syntax`, with `diagnostics` (code, severity, message, start, end) |
| `QueryException`, other errors | 422 | `query-invalid`, with `diagnostics` |
| `QueryExecutionException` | 422 | `query-failed` |
| `DmlScriptException` | 422 | `script-invalid`, with `problems` (message, line, start, length) |
| `SourceUnavailableException` (new in the engine) | 502 | `source-unavailable`; names the source, never the database's words, which are logged |
| `QueryTimeoutException` | 504 | `query-timeout` |
| `ApiException(status, code, title, detail)` | its own | its own |
| anything else | 500 | `internal-error`; details only in development |

  - A client that went away gets 499, logged at debug level.
  - `ApiProblems.Diagnostics(...)` answers with the same problem without throwing.
- **Engine:** `SourceUnavailableException` (a `QueryExecutionException`, with `Target` and `Reason`) is thrown when a source's connection can't be opened or readied; before, the provider's `DbException` escaped as it was. Changes still report it as a failed connection, now without repeating "Couldn't connect".
- **OpenAPI:** `/api/openapi/v1.json`. Enums are camelCase strings, and numbers are strict, so the schema doesn't say "integer or string". `ProblemDetails` lists `code` (required) and `traceId`. A golden snapshot keeps the document under review.
  - `Microsoft.OpenApi` is pinned at 2.7.5: the 2.0.0 that `Microsoft.AspNetCore.OpenApi` 10.0.3 brings has GHSA-v5pm-xwqc-g5wc, a stack overflow when parsing a document with cyclic references. The app only writes documents, but the audit fails the build.
- **Health:** `GET /api/health`, anonymous, gives each check's name and status only: 200, or 503 when one fails. The checks are `dataDirectory` (a file is made and removed) and `mergeEngine` (a session runs `SELECT 1`), each with a 5-second timeout.
- **Client:** static files come from `wwwroot`. Every other path is `index.html` (no-cache), except:
  - paths under `/api`, which give the API's 404s and 405s;
  - files of the kinds the client is built into (`.js`, `.css`, fonts, images), which give 404s when missing.

  Routing's `nonfile` would have turned away `/browse/shop.customers`. A last segment with matrix parameters is always the client's. Note for F4: a last segment that ends in such an extension without matrix parameters is taken as a file.
- **Tests** (`tests/GalaxyData.Web.Tests`, `WebApplicationFactory` with a data directory of its own):
  - health, and a check that fails;
  - the data directory made and held;
  - startup failures;
  - API 404s and 405s;
  - the client's paths;
  - the OpenAPI snapshot;
  - each exception's problem, including that a source's server and login, and an unexpected failure's message, aren't in the answer.

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

**Built in B1**
- **Metadata database** (`Metadata/`): `MetadataDb`, in `{data}/galaxydata.db`.
  - Connections aren't pooled, so nothing holds the file once the application stops.
  - Date-times are converted to and from UTC.
  - Roles are stored as text, and `UserName` is `NOCASE` and unique.
  - `IVersioned.Version` is a concurrency token that goes up on each save of a changed entity.
  - Tables: `Users`, `AdminAuditEvents`, and `Settings` (a key/value store for the application itself).
  - Migrations are made with the repo-local `dotnet-ef` 10.0.11 (`dotnet-tools.json`), into `Metadata/Migrations`. The source folder is `Metadata`, not `Data`, because file names ignore case on Windows, and `Data` is the default data directory `data`.
- **Startup** (`MetadataInitializer`, a hosted service that runs before the server starts):
  - refuses a database with a migration this version doesn't know;
  - backs the database up with SQLite's online backup (`backups/galaxydata-{when}-before-{migration}.db`, the newest 10 kept) when it has applied migrations and this version has more;
  - migrates, and sets WAL mode;
  - makes the first administrator, or resets one.
  - A reset is done once for each password: the `bootstrap.reset` setting keeps a hash of the last password used. A restart with the setting still there therefore doesn't undo the administrator's own change. The reset re-enables the user, makes them an admin, unlocks them, and re-creates them if they were deleted.
- **Settings:** `GalaxyData:Auth:{MinimumPasswordLength 12, MaxFailedSignIns 5, LockoutDuration 15 min, SessionIdleTimeout 8 h, SignInsPerMinute 10}`. All settings, nested ones included, are checked at startup by a source-generated `[OptionsValidator]`.
- **Auth** (`Auth/`):
  - **Data Protection:** keys in `{data}/keys`, DPAPI-protected (current user) on Windows.
  - **Cookie:** `gd.auth`, which on 401/403 answers with a status, not a redirect.
  - **Claims:** id, name, role, `gd:stamp`, and `gd:must-change-password`.
  - **`SessionValidator`:** `OnValidatePrincipal` with a 30-second `IMemoryCache` of each user's stamp and disabled flag, evicted at once on changes made here.
  - **Policies:** `SignedIn`, `CanRead`, `CanEditData` and `CanAdmin`, plus a fallback that also covers requests with no endpoint, so anonymous requests for unknown paths get 401. The current-password requirement fails with a reason, and `AuthorizationProblems` (an `IAuthorizationMiddlewareResultHandler`) answers it with 403 `password-change-required`.
  - **Antiforgery:** the `gd.xsrf` cookie, the `XSRF-TOKEN` cookie the client reads, and the `X-XSRF-TOKEN` header. A group-wide `AntiforgeryFilter` checks POST, PUT, PATCH and DELETE and answers 400 `xsrf-token-invalid`. Tokens are reissued at sign-in, sign-out and password change.
  - **Rate limiter:** a fixed window per remote address, on sign-in and change-password.
- **Endpoints:**

  | Group | Endpoints | Who |
  |---|---|---|
  | `/api/auth` | `GET session`, `POST sign-in`, `POST sign-out` | anonymous |
  | `/api/auth` | `POST change-password` | `SignedIn` |
  | `/api/users` | `GET`, `GET {id}`, `POST`, `PUT {id}` (with version), `DELETE {id}` (`?version=`), `POST {id}/reset-password`, `POST {id}/unlock` | `CanAdmin` |
  | `/api/audit/admin-events` | paged by `before` and `take` | `CanAdmin` |

  - Request validation uses .NET 10's `AddValidation()` (DataAnnotations on records), answering 400 `invalid-request` with `errors`.
  - Failed sign-ins are counted with `ExecuteUpdate`, atomically and without changing the version.
  - Unknown users are checked against a dummy hash, so they take as long as known ones.
  - Update and delete check `own-account` and `last-admin` in an IMMEDIATE transaction. Role, disabled, reset and delete changes give the user a new stamp.
  - Each change is written to the admin audit in the same save.
- **Problems added:** `concurrency-conflict` (also for `DbUpdateConcurrencyException`), `xsrf-token-invalid`, `invalid-request`, `invalid-credentials`, `locked-out`, `account-disabled`, `password-change-required`, `wrong-password`, `weak-password`, `user-name-taken`, `own-account` and `last-admin`.
- **OpenAPI:** needs `CanRead`. The client fallback and health are anonymous.
- **Tests:**
  - **`TestApi`:** a cookie container and the XSRF header, recording every response for secrets.
  - **Coverage:**
    - sign-in, tokens, the same answer for unknown users and wrong passwords, lockout with a manual clock, unlock, disabled users, the rate limit, sign-out, validation;
    - password-change-required, the policy, other sessions ending;
    - users: CRUD in the audit, a name that ignores case, its format, version conflicts, own-account, last-admin (by a race through the database), the changes that end sessions, reset;
    - every `/api` endpoint naming its policy, and the role matrix;
    - the metadata database: the model matches the migrations, WAL and UTC, refusing to start without a password or with bad settings, reset once for each password, a database from a newer version, backups;
    - no response holding a password or a hash.
- **Known limitations:**
  - Signing out removes the cookie, but a copy stays valid until it expires or the stamp changes, since sessions aren't kept on the server (an `ITicketStore` could do that later).
  - The lockout message shows that the account exists.
  - The rate limit is per remote address, which is the proxy's behind one until forwarded headers are set up (B8).

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

**Built in B2**
- **Model:** `Connections` (`SourceConnection`, `IVersioned`), added by the `Connections` migration.
  - `Alias`: NOCASE, unique, immutable; `Kind`; `Mode` (`Form` or `Raw`, how it is edited).
  - `SettingsJson`: the provider's canonical keywords and values, without secrets.
  - `ProtectedSecrets`: Data Protection, purpose `GalaxyData.Connections.Secrets.v1` plus the alias, so a copied blob doesn't read under another alias.
  - `OptionsJson`, `IsReadOnly`, and `SchemaStatus`, used from B3.
  - Connection strings exist only in memory, to connect.
- **`ConnectionKind`** (in place of `IConnectionKind`): one for each of `postgres`, `sqlserver`, `sqlite`, `duckdb` and `excel`. Each has:
  - the descriptor: groups (empty ones left out); fields of type text, number, password, bool, select, filePath, folderPath or keyValues, with `visibleWhen`; and options;
  - `IsSecret` (password, pwd, token, secret);
  - path keywords, checked against `AllowedFileRoots`;
  - reserved keywords: SQLite `Mode`, DuckDB `ACCESS_MODE`, SQL Server `AttachDbFilename`;
  - `Normalize`/`Parse` through the provider's builder, so synonyms get canonical names and unknown keywords fail with the provider's message. DuckDB's builder doesn't do this, so the kind canonicalizes `Data Source` and lowercases settings itself;
  - `Restrict` for read-only: SQLite `Mode=ReadOnly`/`ReadWrite` (never create), DuckDB `ACCESS_MODE=READ_ONLY`, PostgreSQL `Options=-c default_transaction_read_only=on` added to the user's own;
  - `ProbeAsync`: PostgreSQL through `CreateDataSource`, plus the provider's connection prep.
- **`ConnectionInputs`:**
  - **Form:** settings without secrets, plus `secrets: {keyword: {action: keep|set|clear, value}}`. A stored secret not mentioned is kept.
  - **Raw:** `********` keeps (or follows the secret's action), a written value sets, and an absent keyword clears.
  - **Errors:** given by field (`connectionString`, `settings.X`, `secrets.X`, `options.X`, `alias`, `kind`, `mode`) as validation problems.
  - **`Convert`:** echoes only the secrets given in the request.
- **`FileRoots`:** `GalaxyData:Connections:AllowedFileRoots`, by default `{data}/files`, made at startup. Paths must be fully qualified and inside a root (case-insensitive on Windows and macOS). A link that resolves outside the roots is refused.
- **`ConnectionTester`:**
  - checks that the file or folder exists (so it never creates one);
  - runs within `TestTimeout` (10 s), using `WaitAsync` for providers that ignore the token;
  - answers `{ok, message, elapsedMs}`, with secret values scrubbed from messages, and a SQLite probe that clears its pool.
- **Endpoints:**
  - `GET /api/connection-kinds`, `POST /api/connection-kinds/{kind}/convert`;
  - `GET`, `POST`, `GET {id}`, `PUT {id}` (versioned), `DELETE {id}` (`?version=`), `POST {id}/test` and `POST test` (`connectionId` keeps stored secrets) under `/api/connections`;
  - all `CanAdmin`, all audited (secrets by name: set, changed, cleared).
  - Secrets whose keys are gone show `secretsUnreadable`; keeping them is a validation problem, and they must be set again.
  - New problem code: `alias-taken`.
- **Tests:**
  - field keys equal each builder's canonical names; synonyms; refusals; read-only strings; masking; the connection string splitter;
  - resolution in form and raw: keep, set and clear, reserved keywords, roots, options, Excel;
  - API: SQLite, DuckDB and Excel tried; secrets never in answers, the row or the audit; unsaved tries keep stored secrets; the timeout; alias rules; versions; convert; unreadable secrets;
  - PostgreSQL and SQL Server tried against the container servers. Their defaults now live in `tests/Shared/TestServers.cs`, shared with the container tests;
  - the backup before migrating, end to end, from a database at the first migration.

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

**Built in B3**
- **Model** (migration `Schemas`):
  - `SchemaSnapshots`: `Hash` (SHA-256 of the schema without row counts), `Data` (gzip of `IntrospectionJson`), `TableCount`, `TakenAt`, `CheckedAt`, `Changes` (JSON, from the snapshot before), `ReadWith` (the settings and options it was read with). Cascade-deleted with the connection.
  - `Connections` gains `SchemaError` and `SchemaRefreshedAt`.
- **Snapshots** (`Schemas/SchemaSnapshots`): a read with the same structural hash rewrites the newest snapshot's data (its row counts) and `CheckedAt`; a different one adds a snapshot with its diff. The newest 5 are kept.
- **Diff** (`SchemaDiff`): a flat list of `SchemaChange(change: added|removed|changed, object: source|table|column|primaryKey|uniqueKey|index|foreignKey, schema, table, name, properties[{property, from, to}])`.
  - Names are compared exactly.
  - Moved columns are those outside the longest common order of the columns both schemas have, so one column moved is one change.
  - Unique keys are matched by their columns (a rename is a change), indexes by name, foreign keys by name (unnamed ones by what they link).
- **Reading** (`SchemaReader`): connects as queries will. The connection's own read-only setting is used, because DuckDB opens a file one way at a time in a process.
  - `ConnectionKind.Connector(connectionString)` gives a `SourceConnector`: PostgreSQL's data source, or the provider's pool, cleared when it is let go.
  - Reads and tries use `OneOffConnector` (pooling off), so they leave the pools queries use alone, and nothing holds a SQLite file.
  - Excel folders are read under a name of their own (`schema-n`, which no alias can be), so the sheets loaded for queries stay as they are.
  - Failures are `SchemaReadException`s, their messages scrubbed of secrets.
- **Refresh** (`SchemaRefreshQueue`, `SchemaRefresher`, `SchemaRefreshWorker`):
  - The queue deduplicates waiting connections. One asked for while it is read is marked, and queued again once the read ends, so it never holds a second slot waiting.
  - The worker reads `Connections:ParallelRefreshes` (2) at once, each connection one at a time, within `Connections:RefreshTimeout` (10 min, with `WaitAsync`).
  - Statuses are written with `ExecuteUpdate`, so reads never bump a connection's version (no 409s for admins).
  - At startup, `NotLoaded` and `Loading` connections are queued.
  - Reads happen when a connection is made, when its settings, secrets or options change, and on `POST /api/connections/{id}/refresh` (202, `Loading` in the answer).
  - A failed read keeps the last snapshot in the catalog.
- **Catalog** (`CatalogService`, `CatalogState`):
  - Built lazily under a semaphore. `Invalidate()` bumps a wanted generation, read before the build reads the database. A caller after a change waits; the build itself isn't cancelled by one caller.
  - Snapshots are cached by (id, `CheckedAt`), damaged ones too (so they are logged once). The overlay is empty until B5, and re-validating it moves there.
  - An Excel folder is registered with the options its snapshot was read with (`ReadWith`), so its sheets load as the catalog's columns say until it is read again.
  - The version is the first 16 hex characters of a SHA-256 over the engine's version and each connection's id, alias, kind, version, status, refresh time and newest snapshot (id, `CheckedAt`). It is the same after a restart.
  - `CatalogVersionFilter` puts `X-Catalog-Version` on `/api` answers that didn't read the catalog, while it is fresh.
- **Queries' connections** (`SourceConnections : IConnectionFactory`): each build publishes the sources' runtimes, made from their connection strings with secrets.
  - Connectors are shared by connection string, and let go (pools cleared, data sources disposed) when no source uses the string. A connector let go opens nothing (`ObjectDisposedException`, also when it was let go while opening), and the open retries once with the source as it is now.
  - `SourceRuntime.ToString()` gives the alias and kind, never the connection string.
  - A deleted alias is `SourceUnavailableException("There is no such connection any more")`.
  - Excel folders are registered with the provider only when their options change, and removed when their connection is (aliases compared exactly, as the provider does).
  - `SourceProviders` holds the five providers; the Excel one is on the app's merge engine.
- **Tree** (`CatalogTree`): sources, then entity display paths. The default schema's entities sit under the source, other schemas and virtual namespaces are nodes, ids are `QueryText.FormatPath`, and namespaces sort before entities (`OrdinalIgnoreCase`).
  - Search ranks names (equal, prefix, contains) and, for text with `.` or `[`, paths (equal, prefix, then contains), then columns, with ancestor ids.
  - Children aren't paged.
- **Capabilities** (`EntityCapabilities`) follow the engine's rules, now public as `Dml/DmlRules` (`WhyNoInserts`, `WhyNoChanges`, `WhyNotInserted`, `WhyNotUpdated`, `NeedsValue`), which `DmlPlanner` uses too.
  - Changes need the table's own primary key, as the planner does, rather than "PK or non-null unique key".
  - Binary columns are read-only in the app; a table that needs a binary value takes no inserts.
  - The role check is `DataManager` or `Admin`.
- **Endpoints:**
  - `GET /api/catalog`, `GET /api/catalog/tree/children?parent=`, `GET /api/catalog/tree/search?text=&take=` and `GET /api/catalog/entity?name=` are `CanRead`. The entity is a query parameter because names have dots, quotes and brackets.
  - `POST /api/connections/{id}/refresh`, `GET /api/connections/{id}/snapshots` and `GET .../snapshots/{snapshotId}` are `CanAdmin`.
  - `ScalarType` is described in OpenAPI as a string.
- **Engine:** the `DmlRules` above; the message for sources that take no changes reads "`xl (excel) takes no changes`".
- **Found by the review**, fixed:
  - a connection asked for again while read held a second refresh slot for the whole read;
  - an exact path didn't rank first in search;
  - SQLite, SQL Server and DuckDB connectors opened with a retired string after a settings change;
  - an Excel folder registration leaked when its alias came back in another case;
  - an Excel folder loaded with new options while the catalog had the old snapshot's columns;
  - reads and tries cleared the pools queries used;
  - moving one column moved them all in the diff;
  - a damaged snapshot was logged at every build;
  - a startup requeue failure stopped the host;
  - the refresh endpoint failed (500) for a connection deleted as it answered.
  - **Engine:** a table whose key has a column of unknown type (`hierarchyid`) was offered for updates and deletes that couldn't find the row; `DmlRules.WhyNoChanges` and the planner now refuse them.
- **Tests:**
  - the diff (moves, renames) and the structural hash;
  - reads on creation and refresh, kept changes, retention, row counts in place (DuckDB), failures that keep the old schema, timeouts, unreadable secrets, requeueing at startup, reads after edits, deletes;
  - the tree over SQLite, DuckDB (a second schema, a hidden shortcut), Excel and a failed server;
  - search; entities with capabilities by role, read-only connections, views, keyless tables and binary columns;
  - the version across changes and a restart; a damaged snapshot; a folder loaded as its snapshot was read; a connector let go;
  - queries through the catalog and `SourceConnections` (one source, across sources, Excel, a deleted source);
  - PostgreSQL and SQL Server read from databases of their own on the container servers.
  - `XlsxBuilder` moved to `tests/Shared/Excel`, and the fixtures are linked into the web tests.

**API surface** (all under `/api`)

| Area | Endpoints |
|---|---|
| Auth | session, sign-in, sign-out, change-password |
| Users | CRUD, reset-password, unlock |
| Connections | connection-kinds, convert; connection CRUD, test, refresh; snapshots and diff |
| Catalog | tree/children, tree/search, entities/{name} |
| Browse | browse/page, browse/trail |
| Query | validate, explain, execute; saved-queries CRUD |
| Changes | GET, ops, DELETE (scoped), preview, commit |
| Overlay | relations, nav-overrides, virtual-entities (+ validate), entity-settings, issues |
| Audit | admin-events (B1), commits (B7) |

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
| M2 | Binder core: where/select/extend/orderBy/take/skip/distinct, scopes, lambdas, `$params`, lets, coercion, functions, many-to-one navs; virtual entities bound in the catalog (dependency order, inherited key/navs) | Bound-tree and diagnostic snapshots; example 1 binds |
| M3 | Lowering, SQL AST, `SqlBuilder`, 4 dialects, single-site execution, Direct lineage, `gdq` CLI (run, sql, schema, repl) | Golden plans and golden SQL per dialect; example 1 end-to-end on SQLite and DuckDB; SQLite and DuckDB return identical rows for a shared conformance set |
| M4 | Group scope and HAVING, joins, selectMany, collection navs, any/all/in/subqueries, set ops, decorrelation (semi/anti joins, merged group joins, correlated joins), pushdown and prune rules; `first`/`firstOrDefault` moved to M5 | Examples 2–4 golden and end-to-end on SQLite and DuckDB; optimizer phase snapshots |
| M5 | Links, hidden keys, EditTarget, RowIdentity, full lineage, `QueryText.Compose`, `ForCount`, explain model and renderer (`gdq explain`), stable paging, `first`/`firstOrDefault` (as the result: the first row, and `first()` of none fails; in expressions: scalar subqueries, null when there is no row) | Link and lineage snapshots; composed filter reaches the scan |
| M6 | Federation: site assignment and fragmenter, DuckDB merge engine, full fetch, explain with sites and merge SQL | Differential suite: the conformance set on one SQLite database, one DuckDB database, split across SQLite and DuckDB, and with every operator in the merge engine, identical results; spill test at 64 MB; cancellation tests (fetch, merge query, reading) |
| M7 | Adaptive bind-join, runtime scalar params, TopN through navs, cardinality estimates | A 50-row page sends ≤ 50 keys (`ExecutionStats.KeysSent`, `FragmentStats.Strategy/Keys/Batches`); k = 0 early-out; fallback above the key limit; the conformance set split across sources gives the same rows with bind joins always and never |
| M8 | Excel Folder provider (own cell reader, sheets loaded into the merge engine's database, reloaded when workbooks change) | OpenXml-generated fixtures (multiple, hidden, chart, empty and spaced sheets; lock files, damaged files, subfolders); Excel ⋈ SQLite join (navigation and join(), sheets fetched by keys, runtime values) |
| M9 | PG and MSSQL providers (see 4.10) | Container suite runs the same conformance and differential tests; MSSQL varchar parameter typing |
| M10 | DML planner, script splitter, DML-only guard, coordinated executor, `ResultRowEditor`; foreign-key checking per connection (SQLite's `PRAGMA foreign_keys`: on, off, or as the connection has it); `gdq changes` and `gdq script` (see 4.8) | Per provider: insert with returned rows, concurrency conflict rolls back all, cross-connection success, simulated partial commit, guard rejects DDL |
| M11 | Hardening, timeouts, guardrails, language reference doc (`docs/language.md`); see 4.11 | Queries and changes stopped by timeouts in every database; malformed and oversized queries give diagnostics, never other exceptions; concurrent queries; the merge engine offline; the reference's examples run |

**App backend**

| # | Scope |
|---|---|
| B0 | Host, ProblemDetails, OpenAPI, health; single instance per data directory; `SourceUnavailableException` in the engine (see "Built in B0" in §5) |
| B1 | EF metadata, migrations and backup, seeding, cookie auth, antiforgery, policies, users; the admin audit (see "Built in B1" in §5) |
| B2 | Connections, kinds and descriptors, secrets and masking, test-connection; allowed file roots (see "Built in B2" in §5) |
| B3 | Snapshots, refresh worker, diff, CatalogService, tree, search, entity descriptors and capabilities; queries' connections (see "Built in B3" in §5) |
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
  - `dotnet test tests/GalaxyData.Query.ContainerTests` needs the servers `servers.sh up` starts (here: `wsl -d Alpine -u root sh tests/GalaxyData.Query.ContainerTests/servers.sh up`), or `GDQ_TEST_POSTGRES`/`GDQ_TEST_SQLSERVER`. It skips itself when they can't be reached.
- **CLI smoke test:** `gdq repl --source shop=sqlite:tests/fixtures/shop.db --source wh=duckdb:... --overlay overlay.json`, then run the four spec examples plus a cross-source query with `:explain`. Check the fragments, the merge SQL and the lineage output. Then `gdq changes -s shop=sqlite:shop.db -w shop -f changes.json` to see a change set's scripts, and again with `--commit`.
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
- **DuckDB extensions offline:** none are needed. The Excel folder reads its cells itself (M8), and ICU (time zones) is built into DuckDB.NET's native library. The merge engine downloads none unless allowed, and looks for others in `ExtensionDirectory` (M11).
- **Group-key matching complexity:** a large table of positive and negative binder tests.
- **Reserved words** (`and`, `or`, `not`, `in`, `if`, `for`): escape with `it["in"]`. The app quotes names through `QueryText`.
- **AG Grid Community's infinite model with pagination when the total is unknown:** spike this early in F4. The fallback is `MatPaginator` driving the datasource; the URL format doesn't change.
- **Partial commits across connections:** the preview warning, audit statuses and a per-connection result view. Deferred constraints are checked before the first commit.
- **Losing Data Protection keys loses stored secrets:** document that the keys directory must be backed up. The UI shows "re-enter secret" for affected connections.
