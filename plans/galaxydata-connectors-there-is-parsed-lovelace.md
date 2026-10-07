# GalaxyData — Connectors as modules, and ClickHouse

## Context

GalaxyData has one project per database: `GalaxyData.Query.Sqlite`, `.DuckDb`, `.PostgreSql`, `.SqlServer` and
`.Excel`. But each database's code doesn't live only there. It is also in four other places:

- **The engine** (`src/GalaxyData.Query`)
  - It holds all four SQL dialects (`Sql/Dialects/*.cs`), exposed as `SqlDialect.Sqlite/DuckDb/PostgreSql/SqlServer/All`.
  - No other assembly can derive from `SqlDialect`: its constructor is `private protected`, its hooks are `internal`,
    and the SQL AST is `internal`.
  - `DmlGuard`, `SqlScriptSplitter` and `FederationPlanner:305` compare against those instances (`dialect == SqlDialect.DuckDb`).
- **The web host** (`src/GalaxyData.Web`)
  - It holds every kind of connection in `Connections/Kinds.cs` (`PostgreSqlKind` … `ExcelKind`, written with Npgsql,
    SqlClient, Sqlite and DuckDB.NET types).
  - It lists the providers by hand (`Hosting/WebApp.cs:92-101`, `Catalog/SourceProviders.cs:20`).
  - It special-cases Excel in `StoredConnections`, `SourceConnections`, `CatalogService:176`, `SchemaReader:37` and
    `SourceProviders.Excel`.
  - It reads a SQLite option in `StoredConnections:40`.
- **gdq**: `src/GalaxyData.Query.Cli/SourceSpec.cs` and `GdqApp.cs` switch on kind strings and make each provider's
  connections themselves.
- **The client**: `tree-nodes.ts` maps each kind to its display name, and gives Excel its own icon.

So adding a database means touching every one of these. The goals are:

1. Each database's dialect, provider, introspection, type mapping, connection form and command-line handling live
   in its own project, which plugs in as one **`Connector`**. The engine, the web host and gdq name no database
   (apart from DuckDB as the merge engine).
2. Add **ClickHouse** as a connector, built that way.

**Decisions confirmed with the user**

- **Compile-time modules.** Each connector project exposes one `Connector`, and one list in the new
  `GalaxyData.Connectors.BuiltIn` project names them.
  - Adding a connector takes a new project, one line in that list, a ProjectReference and a `.slnx` entry.
  - Loading connectors from a folder at run time comes later; the contract mustn't rule it out.
- **ClickHouse is read-only** in this version. It supports browsing, queries, federation and dashboards, but no
  changes; it is always read-only, as Excel folders are.
- **Project names stay `GalaxyData.Query.<Db>`.** The new connector is `GalaxyData.Query.ClickHouse`, and the shared
  contract is `GalaxyData.Connectors`.

**Out of scope (later)**

- A pluggable merge engine. DuckDB stays the merge engine, configured by the hosts, and Excel keeps depending on it.
- Loading connectors from a folder.
- Writes to ClickHouse (§5.7).
- DuckDB's `clickhouse_scanner` extension, published 2026-09-30, which is too new to rely on.

**Conventions** follow the earlier plans:

- **Code style:** 3-space indents, Allman braces, file-scoped namespaces, sealed classes, explicit types, prose `///`
  comments, warnings as errors.
- **How each milestone ends:** green, with a review (with probes) whose findings are fixed or listed, and a
  "Built in Cn" note added here.

---

## 1. Where things live afterwards

| Project | Holds | References |
|---|---|---|
| `GalaxyData.Query` | The engine; `SqlDialect` and the parts of the SQL AST that dialects build, public (the "dialect SDK"); `SourceProvider`; `IMergeEngine`. **Names no database.** | Common |
| `GalaxyData.Connectors` (new) | `Connector`, `ConnectorContext`, `ConnectorSet`, `IAttachedSources`, the command-line types; moved from Web: `ConnectionKind` and its form DTOs, `ConnectionKinds`, `SourceConnector`/`ProviderConnector`/`DataSourceConnector`, `ConnectionStrings` | Query |
| `GalaxyData.Query.<Db>` | `<Db>Dialect`, `<Db>SourceProvider`, introspector, type mapper, `<Db>Kind`, `<Db>Connector` | Connectors and the database's driver (Excel: DuckDb) |
| `GalaxyData.Connectors.BuiltIn` (new) | `BuiltInConnectors.Create(ConnectorContext)`: **the one list** | every connector project |
| `GalaxyData.Web`, `GalaxyData.Query.Cli` | The hosts | Connectors, BuiltIn, and DuckDb only for the merge engine |

The web host's metadata database (EF Core on SQLite) and the DuckDB merge engine stay in the hosts, since neither is a
data source.

---

## 2. The engine: dialects as plug-ins

### 2.1 Hooks in place of named dialects

All of these defaults reproduce today's output.

**On `SqlDialect`** (each `protected internal`):
- `RunsInTheApplication` replaces `DmlGuard.RunsInTheApplication(d) => d == SqlDialect.DuckDb`. It is true for DuckDB.
  The public static `DmlGuard.RunsInTheApplication(dialect)`, which `ChangeService` calls, now reads it.
- `ChangeRules` replaces `DmlGuard.IsSqliteReplace` and the per-dialect sets in `Forbidden`. It returns a
  `ChangeScriptRules` record, holding:
  - `MoreChangeKeywords`: SQLite's `REPLACE`, which also feeds `QueryEngine.KindOf`.
  - `ForbiddenWords`: the T-SQL list, matched only when unquoted.
  - `ForbidsSelectInto`.
  - `ForbiddenFunctions`: the PostgreSQL and DuckDB lists, and SQLite's `load_extension`.
  - `ForbiddenFunctionPrefixes`: DuckDB's `read_`.
  
  The four sets move into their dialects, and the messages stay word for word.
- `ScriptSyntax` replaces the splitter's `sqlServer`/`sqlite`/`postgresLike` flags (`SqlScriptSplitter.cs:90, 150-152`). It returns a `ScriptSyntax` record of:
  - `BracketNames`, `BacktickNames`
  - `DollarQuotes`, `EscapeStrings`
  - `NestedComments`
  - `BatchSeparator` (`GO`)
  - `TriggerBodies`

**Elsewhere in the engine:**
- `FederationPlanner:305` uses `SqlDialect.DuckDb.ComparesExactly`. The planner gains a `mergeDialect` field set by
  `Plan(...)`, and uses that instead.
- `FederatedExecution:220` writes its keys query as text ending in `… LIMIT n`. It becomes a `SqlSelect`, written by
  `SqlWriter` in the merge dialect.

The new records are public, in `GalaxyData.Query.Dml`.

### 2.2 `SqlDialect` opened to other assemblies (`Sql/SqlDialect.cs`, `Sql/SqlAst.cs`)

The C# rules this has to satisfy:
- The types in a `protected internal` member's signature must be public.
- A dialect in another assembly overrides with `protected override`.

The changes:
- **Constructor:** `private protected` becomes `protected`.
- **Hooks become `protected internal`.** This covers every hook the engine calls, today `internal virtual`/`abstract`:
  - `HasBooleanValues`, `Paging`, `ConcatOperator`, `SharesParameters`, `ComparesExactly`, `TypeName`
  - `BooleanLiteral`, `NullOrdering`, `WriteLiteral`, `Divide`, `Modulo`, `TextLength`, `MaxNameLength`
  - `Returning`, `AcceptsIdentityValues`, `RowCountOfChange`, `InsertedIdentity`, `ComparesOriginal`, `KeyEquals`
  - `SortKey`, `Compare`, `Aggregate`, `AggregateName`, `Function`
  
  The engine and its tests (through InternalsVisibleTo) still call them, and applications using the library don't see them.
- **`private protected` members become `protected`:**
  - members: `IsBare`, `WriteString`, `WriteQuoted`, `Concatenation`, `WriteCharacter`, `WriteTemporal`, `WriteGuid`,
    `WriteBinary`, `NameLength`;
  - helpers: `Call`, `Cast`, `Template`, `Binary`, `Text`, `Integer`, `Raw`, `Iif`, `Like`, `BooleanText`,
    `ConcatArguments`, `IsInteger`, `Int`, `TextThenInts`, `IsFractional`.
  
  `FitName` stays internal.
- **Types that become public**, with get-only properties:
  - `PagingStyle` and `ReturningStyle`.
  - `SqlCall`, with an internal constructor.
  - `SqlExpr`, with a `private protected` constructor so no other assembly can add node kinds.
  - The nodes dialects build, with public constructors: `SqlBinary`/`SqlBinaryOp`, `SqlUnary`/`SqlUnaryOp`, `SqlIsNull`,
    `SqlIn`, `SqlBetween`, `SqlLike`, `SqlCase`/`SqlWhen`, `SqlCast`, `SqlLiteral`, `SqlRaw`, `SqlFunctionCall`,
    `SqlTemplate`, `SqlAggregate`, `SqlSetOperator`.
  - The nodes dialects only inspect, with internal constructors: `SqlColumn` (with `NativeType`) and `SqlParameterRef`.
- **Stays internal:** `SqlBuilder`, `SqlWriter`, `SqlKeywords`, `SqlPrecedence`, the subquery nodes, and every query
  and DML node.

### 2.3 Dialects move into their provider projects

- **Each dialect** becomes `public sealed class <Db>Dialect : SqlDialect` with a static `Instance` and a private
  constructor, in `src/GalaxyData.Query.<Db>/`.
- **The engine's statics** `SqlDialect.Sqlite/DuckDb/PostgreSql/SqlServer/All` are deleted, along with the class
  comment "All dialects live in this assembly".
- **Every user is updated:** the four `*SourceProvider.Dialect` properties, `DuckDbMergeEngine` (lines 157, 198 and
  319), and `ExcelSourceProvider` and `ExcelFolder`, which switch to `DuckDbDialect.Instance`.
- **Tests:**
  - `GalaxyData.Query.Tests` gains ProjectReferences to the four provider projects.
  - `tests/GalaxyData.Query.Tests/Sql/TestDialects.cs` holds `Sqlite`, `DuckDb`, `PostgreSql`, `SqlServer` and
    `All`, plus `Writable`, which later leaves ClickHouse out. It isn't named `Dialects`, which collides with
    `TheoryData` properties, and it isn't in `tests/Shared`, whose files IntegrationTests also links without those
    providers.
  - The statics are replaced mechanically in `SqlReportTests`, `SqlDialectTests`, `DmlReportTests`, `DmlEdgeTests`,
    `SqlScriptTests` and `FederationReportTests`.
  - Snapshots are found by `ProviderKind`, so no snapshot changes.

### 2.4 Hooks ClickHouse needs (added with C4; defaults keep today's SQL)

- **`SetOperator(SqlSetOperator)`** is called by `SqlWriter:178-184`. The default is UNION / UNION ALL / INTERSECT /
  EXCEPT; ClickHouse writes `UNION DISTINCT`, `UNION ALL`, `INTERSECT DISTINCT` and `EXCEPT DISTINCT`.
- **`LikeEscape(char)`** is called by `SqlWriter:436`. The default is `" ESCAPE '\'"`; ClickHouse returns null, since
  backslash is already its escape character.
- **`MaxCorrelationDepth`** defaults to unlimited; ClickHouse's is 1. Past it, `SqlBuilder` throws
  `NotSupportedException`. It checks in two places:
  - in `Translate`'s outer-column lookup (around line 557), by the level the column comes from;
  - at the top of `SemiJoin` (line 336), when the join has a condition, which counts as one level.

  A `NotSupportedException` thrown while building already falls back to the merge engine:
  1. `QueryEngine.Single` catches it and calls `Federate`.
  2. `FederationPlanner.Builds` catches it, so `Candidate` returns null.
  3. `Cut` recurses into the inputs.

  Only what the builder or writer refuses falls back, though; SQL the server rejects fails the query. So every
  ClickHouse capability check must sit in the dialect, builder or writer.

---

## 3. The connector contract (`src/GalaxyData.Connectors`, new)

### 3.1 Moved from the web host

These move into namespace `GalaxyData.Connectors` unchanged:
- from `ConnectionKind.cs`: `ConnectionKind`, `FieldType`, `ChoiceDto`, `VisibleWhenDto`, `GroupDto`, `FieldDto`,
  `ConnectionKindDto`, `ConnectionKinds`;
- from `SourceConnector.cs`: `SourceConnector`, `ProviderConnector`, `DataSourceConnector`;
- `ConnectionStrings`.

The web host keeps `ConnectionInputs`, `ConnectionSecrets`, `ConnectionTester`, `FileRoots` and `StoredConnections`.

Neither OpenAPI nor the app sets schema ids (`Hosting/OpenApiConventions.cs`), so ids are simple type names. Moving
namespaces therefore leaves `HostTests.TheOpenApiDocumentDescribesTheApi.json` unchanged.

Kinds stay form descriptions that can be made without arguments, as the web tests make them (`new SqliteKind()`).
Runtime state belongs on the connector. The kind gains:

```csharp
public virtual SourceInfo Configure(SourceInfo source, IReadOnlyDictionary<string, string> options) =>
   source with { TrustForeignKeys = Flag(options, TrustForeignKeysOption) ?? false };
protected static bool? Flag(IReadOnlyDictionary<string, string> options, string name);
protected static string Things(long count, string one, string many);   // was SqliteKind.Things
```

`SqliteKind` overrides `Configure` to set `EnforceForeignKeys`; ClickHouse's sets `IsReadOnly = true, SupportsDml =
false`. `StoredConnections.Source` calls `kind.Configure`, so the web host no longer names SQLite.

### 3.2 The plug-in

```csharp
public sealed class ConnectorContext(IMergeEngine merge) { public IMergeEngine Merge { get; } = merge; }

public abstract class Connector : IDisposable
{
   public string Id => Kind.Id;
   public abstract SourceProvider Provider { get; }
   public abstract ConnectionKind Kind { get; }
   public virtual IAttachedSources? Attached => null;           // Excel
   public virtual CommandLineHelp? CommandLine => null;         // null: gdq doesn't offer it
   public virtual ValueTask<OpenedSource> OpenAsync(CommandLineSource source, CancellationToken cancellationToken);
   protected ValueTask<OpenedSource> OpenDatabaseAsync(CommandLineSource source, DbConnection keeper, Func<DbConnection> open,
                                                       IAsyncDisposable? owner, CancellationToken cancellationToken);  // open, run .sql, introspect, Configure
   protected static string ExistingFile(CommandLineSource source, string kind);
   protected virtual void Dispose(bool disposing) { }
}

public interface IAttachedSources   // sources the provider opens itself, under an alias, with no connection string
{
   SourceInfo Attach(string alias, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> options); // again only if they differ
   bool Detach(string alias);
   Task<SourceSchema> ReadSchemaAsync(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> options,
                                      CancellationToken cancellationToken);                                                     // under a name of its own
}

public sealed record CommandLineSource(string Alias, string Target, bool Writable);   // IsScript, IsMemory, IsConnectionString
public enum CommandLineTargets : byte { Files, ConnectionString, Folder }               // gdq lists kinds in this order
public sealed record CommandLineHelp(CommandLineTargets Targets, string? Example);
public sealed class OpenedSource : IAsyncDisposable { /* Info, Schema, Open, Warnings */ }
public sealed class ConnectorSet : IDisposable { /* All, Find(id), Kinds (ConnectionKinds), Providers */ }
```

**`ConnectorSet`**
- Checks that ids are unique, and that each connector's `Kind.Id` equals its `Provider.ProviderKind`.
- Disposes the connectors.

**Connectors in gdq**
- Each connector's command-line open logic moves over **as it is today**, so gdq's behaviour doesn't change:
  - SQLite: the shared-cache in-memory database for `:memory:` and `.sql`, or `Mode` from the builder.
  - DuckDB: `:memory:` with `Duplicate`, or `ACCESS_MODE`.
  - PostgreSQL: its own `NpgsqlDataSource`.
  - SQL Server: `SqlConnection`.
  - Excel: the folder check, and "can't be written".
- gdq lists kinds by `CommandLineTargets`, then in the order of the built-in list. That gives "sqlite, duckdb, postgres,
  sqlserver or excel", which matches `GdqTests.Transcript.txt`.

**`ExcelConnector`**
- Requires `context.Merge as DuckDbMergeEngine`, and says so when it isn't one.
- Owns and disposes `ExcelSourceProvider`. Its DI registration goes, so it isn't disposed twice.
- Implements `IAttachedSources`, taking over:
  - from `SourceConnections`: the per-alias options and the `Same` comparison, under a lock;
  - from `SchemaReader`: the `schema-N` throwaway name;
  - from `StoredConnections.Folder`: building the `ExcelFolderOptions`.

### 3.3 The one list (`src/GalaxyData.Connectors.BuiltIn/BuiltInConnectors.cs`)

`Create(ConnectorContext context)` returns this `ConnectorSet`:

```
[new PostgreSqlConnector(), new SqlServerConnector(), new SqliteConnector(), new DuckDbConnector(), new ExcelConnector(context), new ClickHouseConnector()]
```

This is the web host's order of kinds today, so `ConnectionApiTests.TheKindsDescribeTheirForms.json` keeps its order.
ClickHouse is added in C5.

---

## 4. The hosts

### 4.1 Web (`src/GalaxyData.Web`)

- **`Hosting/WebApp.cs`**
  - Keeps `DuckDbMergeEngine` and `IMergeEngine` (the merge engine).
  - Adds `AddSingleton(sp => BuiltInConnectors.Create(new ConnectorContext(sp.GetRequiredService<IMergeEngine>())))` and
    `AddSingleton(sp => sp.GetRequiredService<ConnectorSet>().Kinds)`.
  - Deletes the five `AddSingleton<ConnectionKind, …>` registrations and the `ExcelSourceProvider` registration.
  - The set is created after the merge engine, so it is disposed first.
- **`Connections/Kinds.cs`** is deleted; each kind is in its provider project.
- **`Catalog/SourceProviders.cs`** is built from `ConnectorSet.Providers`. Its `Excel` property goes.
- **`Catalog/QueryEngines.cs`** takes `IMergeEngine`.
- **`Catalog/SourceConnections.cs`**
  - `SourceRuntime` gains `IAttachedSources? Attached`.
  - `Folder(...)` becomes `Attach(alias, attached, settings, options)`.
  - `PublishAsync` detaches aliases that no runtime has with the same owner and the same exact alias.
- **`Catalog/CatalogService.cs:176`:** `kind is ExcelKind` becomes `connector.Attached is { } attached`, which attaches
  with `snapshot.ReadWith`'s settings and options, or else the row's.
- **`Schemas/SchemaReader.cs:37`:** `attached.ReadSchemaAsync`.
- **`Connections/StoredConnections.cs`:** `Source` calls `kind.Configure`. `Folder()` and the `GalaxyData.Query.Excel` using go.
- **`GalaxyData.Web.csproj`:** references `GalaxyData.Connectors`, `GalaxyData.Connectors.BuiltIn` and
  `GalaxyData.Query.DuckDb`. The other provider references go.
- **Catalog DTOs** (`CatalogSourceDto` and the tree node's) gain `KindName` and `KindIcon`, from new `DisplayName` and
  `Icon` properties of the kind. This is the only change to the API (C3).

### 4.2 gdq (`src/GalaxyData.Query.Cli`)

- `GdqApp` makes the merge engine and the `ConnectorSet` before parsing, and disposes the set before the merge engine.
- `SourceSpec.Parse(text, set)` takes its kinds from the set. The "needs a connection string" message uses
  `CommandLineHelp.Example`, with the same text as now. The `--source` help is generated.
- `CliSources` becomes generic over `OpenedSource`.
- The `Npgsql`/`SqlClient`/`Sqlite`/`DuckDB` usings and the provider list in `GdqApp.cs:84-86` go.
- The csproj keeps only DuckDb (the merge engine) among providers, plus Connectors and BuiltIn.

### 4.3 Client (`src/client`)

- `app/features/browse/tree-nodes.ts` and `catalog-overview.ts` take the kind's name and icon from the DTOs. The
  `sourceKinds` map and the `'excel'` test go.
- `schema.d.ts` is regenerated with `npm run api`.
- `sql-languages.ts` already falls back to `sql`.

### 4.4 Guarding it

An architecture test in each of Query.Tests and Web.Tests reads the compiled references
(`Assembly.GetReferencedAssemblies()`):

- `GalaxyData.Query` references no connector project.
- `GalaxyData.Web` and `gdq` reference no `GalaxyData.Query.<Db>` except `GalaxyData.Query.DuckDb`, and only from the
  merge engine's registration.

Only types used in code appear among compiled references. So this catches a host that starts naming a provider
again, which transitive ProjectReferences would otherwise allow.

---

## 5. ClickHouse (`src/GalaxyData.Query.ClickHouse`, new)

### 5.1 Driver and connections

- **Package:** `ClickHouse.Driver` 1.5.x — official (ClickHouse/clickhouse-cs), MIT, net10.0, ADO.NET over HTTP (8123,
  or 8443 for https). It goes into `Directory.Packages.props`. The archived `ClickHouse.Client` and the native-only
  Octonica client are not used.
- **Connections** come from a `ClickHouseDataSource` per connection string (`DataSourceConnector`). Each
  `new ClickHouseConnection` gets its own HTTP pool.
- **Trying a connection:** `Open()` doesn't contact the server, and `ServerVersion` throws. So the probe runs
  `SELECT version(), currentDatabase()`.
- **Supported servers: 26.3 LTS and later**, which have correlated subqueries on by default, `<=>` everywhere, and
  `lag`/`lead`. The image tested is 26.8 LTS.

### 5.2 Settings the language's meaning depends on

These are sent with every command, set in `PrepareCommand` through the command's settings:

| Setting | Why |
|---|---|
| `join_use_nulls=1` | Unmatched outer-join columns are NULL, not 0, '' or 1970-01-01 |
| `aggregate_functions_null_for_empty=1` | min, max and avg of no rows are NULL; the engine already writes `coalesce(sum(x), 0)` |
| `session_timezone='UTC'` | The other providers work in UTC |
| `readonly=2` | Nothing writes, but settings can still be sent (`readonly=1` would forbid them) |

`ClickHouseKind` reserves the matching `set_…` connection keywords. The account must be allowed to change settings: a
profile with `readonly=1` is refused with a clear message when the connection is tried.

### 5.3 Dialect (`ClickHouseDialect`)

**Names and kinds**

`Name` is `ClickHouse` and `ProviderKind` is `clickhouse`. Paging is `LimitOffset`; `NullOrdering` keeps its default
(`NULLS FIRST/LAST`), since ClickHouse puts nulls last otherwise.

**Literals and types**

- **Names:** quoted in backticks, escaping `\` and `` ` ``.
- **Strings:** literals escape `\` and `'`; line breaks are written `char(10)` and `char(13)`.
- **Other literals:** `toDate32('…')`, `toDateTime64('…', 6, 'UTC')`, `toUUID('…')` and `unhex('…')`.
- **`TypeName`:** writes `Nullable(T)` for nullable types, since `CAST(NULL AS Int32)` fails. The types are `Int16`,
  `Int32`, `Int64`, `Float32`, `Float64`, `Decimal(p,s)`, `String`, `Bool`, `Date32`, `DateTime64(6)` and `UUID`.
  Time of day has no type, so `TypeName` throws `NotSupportedException` and the query falls back.

**Parameters**

- Written `@name`. The provider sets each one's `ClickHouseDbParameter.ClickHouseType` from its type, in both
  `BindParameter` overloads, and the driver turns it into `{name:Type}`. Giving the type avoids the driver's
  decimal-inference bug (#637).
- `MaxParameters` is 1000, since parameters travel with the HTTP request. A bind join of 5,000 text keys proves it.

**Arithmetic**

`Divide`: `/` already gives Float64 for whole numbers, so nothing is cast. Decimal division truncating to the
dividend's scale is a documented difference.

**Functions**

- Text: `lengthUTF8`, `substringUTF8`, `upperUTF8`, `lowerUTF8`, `positionUTF8`, `trimBoth`.
- Dates: `toYear`, `toMonth`, `toDayOfMonth`, `dateDiff('day', …)`, `addDays`, and the like.
- Each function's template is checked by `ServerEdgeTests.FunctionCases`. One with no template falls back to the merge
  engine.

**Hooks from §2.4**

- Set operators are written `UNION DISTINCT`, `INTERSECT DISTINCT` and `EXCEPT DISTINCT`.
- `LIKE` is written without `ESCAPE`; `ILIKE` is native.
- `MaxCorrelationDepth` is 1. If conformance shows wrong rows from correlated `EXISTS`, which ClickHouse calls Beta,
  it becomes 0, and those queries fall back.

**To check against the server, adding a hook only where one is needed:**
- `ON 1 = 1`
- `OFFSET` without `LIMIT`
- `ORDER BY`/`LIMIT` around set operations (ClickHouse applies them per branch)
- `round` at halves
- `concat` with nulls
- comparisons whose results are selected as values (UInt8)

**Write-side rules:** none (`ScriptSyntax` and `ChangeRules` are left as they are). The engine refuses scripts for
read-only sources before splitting them (`QueryEngine` around line 360). Backslash escapes in scripts come with
writes (§5.7).

### 5.4 Provider (`ClickHouseSourceProvider`)

- **`ReadValue`** converts each value by its column's `GetDataTypeName`:
  - `ClickHouseDecimal` becomes `decimal`.
  - `Date`/`Date32` (read as `DateTime`) become `DateOnly`.
  - `IPAddress` becomes text.
  - Arrays, maps and tuples become JSON text.
  - Booleans selected as UInt8 become `bool`.
- **`CancelCommand`:** first check that the driver's `Cancel` stops the HTTP request. If it doesn't, `PrepareCommand`
  sets a query id and cancelling runs `KILL QUERY WHERE query_id = …` on a separate connection. `TimeoutTests`
  covers it.
- **`PrepareWriteAsync`** throws, though the engine never gets that far: the source is read-only and has no
  transactions.

### 5.5 Introspection and types

**What is read**
- Schemas are the server's databases, except `system`, `INFORMATION_SCHEMA` and `information_schema`.
- The default schema is the connection's database (`currentDatabase()`).
- Tables come from `system.tables`:
  - engines `View`, `MaterializedView` and `LiveView` are views;
  - `Dictionary` is left out.
- Columns come from `system.columns`; comments become descriptions.

**No keys from the server.** ClickHouse has no foreign keys, and its primary key is a sorting prefix, which isn't
unique.
- Navigation comes from the overlay: declared keys (`OverlayEntitySettings.Key`, "for views and tables without one")
  and relations.
- The sorting key is kept in the snapshot as information only.

**`ClickHouseTypeMapper`** first strips `LowCardinality(…)` and `Nullable(…)`, then maps:

| ClickHouse | Logical type |
|---|---|
| `Int8`, `Int16`, `UInt8` | Int16 |
| `Int32`, `UInt16` | Int32 |
| `Int64`, `UInt32` | Int64 |
| `UInt64` | Decimal(20,0) |
| `Float32`, `Float64` | Single, Double |
| `Decimal(p≤38,s)` | Decimal(p,s) |
| `String`, `FixedString(n)`, `Enum8/16` | Text |
| `Bool` | Boolean |
| `Date`, `Date32` | Date |
| `DateTime`, `DateTime64` | DateTime; DateTimeOffset where the column names a time zone |
| `UUID` | Guid |
| `IPv4`, `IPv6` | Text |
| `JSON`, `Array`, `Map`, `Tuple` | Json |
| `Int128`/`256`, wider decimals, `AggregateFunction`, `Variant`, … | Unknown |

### 5.6 Kind and connector (`ClickHouseKind`, `ClickHouseConnector`)

**`ClickHouseKind`**
- **Builder:** `ClickHouseConnectionStringBuilder`.
- **Fields:**
  - connection: Host (required), Port (default 8123), Database, Username, Password;
  - security: Protocol (`http` or `https`) and skipping the server's certificate check;
  - advanced: Compression, Timeout and Other.
- **Read-only:** `AlwaysReadOnly` is true, so the form shows the source as read-only. `Configure` sets `IsReadOnly` and
  `SupportsDml = false`.
- **Reserved keywords:** those of §5.2.
- **Raw example:** `Host=ch.example.com;Port=8443;Protocol=https;Database=analytics;Username=reader;Password=...`
- **Icon:** `database`.

**`ClickHouseConnector`**
- **Command line:** `CommandLineHelp(ConnectionString, "Host=localhost;Database=shop;Username=me")`. gdq's list
  becomes "…, sqlserver, clickhouse or excel".
- **Writable:** `-w` is refused.

### 5.7 Writes, if wanted later

Writes would cover:
- `INSERT`, which is atomic per block;
- lightweight `DELETE` (MergeTree tables);
- lightweight `UPDATE`, only on tables with block-number and block-offset columns.

Each change would be committed without a transaction, and counted with a `SELECT count()` first, since keys aren't
unique. The splitter would also need backslash escapes (`ScriptSyntax.BackslashEscapes`).

---

## 6. Testing ClickHouse

**The server**
- `tests/GalaxyData.Query.ContainerTests/servers.sh` gains `gdq-clickhouse`:
  - image `clickhouse/clickhouse-server:26.8`, overridden by `GDQ_CLICKHOUSE_IMAGE`;
  - `-p 58123:8123`;
  - `CLICKHOUSE_USER=gdq`, `CLICKHOUSE_PASSWORD=GdqTest2026`, `CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT=1`;
  - `--ulimit nofile=262144:262144`;
  - ready once `/ping` answers.
- `tests/Shared/TestServers.cs` gains `Host=127.0.0.1;Port=58123;Username=gdq;Password=GdqTest2026`, overridden by
  `GDQ_TEST_CLICKHOUSE`.

**`Servers.cs`**
- `ServerKind.ClickHouse` is added. The ternaries on `ServerKind` become a per-server record: the fixture suffix, how
  to create a database, and how to split a script.
- A database is made with `CREATE DATABASE gdq_test_<guid12>`, and its fixture script is split on `;`.
- The skip/fail rule is unchanged.

**Fixtures**
- `tests/fixtures/shop.clickhouse.sql`: MergeTree tables ordered by the shop's keys, `Nullable` where the shop has
  nulls, kept in step with `shop.sqlite.sql`.
- `kinds.clickhouse.sql`: one column per type, with a row of values and a row of nulls.
- The conformance run on ClickHouse adds an overlay declaring the shop's keys and relations, since the server has
  none.

**Tests that gain `ServerKind.ClickHouse`**
- `ConformanceTests`: per server, and split with PostgreSQL and with SQLite. The report must equal SQLite's;
  differences are documented, as for the other servers.
- `IntrospectionTests` and `ValueTests`, with `.ClickHouse` goldens.
- `ServerEdgeTests.FunctionCases`, with a `ClickHouseProbe`.
- `ServerTests`, and `CliTests` using `-s shop=clickhouse:…`.
- `DmlTests`: changes are refused before any SQL.

**Engine golden tests**
- `SqlReportTests.Statements.clickhouse.txt` is new, checked by hand.
- `SqlDialectTests` gains `clickhouse` cases for names, literals and placeholders.
- `TestDialects.All` includes ClickHouse; `Writable` doesn't.

**Web.Tests**
- `ConnectionKindTests` and `ConnectionInputTests` gain `clickhouse` cases: keywords, secrets, reserved settings.
- `ServerConnectionTests` tries a ClickHouse connection and reads its schema.
- The kinds snapshot gains ClickHouse.

---

## Milestones (each ends green and demonstrable)

| # | Scope | Exit criteria |
|---|---|---|
| C0 | §2.1 and §2.2: hooks in place of named dialects, then the dialect SDK made public; dialects still in the engine | All tests pass, **no snapshot changes**; `DmlGuard`, `SqlScriptSplitter`, `FederationPlanner` and `FederatedExecution` name no dialect |
| C1 | §2.3: dialects move to their provider projects; statics deleted; tests on `TestDialects` | All tests pass, no snapshot changes; the engine's architecture test passes |
| C2 | §3, §4.1 (not the DTO fields) and §4.2: `GalaxyData.Connectors` (first a pure move from Web), connectors, `GalaxyData.Connectors.BuiltIn`, `IAttachedSources`, `Configure`; web host and gdq on `ConnectorSet` | All tests pass; kinds, OpenAPI and gdq transcript snapshots unchanged; `Kinds.cs` gone; the hosts' architecture test passes |
| C3 | §4.1 DTO fields and §4.3: kind names and icons from the server | `npm test` and the e2e smoke pass; OpenAPI snapshot and `schema.d.ts` regenerated |
| C4 | §2.4 and §5.3: the hooks with their defaults, then `ClickHouseDialect` with its golden SQL and dialect tests | No earlier snapshot changes; the new `clickhouse` snapshots reviewed by hand |
| C5 | §5.1, §5.2, §5.4–5.6 and §6: provider, introspection, kind, connector, container tests | Container tests pass with none skipped; the ClickHouse conformance report equals SQLite's, with differences documented; tried by hand in the app and in gdq |
| C6 | Docs: new `docs/connectors.md` (what a connector holds, and adding one step by step); `docs/language.md` §1.2 and §9.3; `docs/server.md` Connections; README layout | `LanguageDocTests` pass; this plan's "Built in Cn" notes written |

**Built in C0.**
- **New hooks on `SqlDialect`:** `RunsInTheApplication`, `ChangeRules` and `ScriptSyntax`.
- **New records:** `ChangeScriptRules` and `ScriptSyntax`, in `Dml/ScriptRules.cs`.
- **The engine no longer compares dialects:**
  - `DmlGuard` and `SqlScriptSplitter` read the hooks.
  - `QueryEngine.KindOf` became `DmlGuard.KindOf(keyword, dialect)`, so `REPLACE` is an insert only in SQLite.
  - `FederationPlanner` keeps the merge dialect it is given.
  - `FederatedExecution`'s keys query is a `SqlSelect` written in the merge dialect.
- **`SqlDialect` opened:**
  - constructor `protected`;
  - the engine's hooks `protected internal`;
  - helpers `protected`;
  - `SqlCall` public, with an internal constructor;
  - `PagingStyle` and `ReturningStyle` public, with `SqliteLimitOffset` renamed `LimitOffsetNeedsLimit`.
- **The SQL nodes dialects build** are public, with `SqlExpr` closed to new kinds (`private protected` constructor; `Precedence` and `IsPredicate` internal).
- **Snapshots unchanged; 1899 tests pass.**
- **Found on the way:** `LanguageDocTests.EveryExampleRuns` failed before any change, in a checkout with CRLF line ends: its regex's `$` comes before `\n` only. It now allows a `\r`.

**Built in C1.**
- The four dialects are `public sealed` classes with `Instance`, in their provider projects. `SqlDialect`'s statics are gone.
- `GalaxyData.Query.Tests` references the provider projects, and finds dialects through `TestDialects`.
- `ArchitectureTests` checks two things:
  - the engine's assembly references no connector;
  - no line of its code, comments aside, names a database.
- Snapshots unchanged.

**Built in C2.**

*`GalaxyData.Connectors`:*
- `ConnectionKind`, its DTOs, `ConnectionKinds`, the `SourceConnector` family and `ConnectionStrings`, moved from Web.
- `ConnectionKind` gained `Configure`, `Flag`, `Things` and `Icon`.
- New types: `Connector`, `ConnectorContext`, `ConnectorSet` (which checks ids, and disposes the connectors) and `IAttachedSources`.
- The command line's types: `CommandLineSource`, `CommandLineTargets`, `CommandLineHelp` and `OpenedSource`.

*The connectors:*
- Each kind moved next to its provider, with a `<Db>Connector` holding gdq's open logic as it was.
- `ExcelConnector` owns `ExcelSourceProvider` and implements `IAttachedSources`. It took over the folder bookkeeping of `SourceConnections`, `SchemaReader` and `StoredConnections`.
- `GalaxyData.Connectors.BuiltIn` is the one list.

*Web:*
- DI takes the `ConnectorSet`; `Kinds.cs` and the five registrations are gone.
- `CatalogService`, `SchemaReader` and `SourceConnections` go through `Connector.Attached`.
- `StoredConnections.Source` calls `kind.Configure`.
- The csproj references Connectors, BuiltIn and DuckDb (the merge engine).

*gdq:*
- `SourceSpec.Parse(text, connectors)`, and `CliSources` over `OpenedSource`.
- The `--source` help is made from the connectors.
- The merge engine and connectors are made before parsing.

*Checks:*
- Architecture tests for Web and gdq check their compiled references: no connector but DuckDb, and no driver.
- Kinds, OpenAPI and transcript snapshots unchanged. 1903 tests.

**Built in C3.**
- `CatalogSourceDto.KindName`/`KindIcon` and `TreeNodeDto.SourceKindName`/`SourceIcon`, from the kind.
- The client's `sourceKinds` map and its `'excel'` test are gone.
- OpenAPI snapshot and `schema.d.ts` regenerated, and `CatalogApiTests` checks the names and icons.
- 784 client tests pass.

**Built in C4.**

*Hooks with defaults:* `SetOperator`, `LikeEscape`, `MaxCorrelationDepth`, and one more than planned, `CorrelatesValueSubqueries`, checked in `SqlBuilder` where outer columns are read and at the top of `SemiJoin`.

*`ClickHouseDialect`, as §5.3, but names are in double quotes (escaping `\` and `"`), which ClickHouse takes as
well as backticks.* The server showed what else it needed:
- **Decimal division** casts the dividend to `Decimal(38, 18)`, since ClickHouse gives the dividend's scale.
- **Modulo** casts both sides to `Decimal(38, 18)` when a decimal meets a whole number, since ClickHouse's `%` of them is wrong: `7 % 2.5` gives 0.7, `2.5 % 2` gives 0.
- **`coalesce` of decimals** casts its arguments to one type, since ClickHouse refuses different scales.
- **`substring`** clamps its start with `greatest`, since ClickHouse refuses 0 and counts negative starts from the end.
- **`toString`**:
  - of a decimal: `toDecimalString` at its scale (`250.00`);
  - of a date-time: without the zero fractions ClickHouse writes;
  - of a condition: `true`/`false`, not 1/0.

*Tests:*
- `SqlReportTests` writes `-- not written for <dialect>: <reason>` for a case a dialect refuses; 5 of 89 for ClickHouse. The other dialects' reports are unchanged.
- `SqlDialectTests` has ClickHouse rows.

**Built in C5.**

*The provider, and the driver's quirks:*
- `ClickHouse.Driver` 1.5.0.
- **Settings:** `ClickHouseSourceProvider.Settings` are set on every connection and command.
- **Connections:** one `ClickHouseDataSource` per connection string, on an HTTP client of the provider's. Its handler turns an `HttpRequestException` into a `ClickHouseUnreachableException`, a `DbException`, as the engine expects of a failing source.
- **Parameters** carry their ClickHouse types. A decimal with no precision gets its value's scale.
- **`ReadValue`** converts `ClickHouseDecimal`, network addresses, and date-times of a column with a zone.
- **Cancelling** stops the HTTP request and runs `KILL QUERY ... ASYNC` by query id. The server goes on with a query whose client has gone; ClickHouse allows `KILL` under `readonly=2`.
- **Writes:** `PrepareWriteAsync` refuses them.

*Introspection:*
- From `system.tables` and `system.columns`, with the sorting key as an index that isn't unique.
- Enums, network addresses, JSON and unknown types are read as text (`ReadAs`).
- **Changed from §5.5:** a source is the connection's database alone, and other databases only when `IncludeSchemas` names them. Reading every database leaked other databases' tables into a source, and other databases are other sources, as for the other servers.

*The kind:*
- Keywords canonical as ClickHouse.Driver names them, and `set_` settings.
- Unknown keywords and values the driver would ignore are refused.
- The language's settings, sessions and `ReadStringsAsByteArrays` are reserved.
- Always read-only, with no options.

*Tests:*
- `servers.sh` starts `clickhouse/clickhouse-server:26.8`, with `shop.clickhouse.sql` and `kinds.clickhouse.sql` fixtures. These are split at semicolons, so they have none elsewhere, comments included.
- `ClickHouseShop.Keyed` declares the shop's keys and relations.
- ClickHouse runs in:
  - `ConformanceTests`: all four, including splits with PostgreSQL, SQL Server, SQLite and DuckDB in every bind-join mode;
  - `ServerEdgeTests`: the 79 function cases, and eight semantic tests;
  - `IntrospectionTests`, `ValueTests`, and three `ServerTests` (parameter limit, timeout, cancelling on the server).
- `ClickHouseTests` covers:
  - changes refused, configured or not;
  - the merge engine running what ClickHouse can't write;
  - an unreachable server;
  - the source's database;
  - the settings;
  - gdq.
- In Web.Tests:
  - kinds, inputs and the API with ClickHouse rows;
  - `ServerConnectionTests` tries a ClickHouse connection and reads its database.
- **Tried in the published app:**
  1. a ClickHouse connection made by its form, tried ("Connected to ClickHouse 26.8.20.9, database ...") and read;
  2. the overview naming its kind;
  3. a query run in ClickHouse whole, with explain;
  4. the grid read-only.

  No errors in the console or the log.

**Built in C6.**
- `docs/connectors.md` (new).
- `docs/language.md`: §1.1, §1.2, §2.1, §9.3, §11 and §13.
- `docs/server.md`: the Connections kinds table, and read-only.
- README: the layout, tests and documentation.

**Results.** 2,036 .NET tests (from 1,899) and 784 client tests pass, none skipped, with PostgreSQL, SQL Server and ClickHouse running. The end-to-end smoke test passes: 16 of 16.

**Known limitations.**
- **ClickHouse is read-only (§5.7).** It has no keys or relations of its own, so to follow links, declare them in the overlay.
- **Some subqueries run in the merge engine, over the rows they read.** A subquery that gives a value and reads the query around it is the case: `first()` of a navigation, and counts or sums over one. On large tables that is slow. Filters, top-N and bind joins are still pushed down.
- **ClickHouse's whole numbers wrap around when they overflow;** decimals wider than 38 digits, and `Int128`/`Int256`, are read as text or doubles.
- **Excel needs the merge engine to be a `DuckDbMergeEngine`.** `ExcelConnector` checks this, and the DuckDb-to-Excel InternalsVisibleTo stays.
- **The public dialect SDK is new API.** Changing it would break connectors outside the repository, of which there are none yet.

---

## Verification

- **After each milestone:**
  - Run `dotnet build GalaxyData.slnx` and `dotnet test --solution GalaxyData.slnx`.
  - Check that `git status -- '**/Snapshots/**'` shows only the snapshots named in the milestone's exit criteria.
- **Container tests:**
  - `wsl -d Alpine -u root sh tests/GalaxyData.Query.ContainerTests/servers.sh up` starts PostgreSQL, SQL Server and
    ClickHouse.
  - Run the container tests; none may be skipped.
  - If tests skip, port 51433 may be reserved by Windows; see the project's notes.
- **Client:** `npm test` in `src/client`, then `npm run e2e` in `tests/e2e`. Set `MSBUILDCOPYTASKPARALLELISM=1` on
  Windows.
- **gdq by hand:**
  - `dotnet run --project src/GalaxyData.Query.Cli -- run -s ch=clickhouse:"Host=127.0.0.1;Port=58123;Username=gdq;Password=GdqTest2026;Database=<db>" "ch.orders.where(total > 50)"`
  - A query joining `ch` with `-s shop=sqlite:tests/fixtures/shop.sqlite.sql`, with `explain` showing the fragments.
  - `-w ch` is refused.
- **In the app:**
  1. Add a ClickHouse connection, by form and as a connection string, then try it and read its schema.
  2. Browse, run a query, and add a dashboard widget on it.
  3. Check that edits are refused.
  4. Declare a key and a relation in the overlay, and follow the navigation.

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| The public dialect SDK widens the library's API; changing it breaks connectors outside the repository | Only what dialects need is public: nodes with get-only properties, `SqlCall` with an internal constructor, `SqlExpr` closed to new kinds, hooks `protected internal`. Every connector is in this repository today. |
| The refactor changes SQL or behaviour | C0–C2 are held to unchanged snapshots for four dialects, the kinds form, OpenAPI and the gdq transcript, plus the conformance suites. gdq's open logic moves over as it is. |
| A host keeps naming providers through transitive references | The architecture tests in §4.4 |
| Excel depends on DuckDb internals (InternalsVisibleTo) and on the merge engine being a `DuckDbMergeEngine` | Kept and stated: `ExcelConnector` checks the context's merge engine. Decorating the merge engine would need Excel's attention. |
| Disposal order (`ExcelSourceProvider` before the merge engine) | Owned by `ExcelConnector`, disposed with the `ConnectorSet`, which is created after the merge engine. No DI registration of the provider of its own. |
| ClickHouse semantics: outer-join defaults, empty aggregates, Float division, byte-counting text functions, set-operator defaults | Per-command settings (§5.2), UTF-8 functions, explicit `DISTINCT`, and conformance against SQLite's report, with differences documented |
| Correlated subqueries are Beta in ClickHouse, one level deep | `MaxCorrelationDepth` with the fallback to the merge engine; it can drop to 0 |
| A ClickHouse account whose profile is `readonly=1` can't take the per-command settings | Trying the connection reports it; documented |
| Driver quirks: decimal inference, a socket pool per connection, `ServerVersion` throwing, no row counts | Explicit parameter types, `ClickHouseDataSource`, a probe query, read-only |
| Large ClickHouse tables are pulled into DuckDB when a query falls back | Filters, top-N and bind joins are already pushed down; explain shows the fragments; documented in `docs/connectors.md` and `language.md` §9.3 |
| `Connector` (the plug-in) is easily confused with `SourceConnector`/`ProviderConnector` (connection openers) | Doc comments, and `docs/connectors.md` defining both |
