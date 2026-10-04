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
  - Snapshots are cached by (id, `CheckedAt`), damaged ones too (so they are logged once). The overlay is built in from B5.
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
| Query | validate, explain, execute, link; saved-queries CRUD |
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

**Built in B4**
- **`ValueCodec`** (`Browse/`): values out as JSON holds them exactly.
  - Text: int64, decimal (with its scale), guid (`D`), binary (base64), date `yyyy-MM-dd`, time, date-time (ISO, no offset), date-time with offset (ISO with its offset; the engine gives them in UTC), interval (`c`), `NaN`/`Infinity`, and unknown types (`ValueConverter.UnknownText`).
  - JSON's own: booleans, int16/int32, finite doubles.
  - `Decode` reads the same back (numbers may be JSON numbers; date-times also `yyyy-MM-dd[ HH:mm]`), else `ValueFormatException`.
- **Raw JSON values are declared `object`** in request DTOs (filter values, keys). .NET 10's `AddValidation` source generator walks into a declared `JsonElement` and fails on its indexer. `[SkipValidation]` would avoid that, but it is experimental (ASP0029).
- **`GridQueryComposer`** builds on `QueryText.Compose`:
  - Columns are checked against the base query's visible columns, exact name first, then ignoring case, and quoted with `QuoteName`.
  - Each value is a parameter `$fN` of the column's type, named apart from the where expression's `$` names.
  - **Ops:** `eq`; `ne` (nulls too); `lt`/`le`/`gt`/`ge`; `between` (inclusive); `contains`/`notContains` (`icontains`, nulls too for `not`); `startsWith`/`endsWith` (`ilike` with `% _ \` escaped); `blank`/`notBlank` (text: or `''`).
  - **Which ops each type takes:** comparisons for numbers, dates and times, text and intervals; equality also for booleans and guids; null tests for everything.
  - **Days of date-times:** a date alone against a date-time (with offset: UTC) column is that day's range.
  - **Where:** one statement (`SplitStatements`), it must parse, and its root mustn't be a statement or assignment. It is wrapped as `(<where>\n)` so a trailing comment can't swallow the closing bracket, and its start is kept so diagnostics come back relative to it (`field: grid.where`).
  - **Sort** replaces the query's own. Paging is the engine's (`QueryRequest.Paging`), which already appends the row identity's key to the sort, so the composer doesn't.
- **`NavigationResolver`:**
  - When the navigation's owner columns are all key columns: `target.where(tc == $keyN ...)`.
  - Otherwise a correlated `any`: `target.where(t => from.any(o => o.k == $key1 and o.fk == t.pk))`, which the engine turns into a semi join; the target's own rows, so it stays editable.
  - Finds inherited navigations too, refuses hidden ones, and decodes keys by the key columns' types.
- **`BrowseService`:**
  - Prepares the base query for its columns (pure), composes, and appends `.extend(_displayN: nav.displayColumn, ...)` for each forward navigation with a `RowLink` on a visible column. The display names are kept apart from every column.
  - Pages with `limit + 1` to know `hasMore`.
  - Counts in parallel with `ForCount()` of the composed text without displays, within `Query:CountTimeout` (null on `QueryTimeoutException`; the last page infers it). The count is cancelled if the page fails.
  - Rows are `{id, k, v, r}`: `id` is the JSON of `k`; `r` the display values by reference.
  - Schema columns' editability comes from the entity's own column (direct lineage without a path) through `EntityCapabilities`, with references (indexes into `v`) and collections (inverse navigations).
  - Trails are resolved crumb by crumb, with each chosen row's display value (`select(title: display).take(1)`).
- **`QueryEngines`:** one `QueryEngine` per `CatalogState`, with `Query:Timeout`, `Query:MaxFetchedRows` and the app's clock. `CatalogService.GetAsync(HttpResponse)` stamps `X-Catalog-Version`. `TypeDto` moved to `Catalog/` for reuse.
- **Settings:** `GalaxyData:Query:{Timeout 1 min, CountTimeout 3 s, MaxFetchedRows 10M, MaxPageSize 1000}`.
- **Endpoints:** `POST /api/browse/page` and `POST /api/browse/trail`, `CanRead`, with the anti-forgery token. Problems: 400 by field, 404 for an entity, 422 with diagnostics, and the engine's 502/504.
- **Found by the tests:** a conditional of `DateTimeOffset` and `DateTime` made a day's start a local-offset `DateTimeOffset`, so `datetime` columns compared with the machine's offset. Each branch is now boxed apart.
- **Found by the review**, fixed:
  - null items in `filters`, `conditions`, `sort` and `crumbs` gave 500s; they are now 400s by field;
  - a date filter on 9999-12-31 overflowed (500); the last day now has nothing after it;
  - a navigation from a source aliased `t` was shadowed by the correlated form's lambda (now `t_`, `t__`, ...);
  - a count that failed (not a timeout) failed the page, and could replace the page's own error. It is now null, and the last page no longer waits for the count;
  - diagnostics in a where with leading spaces were off by them (only its end is trimmed now);
  - display values were added for navigations a virtual entity hasn't, and for foreign keys to non-unique columns, which repeated rows;
  - decimals took thousands separators (`12,25` read as 1225), and offset date-times were parsed loosely (`01/05/2026`); both are strict now;
  - a trail crumb's row was `found` even when it wasn't among the crumb's rows;
  - minor: the default page size against a smaller `MaxPageSize`, singles' noise, display names vs navigation names, at most 50 crumbs, two columns named alike but for case, a where of comments only, the trail's 422/504 in OpenAPI.
- **Tests:**
  - the codec, each type both ways, and its refusals;
  - the composer: each op's text and parameters, days, any/all, where (comments, statements, unbalanced input, parameter names), errors by field;
  - the resolver: direct, semi-join and composite keys;
  - the API on the shop: a page with its schema, references, collections and count; stable pages; every filter; where and its placed diagnostics; navigations (forward, inverse, self, composite, to nothing); readers; trails; a DuckDB view whose count runs out of time; an Excel sheet.


**Built in B5**
- **Engine:**
  - A `CatalogDiagnostic` about an overlay item names it: `Item`, an `OverlayItemRef(kind, index)` into the overlay's lists. Before, only a subject string said what it was about, so a tool couldn't tell which of two relations between the same entities was broken.
  - `RelationDef.OverlayItem` says which overlay relation a relation is, so its navigations' names can be shown.
  - A second entity-settings item for an entity, or a second override of a navigation, reaching it by another path (`shop.main.orders`), was applied on top of the first; it is now GDQ5016 and left out. So are a column's second settings (`name`, `NAME`), a column named twice in a key or relation, and a relation the overlay or the database has already.
  - `VirtualEntity.OverlayItem`, like `RelationDef.OverlayItem`.
  - Settings and overrides of a virtual entity that doesn't work say so (GDQ2025); they were left out without a word.
- **Model** (migration `Overlay`): `OverlayRelations`, `OverlayNavigations`, `OverlayVirtualEntities` and `OverlayEntitySettings`, each `IOverlayItem` (versioned, created and updated times).
  - Entity paths are as queries write them, so items outlive refreshes.
  - Unique indexes (settings by entity, overrides by entity and navigation, virtual entities by name) compare exactly, as `pg.Orders` and `pg.orders` may be two tables. The catalog finds the same entity reached by two paths (GDQ5016).
  - Column lists are EF primitive collections (JSON text); column settings are a JSON list.
- **Catalog** (`CatalogService`):
  - Builds with the stored overlay (`StoredOverlay`: items in the order of their ids).
  - Puts each item's kind, id and version in the catalog's version.
  - Maps the engine's diagnostics back to rows (`OverlayIssues`); an unknown entity of a source without a schema says the schema isn't read.
  - Logs at warning level each item broken since the last build (all broken ones at the first). Re-validating on refresh is this: every build checks every item, and a refresh builds the catalog again.
  - `CatalogState.With(overlay)` builds a trial catalog over the same schemas, for `validate`.
- **Endpoints** (`/api/overlay`, `CanAdmin`):
  - `GET /` (every item with its issues, and counts of items with errors and with warnings only), `GET /export` (the overlay as `gdq --overlay` reads it).
  - For `relations`, `navigations`, `virtual-entities` and `entity-settings`: `GET {id}`, `POST`, `PUT {id}` (`{<item>, version}`), `DELETE {id}?version=`, `POST validate?id=`.
  - One `OverlayEditor` does each operation for every kind, through an `OverlayKind<TRow, TInput, TDto>`: reading a request into a row, its fields for the audit, uniqueness, its DTO, what trying it gives.
  - Items are kept whatever the catalog finds wrong with them, and come back with their issues (code, severity, message): a source may not be read yet, and items that stop working when a schema changes are kept too.
  - Requests: 400 by field for what can be told without the catalog; 409 `overlay-item-exists`; 409 `concurrency-conflict`.
  - Updates that change nothing aren't saved or audited; changes are audited (`overlay.relation.created`, ...) with what changed.
  - `validate`: a relation's navigations; a virtual entity's entity and its query's diagnostics, placed in its text (prepared on a trial engine, `QueryEngines.Trial`); the entity as settings and overrides make it.
- `GET /api/catalog`'s diagnostics name their overlay item (`item: {kind, id}`).
- **Found by the review**, fixed:
  - an update's problems were named two ways: ours relative to the item (`from`), the framework's under the request's field (`relation.fromColumns`); ours are under it now;
  - the issues said an error leaves the item out, but settings and overrides are applied in part (hidden, though the display column isn't there; hidden, though the rename can't be made). That is kept, as one column renamed shouldn't drop an entity's labels, and said so;
  - settings for a virtual entity that doesn't work were dropped without an issue;
  - the same relation could be made twice (now 409, and GDQ5016 for one written another way or that the database declares);
  - names of navigations, display columns and columns weren't trimmed (`" customer "` didn't find it, and slipped past the unique index);
  - names alike but for case (`["at", "AT"]`) made one column twice; the catalog says so now (not the request, as two columns may be named so);
  - any constraint failure on save was taken as "there already"; only a unique one is;
  - suggested and done: `validate` says which other items the change would break (`breaks`), and finds the virtual entity it made by its item, not by its query text.
- **Known limitations:** settings of a virtual entity apply after every virtual entity is bound, so one built on another doesn't see its declared key or type overrides. Each `validate` builds a whole catalog (binding every virtual entity).
- **Validation keys:** the framework's validation problems named fields as the request's types do (`UserName`, `Relation.ToColumns`), and ours as the JSON does. They are all camelCase now: our handlers' problems (`ApiProblems.Invalid`) carry their code, and `ApiProblems.Complete` camel-cases the keys of those without one, the framework's. Our own keys are left alone, as some are provider keywords (`settings.Password`).
- **Tests:** the engine's items in diagnostics and duplicates; the API: a relation across two sources and browsing along it, an item broken by a refresh (issues, `/api/catalog`, the log) and fixed, versions, no-op updates, the audit, export round-tripped through `CatalogOverlay.FromJson`; virtual entities tried (placed diagnostics), saved, browsed and in the tree; settings (labels, types, hidden columns and entities, declared keys, duplicates by another path); overrides (renamed, hidden, tried in place of themselves); deletes; requests refused by field; a source whose schema can't be read; the role matrix.

**Built in B6**
- **Engine:**
  - `QueryText.Parameters(text)` and `ParameterUses(text)` list the `$names` a text uses (and where), outside strings and comments, without parsing it, so an editor can ask for values as the query is written.
  - `BoundProgram.ParameterTypes` / `PreparedQuery.ParameterTypes`: the type each parameter takes as it is bound (a null compared with a date column is a date), for an editor's inputs.
- **Parameters** (`QueryParameterInput {name, type?, value}`): typed values are read by `ValueCodec`; untyped JSON values as `gdq -p` types them (boolean, `int64`, `decimal`, text, null), so text adapts to dates as in the language. Names with or without `$`; twice is a 400.
- **`QueryService`** (`/api/query`, `CanRead`):
  - `validate`: every diagnostic placed in the text, the parameters it uses (missing ones taken as null to check the rest), and its columns. Always 200.
  - `execute`: a page as a grid shows it (`GridQueryComposer` over the query's visible columns, the engine's paging), counted alongside. Every column is sent, hidden keys too, with its type, lineage, link (`row`, `collection`, `drillDown`, with the ordinals it needs) and edit target (entity, column, key ordinals, whether the user may change it, by `EntityCapabilities`). `rowIdentity` when the rows are one entity's (row ids are its key). Rows are `{id, v}` with every value. Stats (time, rows fetched, keys sent, fragments) and warnings.
  - `explain`: of the query, or of the page a grid would fetch. The plan is a flat list of nodes with inputs by id (a nested tree would pass JSON's depth limit of 64 for long chains), with fragments, merge SQL, phases when verbose, and `ExplainTextRenderer`'s text.
  - `link`: a page's text, parameters and row, and a column (or a `related` index): the query of the rows the link leads to (`ColumnLink.Query`; a drill-down keeps the query's parameters), null when it leads nowhere. Also a browse hint: an entity and the row to choose, for a row link to a key, or a navigation from a row, for a collection from a key.
  - Display values of referenced rows (browse's `r`) aren't added to queries' pages: a query's rows needn't have the navigation to reach them.
- **`PagedRows`**: fetching a page with its count alongside, lineage, parameters and query problems, shared by browsing and queries. `ProblemResultException` (was `BrowseProblemException`) carries a problem to answer with.
- **Saved queries** (migration `SavedQueries`): `SavedQuery` (owner, owner's name, name unique for the owner ignoring case, description, text, parameters as JSON, shared, versioned). The owner's foreign key is `SET NULL`, so a deleted user's queries stay: shared ones for everyone, the others for administrators. The owner changes and deletes theirs; administrators also shared ones and orphans. Another's unshared query is a 404, one that is only readable a 403 to change. Saved as written, parameters checked. A save of nothing new changes nothing. New problem: `query-name-taken`.
- **Found by the review**, fixed:
  - finding a text's parameters was quadratic in their number, and done before the text's length was checked: a few MB from any reader could hold a core for hours. It is linear now, and a text too long to run isn't searched;
  - a null in place of a parameter without a value doesn't fit everywhere (`-$n`, `abs($n)`, `$a == $b`), so valid queries failed to validate. Those problems are `info` now, the parameter wanting a value (`complete: false`);
  - warnings of a page were placed in the text with the grid's state composed onto it; they are placed in the query as written;
  - an administrator renaming a shared query learned the names of the owner's private ones (by 409s): only owners rename;
  - two queries of deleted owners clashed by name (EF compares a null owner as `IS NULL`);
  - parameter names like `$1`, which the language reads, were refused;
  - a value given for type `unknown` failed when the query ran, with an empty reason; it is a 400;
  - a drill-down carried the grid's filter parameters into its query;
  - suggested and done: parameters' types in `validate`; `link` checks the page's catalog version when given (409); `explain` explains queries that bind but can't be planned; saved queries' numbers kept as browsers write them back (`50.0` as `50`), so a save of what was read changes nothing; the list leaves texts out; names are measured trimmed; the link's `row` is its `key`.
- **Known limitations:** the parameter finder reads `it.$x` and `ns::$x` as parameters (the binder doesn't); the OpenAPI document describes a 400 as a validation problem, where a query's syntax error is a problem with `diagnostics` (as for browsing).
- **Tests:** the parameter finder; validate (null-filled parameters, placed diagnostics, syntax, bad types); pages with hidden keys, links, edit targets by role, row identity, stats; parameters typed and untyped (decimals, dates as text, nulls); problems (syntax, missing parameter, the grid's where, page size, a parameter twice, explain); values and first rows; drill-downs run with the query's parameters; collection and related links with browse hints; a null foreign key; explain with a grid, verbose; saved queries (ownership, sharing, admins, names, versions, no-op saves, refused requests, a deleted owner).

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

**Built in B7**
- **Model** (migration `Changes`):
  - `ChangeSets`: one for each user, deleted with them, versioned (a concurrency token).
  - `PendingChanges`: the kind, the entity's path, the source's alias, the row's key as JSON (its id) or a new row's `tempId`, and values, originals and display values as JSON objects. A row has one change, and a name one new row.
  - `CommitAudits` and `CommitAuditScripts`.
- **Row ids** (`ValueCodec.RowId`), for browsing, queries and changes alike: a key's values as JSON, each written one way (whole numbers as text, decimals without trailing zeros, date-times with offsets in UTC), so a row has one id however its key was given.
- **Operations** (`ChangeSetEditor`, `POST /api/changes/ops`): set, insert, delete and revert.
  - Checked against the catalog: `EntityCapabilities` for the entity and each column, and values of their columns' types (`ValueCodec`), at most 1,000,000 characters each.
  - Merged: a column's original is the one given at its first edit (needed only then); a value set back to its original is dropped (compared as values); deleting a changed row keeps its originals (and a delete needs some); deleting a new row drops it; display values go with their navigation's columns.
  - A batch applies whole or not (400 by `ops[i].field`, named as given). The set's version is checked as it is saved, and a batch that loses a race is applied again to the set as it is then.
  - At most `Changes:MaxChanges` changes. `DELETE /api/changes` clears all, or a source's or an entity's. Deleting a connection drops every user's changes to it.
- **Preview** (`ChangeService`): every change read again against the catalog (issues for entities and columns gone, values that don't read), and planned by the engine; issues by change and column. A plan (`ChangePlans`, one for each user) when there are none, for `Changes:PlanLifetime`.
- **Commit:**
  - `409 plan-stale` for a plan expired or replaced, changes changed, or a catalog built since. While a user's commit runs, their changes aren't previewed or committed again (`409 commit-in-progress`).
  - Edited scripts are parsed and guarded (`ParseScript`); any statement only for administrators, and DuckDB's scripts edited only by them.
  - The plan is taken once, and the audit written before anything runs. It runs to its end, whoever goes away.
  - Connections that can't be opened are named without the database's words.
  - The changes of the connections that committed are cleared, unless changed since the preview. New rows come back with their keys and ids.
  - Warnings for edited statements that changed no rows, and for anything that fails once the changes are written (the answer still says what was).
- **Audit:** `GET /api/audit/commits` and `{id}`, for administrators. Commits in progress at startup are `unknown`.
- **Engine:** `DmlGuard` also refuses functions it blocks when their names are quoted (`"pg_read_file"(...)`), and says which dialects run in the application (`RunsInTheApplication`: DuckDB).
- **OpenAPI:** an enum used only where it may be null no longer lists null among its values.
- **Found by the review**, fixed:
  - a data manager's edited DuckDB script read a file of the server's (`INSERT ... SELECT * FROM 'C:/.../secret.csv'`): DuckDB reads any file or URL a statement names, with the application's rights. Edited DuckDB scripts are for administrators. Quoted names got past the guard's blocked functions (`"pg_read_file"(...)`);
  - a preview made while a commit ran planned the same changes again (their version changes only once they are cleared), and its commit inserted the rows twice;
  - a failure after the changes were written (saving the audit, clearing them) answered with a 500, or a 409 "changed by someone else", for changes that were written; the answer now says what was written, with warnings;
  - one row could be named by two keys (`"1.50"` and `1.5`), so its changes were two statements, the second a conflict;
  - a connection deleted and made again under its alias would take the old one's pending changes (inserts, and deletes without originals). Its changes go with it, and deletes need originals;
  - an edited script that changed no rows cleared the changes without a word; it is told of;
  - smaller: clearing by entity compared names exactly; originals were needed at every edit; errors named columns two ways; the audit counted rows of scripts rolled back; a failure saving the audit after an unexpected one hid it.
- **Known limitations:**
  - Any catalog built since a preview stales it, a refresh of an unrelated connection included; the client previews again.
  - Every operation reads and writes the user's whole set: one operation on a set of 10,000 changes takes about half a second, and the answer is the whole set.
  - A commit marked `unknown` (the application stopped as it ran) leaves its changes pending; whether they were written must be checked.
  - The engine's: a new row can't be referred to by another in the same set when its key is generated.
- **Tests:** operations merged (originals, values set back, decimals as values, new rows, deletes, reverts by key, id and columns, display values, versions, clearing by source); refused by field (entities, views, read-only sources, keys, key columns, nulls, values, originals, names, sizes), whole batches, readers, a deleted user's changes; limits; one id for a row; previews (order, changes, issues); commits (written, cleared, new rows, the audit); a conflict rolling everything back; one commit at a time (a preview during a commit); stale plans (changes, version, replaced, expired, the catalog, twice); edited scripts (refused, the same but for line breaks, as edited, any statement, statements that changed no rows); two connections, and a conflict on either; DuckDB's scripts; a connection deleted; commits cut short; the guard's quoted names.

**Built in B8**
- **Security headers** (`Hosting/SecurityHeaders.cs`), set as each answer starts, so those made over for a failure keep them:
  - nosniff, no framing, no referrer, same-origin COOP/CORP, a Permissions-Policy turning devices off;
  - a content security policy: the client's (`Security:ContentSecurityPolicy`: its own scripts, inline styles, data images and fonts, blob workers), or the API's, which loads nothing;
  - `Cache-Control: no-store` on the API's answers; HSTS over HTTPS outside development, but for local hosts (`Security:Hsts`, `HstsMaxAge`); `Security:RequireHttps` (and `HttpsPort`) redirects HTTP; no `Server` header.
- **Static files:** names with a content hash (eight capitals and digits, a capital among them) are cached for a year; others `no-cache`.
- **Behind a proxy** (`Proxy:Enabled`, `KnownProxies`, `KnownNetworks`, `ForwardLimit`): `X-Forwarded-For` and `-Proto` from trusted proxies alone (one on the machine, by default). Addresses and networks are checked written in full. `ASPNETCORE_FORWARDEDHEADERS_ENABLED`, which believes every client, stops the application. This resolves B1's limitation of sign-ins counted by the proxy's address.
- **Rate limits** (`Hosting/RateLimits.cs`, `RateLimits:*`):
  - requests to the API a minute, for each user (signed out, each address; IPv6 by /64), in a token bucket given back at the rate set, counted before authorization (so 401s count), health checks aside;
  - requests that run queries or reach sources (`queries`: browse, query execute, commits, trying connections and overlay items) a few at a time for each user, more waiting;
  - 429 on every operation in the OpenAPI document. Sign-ins count by the same address (IPv6 /64).
- **Log redaction** (`Hosting/LogRedaction.cs`): the logger factory is replaced by one whose loggers mask secrets before any provider writes them (providers' own filters kept).
  - `SecretRedactor` masks secrets the application knows (connections' secrets as protected, read or tried; the bootstrap password; six characters or more, standing alone), secret settings as connection strings and JSON write them (`=` or `:`), and URL passwords. Its patterns don't backtrack (linear in the entry).
  - An entry with a secret goes on with its values (its template too) masked, and its exception as masked text.
- **Publish** (`GalaxyData.Web.csproj`, `PublishClient`): `npm ci` and `npm run build` in `src/client` (or `-p:ClientRoot`, relative to the project), publishing `dist/browser` as `wwwroot`. Skipped with `-p:SkipClientBuild=true` or without a client. A `ClientRoot` without one, or a project `wwwroot`, is an error. Checked with a stand-in client.
- **Found by the review**, fixed:
  - a `wwwroot` in the project was published mixed with the client's build, the client's files replacing its own of the same names without a word; it is an error now. (B8 took this for a crash of MSBuild, which F0 found was Microsoft Defender's, whatever is published: see "Built in F0".)
  - `-p:ClientRoot` without a trailing slash skipped the client without a word;
  - secrets added at once could drop one from those masked (8 threads: one in seven rounds);
  - the secret-setting pattern was quadratic: 100 KB took 16 s; patterns are non-backtracking now;
  - `ASPNETCORE_FORWARDEDHEADERS_ENABLED` believed every client, past the `Proxy` settings;
  - answers refused by authorization weren't counted, health checks were, and an IPv6 client could take a new address for each request;
  - HSTS was dropped from answers the exception handler made;
  - short secrets were masked in other words and numbers, telling what they were (`1234` in `51234`);
  - smaller: `Password: x`, JSON keys and URL passwords weren't masked, a line break after `Password=` masked the next line; the token bucket gave 120 a minute for 61; a CSP with a line break would fail every page; `10.1` was taken for `10.0.0.1`; dated names (`notes-20250101.txt`) were cached as hashed; checking queries waited behind running ones.
- **Known limitations:** scopes aren't masked; an exception with a secret reaches structured sinks as text; the publish copies native libraries of every platform (370 MB) unless a runtime is given.
- **Tests:** headers on pages, files, the API, 401s, 404s and failures; the client's policy as a setting; HSTS by environment and setting, through a proxy, on failures; HTTPS redirects; trusted and untrusted proxies, IPv4 as IPv6; the forwarded-headers switch refused; settings checked; each user's requests, an address's (401s, IPv6 /64, health aside), each user's queries at once; masking patterns, known secrets standing alone, long entries, secrets added at once, loggers (messages, values, exceptions), and the application's secrets in its own log.

---

## 6. Angular app (`src/client`)

Scaffold with `npx @angular/cli@latest new … --zoneless --style=scss --ssr=false`. Add Material and CDK (M3 theme), `ag-grid-community` and `ag-grid-angular` (register only the modules used), `monaco-editor` (AMD assets loaded by a loader service), and self-hosted fonts. Tests use Vitest. In development, a proxy sends `/api` to `dotnet watch`. For publish, an MSBuild target builds the client into `wwwroot`.

**Structure**
- `core/`: API clients, `AuthStore`, guards, interceptors (errors, catalog version), `CatalogStore`, `PendingChangesStore`, value and key codecs.
- `shell/`: the app shell.
- `features/`: auth, tree, browse (grid, renderers, editors, nav-picker, inspector/lineage, breadcrumb), changes (drawer, SQL preview dialog), query, editor (Monaco + `gdq` Monarch grammar), admin (users, connections with a dynamic form, overlay, audit).

**Routes**
- `/sign-in` (named as the API names it) and `/change-password`.
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

**Built in F0**
- **Workspace** (`src/client`, `ng new` of Angular 22.2): standalone, zoneless and OnPush by default (Angular 22's defaults), strict templates, SCSS, routing, no SSR, prefix `gd`; TypeScript 6; Material and CDK 22.2. ESLint (angular-eslint 22, with the template accessibility rules) and Prettier (`endOfLine: auto`, as Windows checkouts have CRLF). CLI analytics off.
  - `angular.json`: `outputPath: dist` (the publish target takes `dist/browser`); the production build doesn't inline critical CSS (Angular 22 loads the rest of the stylesheet with an inline script); `ng serve` passes `/api` to `http://localhost:5180` (`proxy.conf.json`).
  - `package.json`: Node `^22.22.3 || ^24.15.0 || >=26` (the CLI's), enforced by `engine-strict` (`.npmrc`). `build` checks the API's types, builds, and checks the CSP; `test` checks the types, runs the scripts' tests (`node --test`) and the unit tests once (`test:watch` watches); `lint`, `format`, `api`. The checks are chained into the scripts, not `pre`/`post` scripts, which `ignore-scripts` skips.
  - The publish target runs `npm ci --include=dev` (the build's tools, whatever `NODE_ENV` says).
- **CSP check** (`scripts/csp.mjs`, `check-csp.mjs`, after each build): `index.html` with an inline script (data blocks aside), an event handler attribute (written with spaces or slashes), a `javascript:` URL, or a resource from another origin, and stylesheets with URLs or imports from another origin, fail the build. Tested (`csp.test.mjs`), and seen failing a build with critical CSS inlined.
- **Generated types** (`scripts/api-types.mjs`, `npm run api`): openapi-typescript 7.13 from the server tests' OpenAPI snapshot, into `src/app/core/api/schema.d.ts`; schemas as types of their own (`UserDto`), open objects as `Record<string, unknown>`. Checked by `build` and `test` (`--check`; line endings aside; seen failing).
  - openapi-typescript asks for TypeScript 5 as a peer; an npm `overrides` entry gives it the workspace's 6 (it uses the compiler API's factory and printer, which 6 keeps).
  - **Server:** the query parameters the catalog's search (`text`) and entity (`name`) need are required in the document (`OpenApiConventions.RequiresQuery`, an operation transformer); their handlers take them as nullable, to answer a problem by field.
- **`ApiClient`** (`core/api/api-client.ts`): `get`, `post`, `put` and `delete` of the document's paths, with `{path, query, body}` as the operation takes them (the argument required when something in it is), answering the operation's success type (`void` for 204). Path values are encoded as one segment each (a missing one throws, and so do `''`, `.` and `..`, which URLs read as steps, encoded or not); query values without a value are left out, and lists are sent value by value. Also `Schema<'Name'>`, `ApiPath<M>`, `RequestBody`, `ResponseBody`, `RequestOptions`. Checked against every operation by the review: `HttpParams` and ASP.NET Core's query parsing agree on `+ & = ; # % [ ] "`, spaces and non-ASCII.
- **Theme** (`src/styles.scss`): `mat.theme` (azure and blue, Roboto, density 0) on `html`, with `color-scheme: light dark`.
  - `ColorScheme` (`core/theme`) sets the page's `color-scheme` to the choice (system, light or dark), keeps it in `localStorage` (`gd.colorScheme`), and gives `dark` (the system's resolved by `prefers-color-scheme`, as it changes) for the grid and the editor. It is created at startup (`provideAppInitializer`).
  - Fonts served with the client: `@fontsource-variable/roboto`, `roboto-mono` (`--gd-code-font-family`), and `@material-symbols/font-400` outlined: `MatIconRegistry`'s default font set (with `mat-ligature-font`, so `fontIcon` works too). An SVG favicon of its own.
- **App:** a toolbar with the name and the color scheme menu, and the router's outlet; unknown addresses go to the start. F1 makes the shell.
- **Tests** (Vitest 5 in jsdom; the test build type-checks, so `expectTypeOf` and `@ts-expect-error` check types):
  - `ApiClient`'s requests: methods, bodies, path values, encoded queries, lists, missing values, steps refused;
  - its types, with calls the document doesn't describe refused (a required query among them);
  - `ColorScheme`: the system's and its changes, choices, kept and unknown values, storage refused, no `matchMedia`;
  - the app's menu (Material's harnesses), and icons by text and `fontIcon` with the app's providers.
- **Checked by hand:** the application published with its client (`dotnet publish -r win-x64`, 57 MB), under the server's headers, in a browser: no console errors (no CSP violations), Roboto and the symbols loaded, the scheme switched and kept across a reload, a deep address (`/browse/shop.customers;f=country:eq:ZA/orders`) served the client. `ng serve` passed `/api/health` to the server.
- **Publishing crashed, whatever was published** (found here; B8's publishes had crashed too): MSBuild printed `Stack overflow.` after copying the files, and hung. A dump showed Microsoft Defender's copy accelerator (`MpDetoursCopyAccelerator`, which Defender loads into processes that copy files) loading `MpClient` on one of MSBuild's copying threads, whose small stack overflowed; the runtime's handler then waited for the loader lock that thread held. A console app with Npgsql or SqlClient alone crashed the same way. `MSBUILDCOPYTASKPARALLELISM=1` copies on one thread and avoids it (server.md says so). With it, publishing takes 15 s.
- **Docs:** `docs/client.md` (developing, the API's types, the theme, building and the CSP, conventions); `server.md` links it, and its publishing section has the Node version, the Defender workaround, and what a project `wwwroot` would do.

**Built in F1**
- **Server:** `GET /api/auth/password-policy` (signed in, even with a password to change): the lengths a password must have (`PasswordPolicyDto`), for forms to say before a password is sent.
- **Session** (`core/auth/AuthStore`): the session as the server gives it, asked for once at a time; signing in, signing out and changing the password; `ended()` (a 401) and `passwordChangeRequired()` (a 403) for what requests meet.
  - **Memory and users:** the application holds what its user could see. When someone else is signed in, or the same user may now see less (fewer permissions after signing in again), it is loaded anew (`PageLoader`): another user at `/`, the same user where they were going; signing out loads `/sign-in`. The same user signing in again after a session ended goes on with what the root services hold.
  - **Asking again** (`load()`): finding someone else signed in is as if they had signed in here; finding no one, while signed in, is the session ending. Every change of session bumps an `epoch`; answers to questions asked before the latest change are left aside.
  - **Tabs** tell each other who is signed in (`SessionChannel`, a `BroadcastChannel`): another user, or signing out, loads a tab anew; the same user (signed in again, or a new password) is read again; a tab that held no one's reloads where it is. A tab back from the back-forward cache, or coming into view 30 s after it last asked, asks again.
- **Routes:** `/sign-in` and `/change-password`, lazy, with `returnUrl` (`safeReturnUrl`: a path read as browsers read it, so tabs, line breaks and `\` don't lead to another site; not these pages; the start otherwise; from the password page, its own `returnUrl`); the shell at `''`, with `signedInGuard` as `canActivate` and `canActivateChild`; unknown addresses to the start. `allowedTo(permission)` sends the signed-out and those with a password to change on, with the return address, as a `canMatch` guard too (it runs before its parents' guards). The return address of a navigation under way is its target (`navigationTarget`). Titles "Page · GalaxyData" (`PageTitles`); route inputs (`withComponentInputBinding`).
- **Interceptors:**
  - `sessionInterceptor`: 401 `unauthenticated` signs in again, with a notice and the return address; 403 `password-change-required` goes to its page; 400 `xsrf-token-invalid` asks for the session (a new token) and resends once, setting the header itself (Angular's XSRF interceptor runs before the others and doesn't set one twice), with the resent request's problems handled too. Session problems of requests sent under an older epoch are left alone. 403 `forbidden` is the caller's (a role change ends sessions, a 401).
  - `catalogVersionInterceptor`: `CatalogVersion` keeps the last `X-Catalog-Version` (for F3).
- **Problems** (`core/api/problem.ts`): `problemOf` (the API's problems; no answer, or a proxy's 502 to 504, is `unreachable`; codes by status for answers without a problem; `Retry-After`), `problemMessage`, `isSessionProblem`; `Notifier` (a snack bar; the session's problems left out).
- **Forms:** signal forms (stable in Angular 22; Material's inputs take `[formField]`), submitted through `submission.action` with `<form [formRoot]>`; `fieldErrors` puts `invalid-request` errors on fields by their JSON names; `focusFirstInvalid` after a refused submission.
- **Pages:**
  - **Sign-in:** user name and password (shown on asking), the session-ended notice, refusals as the server says them, "Try again" when the server couldn't be reached.
  - **Change password:** forced (an explanation, and Sign out) or chosen (Cancel); the policy's hint; checks before sending (length, the user name, unchanged, confirmation); wrong and weak passwords on their fields; a hidden user name field for password managers.
- **Shell:** the bar (a banner, with "Skip to the page"): navigation button, name, color scheme menu, user menu (name, user name and role; change password; sign out); the navigation from `navItems`, filtered by `needs`, beside the page from 960 px and over it below (closing once a page is chosen); the start page greets the user and says what they may do.
- **Accessibility:** focus goes to `main` when another page opens (not on a page's own address changes); live regions a page opens with are filled after it shows; one navigation landmark (`mat-nav-list` is one).
- **Bundle:** the auth pages are lazy; the first load is 597 kB (136 kB compressed), Angular and the shell's Material parts. The warning budget is 700 kB (Angular's 500 kB is for a new app); the error stays at 1 MB.
- **Tests** (109 Vitest, 6 node): problems; return addresses; the page loader; the store (once at a time, failures, sign-in, another user, fewer permissions, sign-out, ended, the password page's return address, a navigation's target, stale answers, asking again finding another user or no one, tabs, coming back); the interceptor (each problem, resending once, the resent request's problems, stale epochs); guards (as `canMatch` and `canActivateChild` too); the catalog version; field errors; the sign-in and password pages through the router and Material's harnesses (focus included); the shell (navigation, user menu, sign-out, narrow screens, banner and skip link); the whole application from the start through signing in to a session ending. Mutations of the key rules (resending once, stale epochs, another user, fewer permissions, other origins, password first, the `canMatch` return address, tabs, focus) fail tests.
- **Checked by hand** (`ng serve` with the server, then published): wrong credentials; the first administrator's forced change (checks before sending, the wrong current password on its field, the change); a session ended behind the client's back (the 401 brought sign-in with its notice; the same user went on); two tabs following each other's sign-out and sign-in; the narrow layout; the published application under its CSP with no console messages, the lazy pages included.
- **Found while checking:** a session ending on the password page left the user there; nested navigation landmarks; publishing runs `npm ci`, which fails (`EBUSY`) with `node_modules` half removed while `ng serve` runs (documented in both guides).
- **Found by the review**, fixed:
  - asking for the session again could change who was signed in without loading anew, find no one without asking to sign in again, or overwrite a newer session with an older answer (now `found()`, epochs, `canActivateChild`);
  - a slow request's 401 from before signing in again threw the user out again (stale epochs left alone);
  - `allowedTo` as `canMatch` lost the return address of the signed-out and of those with a password to change;
  - `/\t/evil.example` passed as a return address, and a tab reloading at its own address could leave the site (addresses parsed; the page loader refuses other origins; tabs reload in place);
  - a stale "Can't reach the server" stayed after the session ended;
  - another user was taken to the previous user's address (now the start);
  - tabs back from the back-forward cache or into view didn't ask again;
  - smaller: the same user with fewer permissions kept what they could see before; a tab on the forced password page stayed after the password was changed in another; the password page's return address was lost when the session ended there; a navigation's target was lost to `router.url`; live regions weren't announced; focus didn't move; no banner or skip link; the password-policy endpoint didn't declare its 401; the shell's return test started at `/`.

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
| B4 | GridQueryComposer, codecs, NavigationResolver, CountStrategy, browse endpoints and trails (see "Built in B4" in §5) |
| B5 | Overlay CRUD and validation; overlay items named by the engine's diagnostics (see "Built in B5" in §5) |
| B6 | Query validate, explain, execute and links; saved queries (see "Built in B6" in §5) |
| B7 | ChangeSets, merger, preview, commit, audit (see "Built in B7" in §5) |
| B8 | Security headers and CSP, rate limits, log redaction, publish target (see "Built in B8" in §5) |

**Frontend**

| # | Scope |
|---|---|
| F0 | Workspace, theme, generated types, Vitest (see "Built in F0" in §6) |
| F1 | Auth, guards, interceptors, shell (see "Built in F1" in §6) |
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
