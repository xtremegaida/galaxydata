# Connectors

Each kind of source GalaxyData reads (SQLite, DuckDB, PostgreSQL, SQL Server, folders of Excel workbooks and
ClickHouse) is a **connector**: a project of its own that holds everything particular to its database. The engine
(`GalaxyData.Query`), the application (`GalaxyData.Web`) and the command line (`gdq`) name no database; they take
the connectors from one list. The one database they do name is DuckDB, as the **merge engine** that runs what
combines sources, which the hosts configure.

This guide says what a connector holds, how to add one, and what is particular to ClickHouse. The query language is
in [language.md](language.md); connections, as administrators make them, in [server.md](server.md#connections).

## What a connector holds

| Part | Type | What it does |
|---|---|---|
| SQL dialect | `SqlDialect` (`GalaxyData.Query.Sql`) | How the database spells SQL: names, literals, types, paging, each language function, and what it can't write |
| Provider | `SourceProvider` (`GalaxyData.Query.Execution`) | The engine's view of the database: its dialect and schema reader, how values cross ADO.NET each way, how statements are readied and stopped |
| Schema reader | `ISchemaIntrospector`, and a type mapper | Tables, views, columns and their logical types, keys and foreign keys |
| Kind of connection | `ConnectionKind` (`GalaxyData.Connectors`) | The application's form for it (fields, options), its connection-string keywords, which are secrets or paths, read-only, how a connection is made and tried |
| Connector | `Connector` (`GalaxyData.Connectors`) | Ties the provider and the kind together, says how `gdq` opens a source of the kind, and owns what they share |

A `Connector` (the plug-in) isn't a `SourceConnector`, which opens connections with one connection string (a
connection pool, or a data source) for the application.

```text
GalaxyData.Query               the engine, and the dialect SDK (SqlDialect, the SQL nodes dialects build)
   ^
GalaxyData.Connectors          Connector, ConnectionKind and its form, SourceConnector, ConnectorSet
   ^
GalaxyData.Query.<Db>          one connector: dialect, provider, schema reader, types, kind, connector; and its driver
   ^
GalaxyData.Connectors.BuiltIn  BuiltInConnectors: the list the application and gdq come with
   ^
GalaxyData.Web, gdq            the hosts
```

Architecture tests keep it so: the engine references no connector, and the hosts none but DuckDB's (the merge
engine), and no database driver.

## Adding a connector

1. **The project.** Make `src/GalaxyData.Query.<Db>`, referencing `GalaxyData.Connectors` and the database's ADO.NET
   driver (its version in `Directory.Packages.props`), and add it to `GalaxyData.slnx`.

2. **The dialect.** Derive `public sealed class <Db>Dialect : SqlDialect`, with a static `Instance`. The engine calls
   the dialect's `protected internal` members; a dialect overrides them as `protected`, building expressions of the
   public SQL nodes (`SqlBinary`, `SqlCast`, `SqlCase`, `SqlLiteral`, `SqlFunctionCall`, `SqlTemplate`, ...) with the
   base class's helpers (`Call`, `Cast`, `Template`, `Like`, `Int`, ...).
   - **Must have:** `Name`, `ProviderKind`, `MaxParameters`, `TypeName` (the type a cast names), `WriteBinary`, and
     `Function`, which writes each language function (`FunctionId`), or gives null for one the database can't run.
   - **Names and values:** `QuoteIdentifier`, `IsBare`, `Placeholder`, `ParameterName`, `WriteQuoted`,
     `WriteCharacter`, `WriteTemporal`, `WriteGuid`, `WriteLiteral`, `BooleanLiteral`, `MaxNameLength`.
   - **Semantics:** `Paging`, `NullOrdering`, `ConcatOperator`, `HasBooleanValues`, `Divide`, `Modulo`, `Compare`,
     `SortKey`, `Aggregate`, `TextLength`, `ComparesExactly` (which keys may fetch rows by key).
   - **What it can write:** `SetOperator`, `LikeEscape`, `MaxCorrelationDepth` (how far out a subquery may read),
     `CorrelatesValueSubqueries` (whether a scalar or `IN` subquery may read the query around it).
   - **Changes:** `Returning`, `AcceptsIdentityValues`, `RowCountOfChange`, `InsertedIdentity`, `KeyEquals`,
     `ComparesOriginal`; and for scripts people edit, `ScriptSyntax` (how its scripts quote and end statements),
     `ChangeRules` (what else keeps a statement from running) and `RunsInTheApplication`.

   **What the dialect can't write runs in the merge engine.** A function it gives null for, or anything it throws
   `NotSupportedException` for while the SQL is written, makes the engine run that part in DuckDB, over rows the
   source gives. SQL the database refuses when it runs isn't caught that way: it fails the query. So every limit of
   the database belongs in the dialect.

3. **The provider.** Derive `SourceProvider`: `ProviderKind`, `Dialect`, `Introspector`, and as the database needs
   them, `PrepareConnectionAsync` and `PrepareCommand` (settings the language's meaning depends on, such as UTC),
   `BindParameter` (parameters of the right type), `ReadValue` (values the logical types' converter doesn't know),
   `CancelCommand`, `PrepareWriteAsync` and `PrepareCommitAsync`. Report a database that can't be reached as a
   `DbException`, as the engine expects of a source that fails.

4. **The schema reader.** Implement `ISchemaIntrospector` and a type mapper, from the database's type names to
   `ScalarType`s. Read tables ordered by schema and name, columns by position. A column of a type the language has
   no values for is `Unknown`, with `ReadAs` naming the type the source's SQL reads it as (text, usually).

5. **The kind of connection.** Derive `ConnectionKind`: `Id` (the provider's kind), `DisplayName`, `Icon`, `Fields`
   and `Options` (the form, its keys the driver's keywords as its connection string builder writes them), `NewBuilder`,
   and as needed `Restrict` (what read-only sets), `Reserved` (keywords that are the application's), `Normalize` and
   `Parse` (for a builder that doesn't name keywords itself), `Configure` (the engine's `SourceInfo` for a connection,
   from its options), `AlwaysReadOnly`, `Connector` and `Unpooled`, and `FoundAsync` (what trying a connection says).
   The client draws the form from what the kind describes: it needs no code of its own.

6. **The connector.** Derive `Connector`: `Provider`, `Kind`, and for `gdq`, `CommandLine` (whether a target is
   files, a connection string or a folder) and `OpenAsync`, usually with `OpenDatabaseAsync`, `Create` (a connection
   string the driver refuses is a `FormatException`) and `ExistingFile`. A connector whose sources the provider opens
   itself, with no connection string (a folder of workbooks), implements `IAttachedSources` too. A connector that
   needs the merge engine gets it from the `ConnectorContext`.

7. **Plug it in.** Add the project to `GalaxyData.Connectors.BuiltIn.csproj`, and a line to
   `BuiltInConnectors.Create`: the order there is the order the application lists kinds in. `gdq` lists them by their
   targets: files, connection strings, folders.

8. **Test it.**
   - The engine's golden SQL: add the dialect to `TestDialects` (and `Writable`, if it can be changed), and accept
     its `SqlReportTests.Statements.<kind>.txt` after reading it through; `SqlDialectTests` for names and literals.
   - Against the database: the conformance set (`Conformance`, which must give the rows SQLite gives, in the
     database's SQL and through the merge engine), the function cases (`ServerEdgeTests.FunctionCases`), a column of
     each type read and sent back (`ValueTests`), and its schema (`IntrospectionTests`). For a server, add it to
     `servers.sh` and `Servers`, with a `shop` and a `kinds` fixture in `tests/fixtures`.
   - The application: the kinds' form (`ConnectionApiTests.TheKindsDescribeTheirForms`), its keywords and secrets
     (`ConnectionKindTests`), and a connection tried and read (`ServerConnectionTests`).
   - Document what the database leaves different in [language.md](language.md) (section 9.3), and its kind in
     [server.md](server.md#connections).

## ClickHouse

`GalaxyData.Query.ClickHouse` reads ClickHouse 26.3 or later over HTTP, with ClickHouse.Driver.

- **Read-only.** ClickHouse has no transactions, and changes rows later, as mutations; changes to its rows aren't
  planned, scripts for it aren't run, and its statements run with `readonly=2`.
- **A source is a database.** The connection's database is the source's default schema, and the only one read
  unless an introspection names others.
- **No keys or relations.** A ClickHouse primary key is the start of the sorting key, and needn't be unique: tables
  are read with none (the sorting key as an index that isn't unique). Declare keys and relations in the overlay to
  follow links.
- **Settings on every statement.** Outer joins give nulls (`join_use_nulls`), aggregates of no rows are null
  (`aggregate_functions_null_for_empty`), times are UTC (`session_timezone`), and a statement whose client has gone
  is stopped. The login must be allowed to change settings.
- **What runs in the merge engine.** A subquery may read columns of the query it is in only to test whether rows
  exist (`EXISTS`): ClickHouse's subqueries that give values read null where they find no rows (a count of none is
  null), so `first()` of a navigation, and counts and sums over one, run in the merge engine, over the rows they read.
  A filter, a sort with a limit and a join's keys are still sent to ClickHouse.
- **Written as the language means.** Text functions use the `UTF8` forms (ClickHouse's count bytes), set operations
  name `DISTINCT`, decimals divide with 18 places, a decimal and a whole number take a remainder as two decimals
  (ClickHouse's own `%` of them is wrong), `substring` takes a start before the first character, and `toString`
  writes decimals with their scale and date-times without fractions they don't have.
- **Parameters** are sent with their ClickHouse types, and at most 1,000 in a statement: keys that are text past
  that are fetched in full.
- **Stopping a statement** stops its HTTP request, and kills the statement on the server by its query id.
- **Values.** Decimals are read as `decimal` (a wider one as a double); `UInt64` is a decimal(20,0); `Int128` and
  wider, arrays, maps and tuples are read as text; enums and network addresses as text; a date-time with a zone of
  its own is an instant.
