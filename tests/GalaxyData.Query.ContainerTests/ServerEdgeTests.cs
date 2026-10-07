using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Cli;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.IntegrationTests.Execution;
using GalaxyData.Query.Providers;
using GalaxyData.Query.Types;
using Microsoft.Data.SqlClient;
using Npgsql;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.ContainerTests;

/// <summary>
/// Edges of the server sources: every function against SQLite's results, the same values in each server's SQL and
/// in the merge engine, exotic types, parameters, bind joins, names, introspection, errors and connections, and the
/// command line. Where the language leaves results to the database (decimal division's digits, toString of dates and
/// doubles, division by zero, char(n) padding, surrogate pairs), the tests say what each gives.
/// </summary>
public sealed class ServerEdgeTests(Servers servers)
{
   private static readonly QueryEngineOptions NoPushDown = new() { PushDown = false };

   #region Fixtures

   /// <summary>A table of awkward values, the same in every database: trailing spaces, patterns, nulls, month ends, offsets, guids.</summary>
   private const string ProbeRows = """
      INSERT INTO p (id, s, t, n, m, f, dm, d, ts, b, g, w, z) VALUES
         (1, 'Acme Ltd', 'Ltd', 7, 3, 2.5, 2.50, '2026-01-31', '2026-01-31 10:30:00', '1', '00000000-0000-0000-0000-000000000002', 2000000000, '2026-01-30 01:00:00+05:00'),
         (2, 'abX', 'ab ', -7, 3, -2.5, -2.50, '2024-02-29', '2024-02-29 23:59:59', '0', '01000000-0000-0000-0000-000000000001', 2000000000, '2026-01-31 23:30:00-05:00'),
         (3, '50% off_[x]', '', 7, -3, 0.5, 0.50, '2026-03-31', '2026-03-31 00:00:00', NULL, '00000000-0000-0000-0000-000000000003', 2000000000, '2026-03-01 00:30:00+01:00'),
         (4, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL),
         (5, 'x', 'X', 0, 5, 1.5, 1.55, '2026-12-31', '2026-12-31 23:00:00', '1', 'ffffffff-0000-0000-0000-000000000000', 2000000000, '2026-12-31 22:00:00-03:00'),
         (6, 'ab  ', 'b ', 2, 3, 3.5, 3.25, '2025-01-15', '2025-01-15 12:00:00', '0', '00000000-0000-0000-0000-000000000001', 1, '2026-01-29 21:00:00+00:00'),
         (7, 'Xab', 'ab ', 1, 2, -1.5, 0.05, '2025-06-30', '2025-06-30 06:15:45', '1', '00000000-0001-0000-0000-000000000000', 1, '2026-02-01 02:00:00+00:00');
      """;

   private static readonly string PostgresProbe = """
      CREATE TABLE p (id integer PRIMARY KEY, s varchar(40), t varchar(40), n integer, m integer, f double precision, dm numeric(10,2), d date,
         ts timestamp, b boolean, g uuid, w integer, z timestamptz);
      """ + "\n" + ProbeRows + "\n" + """
      CREATE SCHEMA "odd schema";
      CREATE TABLE "odd schema"."Mixed Case" ("Id" integer PRIMARY KEY, "select" text, "a""b" text, "c]d" text, "with space" integer, "order" integer, "Ünï" text);
      INSERT INTO "odd schema"."Mixed Case" VALUES (1, 'sel', 'quote', 'bracket', 10, 20, 'u');
      CREATE TABLE "dot.table" (id integer PRIMARY KEY, v text);
      INSERT INTO "dot.table" VALUES (1, 'dot');
      CREATE TYPE color AS ENUM ('red', 'green');
      CREATE TABLE paint (c color PRIMARY KEY, n integer);
      INSERT INTO paint VALUES ('red', 1), ('green', 2);
      ANALYZE;
      """;

   private static readonly string SqlServerProbe = """
      GO
      CREATE TABLE p (id int CONSTRAINT pk_p PRIMARY KEY, s varchar(40), t varchar(40), n int, m int, f float, dm decimal(10,2), d date,
         ts datetime2, b bit, g uniqueidentifier, w int, z datetimeoffset);
      """ + "\n" + ProbeRows + "\n" + """
      GO
      CREATE SCHEMA [odd schema];
      GO
      CREATE TABLE [odd schema].[Mixed Case] ([Id] int CONSTRAINT pk_mixed PRIMARY KEY, [select] nvarchar(10), [a"b] nvarchar(10), [c]]d] nvarchar(10), [with space] int, [order] int, [Ünï] nvarchar(10));
      INSERT INTO [odd schema].[Mixed Case] VALUES (1, N'sel', N'quote', N'bracket', 10, 20, N'u');
      CREATE TABLE [dot.table] (id int CONSTRAINT pk_dot PRIMARY KEY, v nvarchar(10));
      INSERT INTO [dot.table] VALUES (1, N'dot');
      """;

   private static readonly string SqliteProbe = """
      CREATE TABLE p (id INTEGER PRIMARY KEY, s VARCHAR(40), t VARCHAR(40), n INTEGER, m INTEGER, f REAL, dm DECIMAL(10,2), d DATE,
         ts DATETIME, b BOOLEAN, g UUID, w INTEGER, z DATETIMEOFFSET);
      """ + "\n" + ProbeRows;

   /// <summary>PostgreSQL types the kinds fixture doesn't have.</summary>
   private const string PostgresExotic = """
      CREATE EXTENSION IF NOT EXISTS citext;
      CREATE TYPE pair AS (a integer, b text);
      CREATE TYPE lvl AS ENUM ('lo', 'hi');
      CREATE DOMAIN lvl_d AS lvl;
      CREATE DOMAIN d1 AS integer;
      CREATE DOMAIN d2 AS d1;
      CREATE TABLE exo (id integer PRIMARY KEY, num numeric, dinf date, tsinf timestamp, ttz timetz, rng int4range, comp pair, big text,
         ci citext, lv lvl_d, dd d2, tm time, tzinf timestamptz);
      INSERT INTO exo VALUES
         (1, 'NaN', 'infinity', 'infinity', '10:00:00+02', '[1,5)', ROW(1, 'a'), repeat('x', 1000000), 'Mixed', 'hi', 5, '24:00:00', 'infinity'),
         (2, 1e40, '-infinity', '-infinity', '11:00:00-03', 'empty', ROW(2, 'b'), 'short', 'other', 'lo', 6, '23:59:59.999999', '-infinity'),
         (3, 'Infinity', '2026-01-01', '2026-01-01 00:00', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, '2026-01-01 00:00+00'),
         (4, 0.12345678901234567890123456789012, '2300-01-01', '2300-01-01 00:00', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, '2300-01-01 00:00+00');
      ANALYZE;
      """;

   /// <summary>SQL Server types and shapes the kinds fixture doesn't have.</summary>
   private const string SqlServerExotic = """
      GO
      CREATE TABLE exo (id int CONSTRAINT pk_exo PRIMARY KEY, geo geography, sp int SPARSE NULL, sn sysname NULL, cc AS (id * 2) PERSISTED,
         u8 varchar(20) COLLATE Latin1_General_100_CS_AS_SC_UTF8, big nvarchar(4000), bigv varchar(8000), emoji nvarchar(10), dmin datetime2, dmax datetime2, q varchar(10), dtox datetimeoffset);
      INSERT INTO exo (id, geo, sp, sn, u8, big, bigv, emoji, dmin, dmax, q, dtox) VALUES
         (1, geography::Point(47.65, -122.34, 4326), 5, N'name', N'日本', REPLICATE(N'a', 4000), REPLICATE('b', 8000), N'😀', '0001-01-01', '9999-12-31 23:59:59.9999999', 'plain', '9999-12-31 23:59:59.9999999 +00:00'),
         (2, NULL, NULL, NULL, 'plain', REPLICATE(N'a', 3999) + N' ', 'c', N'a', '1900-01-01', '2262-04-12', 'a?b', '0001-01-01 00:00:00 +00:00');
      CREATE TABLE sps (id int CONSTRAINT pk_sps PRIMARY KEY, a int SPARSE NULL, b nvarchar(10) SPARSE NULL, cs xml COLUMN_SET FOR ALL_SPARSE_COLUMNS);
      INSERT INTO sps (id, a, b) VALUES (1, 5, N'x');
      """;

   /// <summary>Keys, constraints and indexes to introspect.</summary>
   private const string PostgresKeys = """
      CREATE SCHEMA sales;
      CREATE TABLE sales.hdr (a integer, b integer, PRIMARY KEY (a, b), CONSTRAINT uq_hdr_ba UNIQUE (b, a));
      CREATE TABLE sales.det (id integer PRIMARY KEY, x integer, y integer, CONSTRAINT fk_det FOREIGN KEY (y, x) REFERENCES sales.hdr (b, a));
      CREATE TABLE xref (id integer PRIMARY KEY, a integer, b integer, CONSTRAINT fk_xref FOREIGN KEY (a, b) REFERENCES sales.hdr (a, b) DEFERRABLE INITIALLY DEFERRED);
      CREATE TABLE ne (id integer PRIMARY KEY, c integer, CONSTRAINT fk_ne FOREIGN KEY (c) REFERENCES customers (id) NOT ENFORCED);
      CREATE TABLE idt (a integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, b serial, c integer DEFAULT 5, e integer GENERATED ALWAYS AS (c * 2) STORED);
      CREATE UNIQUE INDEX ux_idt_c ON idt (c) WHERE c > 0;
      CREATE UNIQUE INDEX ux_idt_expr ON idt ((c + 1));
      CREATE TABLE meas (id integer, at date, v integer) PARTITION BY RANGE (at);
      CREATE TABLE meas_2026 PARTITION OF meas FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
      INSERT INTO meas VALUES (1, '2026-05-01', 10), (2, '2026-06-01', 20);
      CREATE MATERIALIZED VIEW mv AS SELECT id, name FROM customers;
      COMMENT ON MATERIALIZED VIEW mv IS 'mv comment';
      ANALYZE;
      """;

   private const string SqlServerKeys = """
      GO
      CREATE SCHEMA sales;
      GO
      CREATE TABLE sales.hdr (a int NOT NULL, b int NOT NULL, CONSTRAINT pk_hdr PRIMARY KEY (a, b), CONSTRAINT uq_hdr_ba UNIQUE (b, a));
      CREATE TABLE sales.det (id int CONSTRAINT pk_det PRIMARY KEY, x int, y int, CONSTRAINT fk_det FOREIGN KEY (y, x) REFERENCES sales.hdr (b, a));
      CREATE TABLE xref (id int CONSTRAINT pk_xref PRIMARY KEY, a int, b int, CONSTRAINT fk_xref FOREIGN KEY (a, b) REFERENCES sales.hdr (a, b));
      ALTER TABLE xref NOCHECK CONSTRAINT fk_xref;
      CREATE TABLE idt (a int IDENTITY(1, 1) CONSTRAINT pk_idt PRIMARY KEY, c int CONSTRAINT df_idt_c DEFAULT 5, e AS (c * 2) PERSISTED, rv rowversion);
      CREATE UNIQUE INDEX ux_idt_c ON idt (c) WHERE c > 0;
      """;

   /// <summary>A unique index SQL Server doesn't maintain any more, and a row that breaks it.</summary>
   private const string SqlServerDisabledIndex = """
      GO
      CREATE UNIQUE INDEX ux_addr_cust ON addresses (customer_id);
      ALTER INDEX ux_addr_cust ON addresses DISABLE;
      INSERT INTO addresses (id, customer_id, line1, city) VALUES (3, 1, N'2 Side St', N'Cape Town');
      """;

   /// <summary>Keys of each type, in DuckDB, to fetch the probe table's rows by.</summary>
   private const string DuckDbKeys = """
      CREATE TABLE keys (k INTEGER, s VARCHAR, g UUID, d DATE, dm DECIMAL(10,2), ts TIMESTAMP, z TIMESTAMPTZ);
      INSERT INTO keys VALUES
         (1, 'Acme Ltd', '00000000-0000-0000-0000-000000000002', '2026-01-31', 2.50, '2026-01-31 10:30:00', '2026-01-29 20:00:00+00'),
         (2, 'abX', '01000000-0000-0000-0000-000000000001', '2024-02-29', -2.50, '2024-02-29 23:59:59', '2026-02-01 04:30:00+00'),
         (3, 'nope', 'ffffffff-ffff-ffff-ffff-ffffffffffff', '1999-01-01', 9.99, '1999-01-01 00:00:00', '1999-01-01 00:00:00+00'),
         (4, 'Xab', '00000000-0001-0000-0000-000000000000', '2025-06-30', 0.05, '2025-06-30 06:15:45', '2026-02-01 02:00:00+00'),
         (5, 'x', 'ffffffff-0000-0000-0000-000000000000', '2026-12-31', 1.55, '2026-12-31 23:00:00', '2027-01-01 01:00:00+00');
      CREATE TABLE wants (c VARCHAR);
      INSERT INTO wants VALUES ('red'), ('blue');
      CREATE TABLE picks AS SELECT i AS n FROM range(1001, 1005) AS t(i);
      """;

   private Task<ServerDatabase> ProbeDatabaseAsync(ServerKind server) =>
      servers.DatabaseAsync(server, "shop", server == ServerKind.Postgres ? PostgresProbe : SqlServerProbe);

   private async Task<TestSources> ProbeSourcesAsync(ServerKind server) => await (await ProbeDatabaseAsync(server)).SourcesAsync();

   private static Task<TestSources> SqliteProbeAsync() =>
      new TestSources().AddSqliteAsync("shop", IntegrationTests.Fixtures.Sql("shop.sqlite.sql") + "\n" + SqliteProbe);

   private async Task<TestSources> ExoticSourcesAsync(ServerKind server) =>
      await (await servers.DatabaseAsync(server, "kinds", server == ServerKind.Postgres ? PostgresExotic : SqlServerExotic)).SourcesAsync("k");

   private Task<ServerDatabase> KeysDatabaseAsync(ServerKind server) =>
      servers.DatabaseAsync(server, "shop", server == ServerKind.Postgres ? PostgresKeys : SqlServerKeys);

   #endregion

   #region Helpers

   private static CancellationToken Token => TestContext.Current.CancellationToken;

   /// <summary>A query's rows as text, one line each; a failure as <c>!ExceptionType: message</c>.</summary>
   private static async Task<string> TextAsync(QueryEngine engine, string query, QueryParameters? parameters = null)
   {
      try
      {
         await using QueryResult result = await engine.ExecuteAsync(new QueryRequest(query) { Parameters = parameters ?? new QueryParameters() }, Token);
         IReadOnlyList<object?[]> rows = await result.ToListAsync(Token);
         return TestSources.Format(rows, result.Schema).ReplaceLineEndings("\n").TrimEnd('\n');
      }
      catch (Exception e) when (e is not OperationCanceledException)
      {
         return $"!{e.GetType().Name}: {e.Message}";
      }
   }

   private static string Lines(params string[] lines) => string.Join("\n", lines);

   /// <summary>The rows on the server, in its SQL and through the merge engine, are the rows SQLite gives.</summary>
   private async Task MatchesSqliteAsync(ServerKind server, string query, QueryParameters? parameters = null)
   {
      await using TestSources sources = await ProbeSourcesAsync(server);
      await using TestSources sqlite = await SqliteProbeAsync();
      string expected = await TextAsync(sqlite.Engine(), query, parameters);
      string direct = await TextAsync(sources.Engine(), query, parameters);
      string merged = await TextAsync(sources.Engine(options: NoPushDown), query, parameters);
      $"direct:\n{direct}\nmerged:\n{merged}".ShouldBe($"direct:\n{expected}\nmerged:\n{expected}", $"{server}: {query}");
   }

   /// <summary>The rows on the server, in its SQL and through the merge engine, are <paramref name="expected"/>.</summary>
   private static async Task GivesAsync(TestSources sources, string query, string expected, QueryParameters? parameters = null, bool merged = true)
   {
      string direct = await TextAsync(sources.Engine(), query, parameters);
      string viaMerge = merged ? await TextAsync(sources.Engine(options: NoPushDown), query, parameters) : expected;
      $"direct:\n{direct}\nmerged:\n{viaMerge}".ShouldBe($"direct:\n{expected}\nmerged:\n{expected}", query);
   }

   /// <summary>The server's SQL gives what the merge engine gives, and neither fails.</summary>
   private static async Task SameDirectAndMergedAsync(TestSources sources, string query)
   {
      string direct = await TextAsync(sources.Engine(), query);
      string merged = await TextAsync(sources.Engine(options: NoPushDown), query);
      (direct == merged && !direct.StartsWith('!')).ShouldBeTrue($"{query}\ndirect:\n{direct}\nmerged:\n{merged}");
   }

   /// <summary>The server's SQL gives what the merge engine gives, date-times to the microsecond (the merge engine keeps no more).</summary>
   private static async Task SameToTheMicrosecondDirectAndMergedAsync(TestSources sources, string query)
   {
      async Task<string> ReadAsync(QueryEngine engine)
      {
         await using QueryResult result = await engine.ExecuteAsync(new QueryRequest(query), Token);
         return string.Join("\n", (await result.ToListAsync(Token)).Select(r => string.Join(" | ", r.Select(Micro))));
      }
      static string Micro(object? value) => value switch
      {
         DateTime time => new DateTime(time.Ticks - time.Ticks % 10).ToString("O", CultureInfo.InvariantCulture),
         DateTimeOffset instant => new DateTimeOffset(instant.UtcTicks - instant.UtcTicks % 10, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture),
         _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
      };
      string direct = await ReadAsync(sources.Engine());
      (await ReadAsync(sources.Engine(options: NoPushDown))).ShouldBe(direct, query);
   }

   private static TheoryData<ServerKind, string, string> Both(params (string Name, string Query)[] cases)
   {
      TheoryData<ServerKind, string, string> data = [];
      foreach (ServerKind server in new[] { ServerKind.Postgres, ServerKind.SqlServer })
      {
         foreach ((string name, string query) in cases) { data.Add(server, name, query); }
      }
      return data;
   }

   private static async Task<SourceSchema> IntrospectAsync(ServerDatabase database, IntrospectionOptions? options = null)
   {
      await using DbConnection connection = database.Open();
      return await database.Provider.Introspector.IntrospectAsync(connection, options ?? IntrospectionOptions.Default, Token);
   }

   private static async Task<(int Exit, string Output, string Error)> GdqAsync(params string[] args)
   {
      using StringWriter output = new();
      using StringWriter error = new();
      int exit = await GdqApp.RunAsync(args, new StringReader(string.Empty), output, error, Token);
      return (exit, output.ToString(), error.ToString());
   }

   #endregion

   #region 1. Functions, operators, booleans, grouping and paging against the SQLite reference

   public static TheoryData<ServerKind, string, string> FunctionCases => Both(
      ("text-case-trim", "shop.p.select(id, lo: lower(s), up: upper(s), tr: trim(s), lt: ltrim(s), rt: rtrim(s)).orderBy(id)"),
      ("length-trailing-spaces", "shop.p.select(id, ls: length(s), lt: length(t)).orderBy(id)"),
      ("substring", "shop.p.select(id, a: substring(s, 2), b: substring(s, 2, 3), c: substring(s, 1, 0), e: substring(s, 0, 2)).orderBy(id)"),
      ("indexOf", "shop.p.select(id, i: indexOf(s, 'b'), j: indexOf(s, t)).orderBy(id)"),
      ("replace-left-right", "shop.p.select(id, r: replace(s, ' ', '_'), l: left(s, 2), rr: right(s, 2), whole: right(s, 50)).orderBy(id)"),
      ("startsWith-column", "shop.p.where(startsWith(s, t)).select(id).orderBy(id)"),
      ("endsWith-column", "shop.p.where(endsWith(s, t)).select(id).orderBy(id)"),
      ("contains-column", "shop.p.where(contains(s, t)).select(id).orderBy(id)"),
      ("icontains-column", "shop.p.where(icontains(s, t)).select(id).orderBy(id)"),
      ("like-escapes", @"shop.p.where(like(s, '50\\% off%') or like(s, 'a_X')).select(id).orderBy(id)"),
      ("like-brackets", "shop.p.where(like(s, '%[x]%')).select(id).orderBy(id)"),
      ("ilike-brackets", "shop.p.where(ilike(s, 'ACME%') or ilike(s, '%[X]')).select(id).orderBy(id)"),
      ("fixed-patterns", "shop.p.where(startsWith(s, '50%') or endsWith(s, '_[x]') or contains(s, '% o') or icontains(s, 'CME')).select(id).orderBy(id)"),
      ("concat-nulls", "shop.p.select(id, a: s + '|' + t, b: concat(s, t, n)).orderBy(id)"),
      ("round-double", "shop.p.select(id, r: round(f), r1: round(f, 1)).orderBy(id)"),
      ("round-decimal", "shop.p.select(id, r: round(dm), r1: round(dm, 1)).orderBy(id)"),
      ("abs-floor-ceiling", "shop.p.select(id, a: abs(n), fl: floor(f), ce: ceiling(f), fd: floor(dm), cd: ceiling(dm)).orderBy(id)"),
      ("power-sqrt-sign", "shop.p.select(id, pw: power(n, 2), sq: sqrt(abs(f)), sg: sign(n), sf: sign(f), sd: sign(dm)).orderBy(id)"),
      ("modulo-negatives-doubles", "shop.p.select(id, a: n % m, b: f % 2, c: dm % 2, e: n % 2.5).orderBy(id)"),
      ("int-division", "shop.p.select(id, a: n / m, b: f / m, c: w / 7).orderBy(id)"),
      ("negation", "shop.p.select(id, a: -n, b: -f, c: -dm).orderBy(id)"),
      ("coalesce-arithmetic", "shop.p.select(id, a: coalesce(n, 0) + 1, b: coalesce(dm, 0) * 2).orderBy(id)"),
      ("date-parts", "shop.p.select(id, y: year(d), mo: month(d), dd: day(d), h: hour(ts), mi: minute(ts), se: second(ts), dt: date(ts)).orderBy(id)"),
      ("date-buckets", "shop.p.select(id, w: startOfWeek(d), mo: startOfMonth(d), q: startOfQuarter(d), y: startOfYear(d), qn: quarter(d), wd: dayOfWeek(d), tw: startOfWeek(ts), tm: startOfMonth(ts), tq: startOfQuarter(ts), twd: dayOfWeek(ts)).orderBy(id)"),
      ("date-bucket-groups", "shop.p.groupBy(m: startOfMonth(d)).select(m, c: count()).orderBy(m)"),
      ("date-arithmetic", "shop.p.select(id, a: addDays(d, 1), b: addDays(ts, -1), c: daysBetween(d, toDate('2026-03-01')), e: daysBetween(ts, d)).orderBy(id)"),
      ("between-dates", "shop.p.where(between(d, toDate('2025-01-01'), toDate('2026-01-31'))).select(id).orderBy(id)"),
      ("conversions", "shop.p.select(id, i: toInt(f), l: toLong(dm), x: toDouble(n), c: toDecimal(f, 10, 2), s1: toString(n), s2: toString(dm), s3: toString(d), s4: toString(ts)).orderBy(id)"),
      ("toString-guid", "shop.p.select(id, s: toString(g)).orderBy(id)"),
      ("toString-double", "shop.p.select(id, s: toString(f / 4)).orderBy(id)"),
      ("text-to-values", "shop.orders.select(id, i: toInt(toString(id)), x: toDouble(toString(total)), a: toDate(toString(order_date)), b: toDateTime(toString(order_date))).orderBy(id)"),
      ("toBool-text", "shop.customers.select(name, b: toBool(iif(id == 1, 'yes', iif(id == 2, 'no', 'true')))).orderBy(name)"),
      ("toDouble-bool", "shop.p.where(b != null).select(id, a: toDouble(b)).orderBy(id)"),
      ("toLong-bool", "shop.p.where(b != null).select(id, a: toLong(b)).orderBy(id)"),
      ("toDecimal-bool", "shop.p.where(b != null).select(id, a: toDecimal(b), c: toDecimal(b, 5, 2)).orderBy(id)"),
      ("toInt-bool", "shop.p.where(b != null).select(id, a: toInt(b), c: toInt(n > 1)).orderBy(id)"),
      ("null-functions", "shop.p.select(id, a: coalesce(s, t, 'none'), b: nullif(n, 7), c: iif(n > 0, 'pos', 'other'), e: between(n, 0, 7), h: coalesce(b, false)).orderBy(id)"),
      ("in-and-not-in", "shop.p.where(n in [7, 0] or not (m in [3, 5])).select(id).orderBy(id)"),
      ("bool-values", "shop.p.select(id, b, nb: not b, gt: n > 1, eq: b == true, same: (n > 1) == b, both: b and n > 1, either: b or n > 1).orderBy(id)"),
      ("bool-where", "shop.p.where(b).select(id).orderBy(id)"),
      ("bool-where-not", "shop.p.where(not b).select(id).orderBy(id)"),
      ("bool-where-compare", "shop.p.where(b != true or b == null).select(id).orderBy(id)"),
      ("bool-group", "shop.p.groupBy(b).select(b, c: count()).orderBy(b)"),
      ("bool-distinct", "shop.p.select(b).distinct().orderBy(desc(b))"),
      ("bool-order", "shop.p.orderBy(b, desc(id)).select(id)"),
      ("bool-expression-key", "shop.p.groupBy(big: f > 1).select(big, c: count()).orderBy(big)"),
      ("bool-min-max", "shop.p.groupBy().select(mn: min(b), mx: max(b))"),
      ("bool-max-condition", "shop.p.groupBy(m).select(m, mx: max(f > 1)).orderBy(m)"),
      ("bool-countDistinct", "shop.p.groupBy().select(c: countDistinct(b), c2: countDistinct(n > 1))"),
      ("bool-union", "shop.p.select(id, k: n > 1).union(shop.p.select(id, k: b)).orderBy(id, k)"),
      ("bool-functions", "shop.p.select(id, x: iif(b, 'y', 'n'), t: toString(b), i: toInt(b), t2: toString(n > 1)).orderBy(id)"),
      ("aggregates", "shop.p.groupBy().select(c: count(), cn: count(n), cd: countDistinct(m), s: sum(n), sw: sum(w), a: avg(n), mn: min(d), mx: max(ts), mf: min(f))"),
      ("aggregates-empty", "shop.p.where(id > 100).groupBy().select(c: count(), s: sum(n), a: avg(n), mn: min(d), mx: max(s), sd: sum(dm))"),
      ("group-expression-keys", "shop.p.groupBy(k: n % 2, y: year(d)).select(k, y, c: count(), s: sum(f)).orderBy(k, y)"),
      ("having", "shop.p.groupBy(m).where(count() > 1 and sum(n) != 0).select(m, c: count()).orderBy(m)"),
      ("any-all-groups", "shop.p.groupBy(m).select(m, a: any(f > 1), l: all(f > 1)).orderBy(m)"),
      ("nested-correlated", "shop.customers.select(name, m: orders.max(order_lines.sum(qty * price)), n: orders.where(order_lines.any(qty > 1)).count()).orderBy(name)"),
      ("correlated-paging", "shop.customers.select(name, top: orders.orderBy(desc(total)).take(1).sum(total), second: orders.orderBy(desc(total)).skip(1).firstOrDefault().id).orderBy(name)"),
      ("sum-of-nothing", "shop.customers.select(name, s: addresses.sum(id), c: addresses.count()).orderBy(name)"),
      ("skip-alone", "shop.orders.orderBy(id).skip(1).select(id)"),
      ("distinct-take", "shop.orders.select(status).distinct().orderBy(status).take(2)"),
      ("distinct-skip", "shop.orders.select(status).distinct().skip(1).count()"),
      ("distinct-order-expression", "shop.orders.select(status).distinct().orderBy(length(status), status)"),
      ("union-take", "shop.orders.select(id).union(shop.addresses.select(id)).orderBy(desc(id)).take(3)"),
      ("take-in-union", "shop.orders.orderBy(desc(total)).take(2).select(id).union(shop.orders.orderBy(total).take(1).select(id)).orderBy(id)"),
      ("take-zero", "shop.orders.orderBy(id).skip(1).take(0).select(id)"),
      ("paging-over-nav", "shop.orders.orderBy(customer.name, id).skip(1).take(2).select(id, customer.name)"),
      ("paging-groups", "shop.orders.groupBy(status).select(status, n: count()).orderBy(desc(n), status).take(2)"),
      ("order-expression-take", "shop.orders.orderBy(desc(total * 2), id).take(2).select(id)"),
      ("firstOrDefault-none", "shop.orders.where(total > 1000).firstOrDefault()"),
      ("set-operations", "shop.orders.select(customer_id).except(shop.addresses.select(customer_id)).union(shop.orders.select(customer_id).intersect(shop.addresses.select(customer_id))).orderBy(customer_id)"),
      ("dto-parts", "shop.p.where(z != null).select(id, y: year(z), mo: month(z), dd: day(z), h: hour(z), dz: date(z), ad: addDays(z, 1), db: daysBetween(z, toDate('2026-03-01')), dt: toDateTime(z)).orderBy(id)"),
      ("dto-buckets", "shop.p.where(z != null).select(id, w: startOfWeek(z), mo: startOfMonth(z), q: startOfQuarter(z), y: startOfYear(z), qn: quarter(z), wd: dayOfWeek(z)).orderBy(id)"),
      ("dto-order", "shop.p.orderBy(z, id).select(id)"),
      ("dto-compare", "shop.p.where(z > toDateTime('2026-02-01 03:00')).select(id).orderBy(id)"),
      ("guid-order", "shop.p.orderBy(g, id).select(id)"),
      ("guid-min-max", "shop.p.groupBy().select(mn: min(g), mx: max(g))"),
      ("case-sensitive-equality", "shop.p.where(s == 'x' or s == 'XAB').select(id).orderBy(id)"));

   [Theory]
   [MemberData(nameof(FunctionCases))]
   public async Task FunctionMatchesSqlite(ServerKind server, string probe, string query)
   {
      _ = probe;
      await MatchesSqliteAsync(server, query);
   }

   #endregion

   #region 2. Semantics checked against hand-computed values (where SQLite itself is off, or the answer is known)

   private const string MonthEnds = "shop.p.select(id, a: addMonths(d, 1), b: addMonths(d, -1), c: addMonths(ts, 1)).orderBy(id)";

   private static readonly string MonthEndsExpected = Lines(
      "1 | 2026-02-28 | 2025-12-31 | 2026-02-28 10:30:00",
      "2 | 2024-03-29 | 2024-01-29 | 2024-03-29 23:59:59",
      "3 | 2026-04-30 | 2026-02-28 | 2026-04-30 00:00:00",
      "4 | null | null | null",
      "5 | 2027-01-31 | 2026-11-30 | 2027-01-31 23:00:00",
      "6 | 2025-02-15 | 2024-12-15 | 2025-02-15 12:00:00",
      "7 | 2025-07-30 | 2025-05-30 | 2025-07-30 06:15:45");

   /// <summary>addMonths stays in the month it lands in: 31 January plus a month is 28 February.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task AddMonthsClampsToTheMonthEnd(ServerKind server)
   {
      await using TestSources sources = await ProbeSourcesAsync(server);
      await GivesAsync(sources, MonthEnds, MonthEndsExpected);
   }

   /// <summary>The reference itself: SQLite's date(x, '+1 months') overflows into the next month (not an M9 change).</summary>
   [Fact]
   public async Task SqliteAddMonthsClampsToTheMonthEnd()
   {
      await using TestSources sqlite = await SqliteProbeAsync();
      (await TextAsync(sqlite.Engine(), MonthEnds)).ShouldBe(MonthEndsExpected);
   }

   private const string DateBuckets = "shop.p.select(id, w: startOfWeek(d), mo: startOfMonth(d), q: startOfQuarter(d), y: startOfYear(d), qn: quarter(d), wd: dayOfWeek(d)).orderBy(id)";

   private static readonly string DateBucketsExpected = Lines(
      "1 | 2026-01-26 | 2026-01-01 | 2026-01-01 | 2026-01-01 | 1 | 6",
      "2 | 2024-02-26 | 2024-02-01 | 2024-01-01 | 2024-01-01 | 1 | 4",
      "3 | 2026-03-30 | 2026-03-01 | 2026-01-01 | 2026-01-01 | 1 | 2",
      "4 | null | null | null | null | null | null",
      "5 | 2026-12-28 | 2026-12-01 | 2026-10-01 | 2026-01-01 | 4 | 4",
      "6 | 2025-01-13 | 2025-01-01 | 2025-01-01 | 2025-01-01 | 1 | 3",
      "7 | 2025-06-30 | 2025-06-01 | 2025-04-01 | 2025-01-01 | 2 | 1");

   private const string OffsetBuckets = "shop.p.where(z != null).select(id, w: startOfWeek(z), mo: startOfMonth(z), q: startOfQuarter(z), y: startOfYear(z), qn: quarter(z), wd: dayOfWeek(z)).orderBy(id)";

   /// <summary>The UTC days: 2026-01-31 23:30 at -05:00 is a Sunday in February; 2026-12-31 22:00 at -03:00 a Friday in 2027.</summary>
   private static readonly string OffsetBucketsExpected = Lines(
      "1 | 2026-01-26 | 2026-01-01 | 2026-01-01 | 2026-01-01 | 1 | 4",
      "2 | 2026-01-26 | 2026-02-01 | 2026-01-01 | 2026-01-01 | 1 | 7",
      "3 | 2026-02-23 | 2026-02-01 | 2026-01-01 | 2026-01-01 | 1 | 6",
      "5 | 2026-12-28 | 2027-01-01 | 2027-01-01 | 2027-01-01 | 1 | 5",
      "6 | 2026-01-26 | 2026-01-01 | 2026-01-01 | 2026-01-01 | 1 | 4",
      "7 | 2026-01-26 | 2026-02-01 | 2026-01-01 | 2026-01-01 | 1 | 7");

   /// <summary>Weeks start on Monday and days of the week count from it, whatever the server's settings; offsets by their UTC days.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task DateBucketsAreTheFirstDaysOfTheirPeriods(ServerKind server)
   {
      await using TestSources sources = await ProbeSourcesAsync(server);
      await GivesAsync(sources, DateBuckets, DateBucketsExpected);
      await GivesAsync(sources, OffsetBuckets, OffsetBucketsExpected);
   }

   /// <summary>The reference, by hand too, as the servers are compared with it.</summary>
   [Fact]
   public async Task SqliteDateBucketsAreTheFirstDaysOfTheirPeriods()
   {
      await using TestSources sqlite = await SqliteProbeAsync();
      (await TextAsync(sqlite.Engine(), DateBuckets)).ShouldBe(DateBucketsExpected);
      (await TextAsync(sqlite.Engine(), OffsetBuckets)).ShouldBe(OffsetBucketsExpected);
   }

   /// <summary>A date-time with an offset is an instant: a month is added to its UTC time, as PostgreSQL and DuckDB do.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task AddMonthsOfAnOffsetDateTimeWorksInUtc(ServerKind server)
   {
      await using TestSources sources = await ProbeSourcesAsync(server);
      await GivesAsync(sources, "shop.p.where(z != null).select(id, a: addMonths(z, 1)).orderBy(id)", Lines(
         "1 | 2026-02-28 20:00:00+00:00",
         "2 | 2026-03-01 04:30:00+00:00",
         "3 | 2026-03-28 23:30:00+00:00",
         "5 | 2027-02-01 01:00:00+00:00",
         "6 | 2026-02-28 21:00:00+00:00",
         "7 | 2026-03-01 02:00:00+00:00"));
   }

   /// <summary>Offset date-times sort as instants (row 7 is earlier than row 2 in UTC, later in local time).</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task OffsetDateTimesSortAsInstants(ServerKind server)
   {
      await using TestSources sources = await ProbeSourcesAsync(server);
      await GivesAsync(sources, "shop.p.orderBy(z, id).select(id)", Lines("4", "1", "6", "7", "2", "3", "5"));
   }

   /// <summary>Guids have no order every database agrees on (SQL Server sorts by the last group first): the binder refuses to sort them (by design).</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task GuidsAreNotSorted(ServerKind server)
   {
      await using TestSources sources = await ProbeSourcesAsync(server);
      (await TextAsync(sources.Engine(), "shop.p.orderBy(g, id).select(id)")).ShouldStartWith("!QueryException: Guid values can't be sorted");
      (await TextAsync(sources.Engine(), "shop.p.groupBy().select(mx: max(g))")).ShouldStartWith("!QueryException");
   }

   /// <summary>A whole number parameter of any CLR width compared with a bigint column (the reference too).</summary>
   [Theory]
   [InlineData(null)]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task IntParameterAgainstABigintColumn(ServerKind? server)
   {
      await using TestSources sources = server is { } kind ? await (await servers.ShopAsync(kind)).SourcesAsync() : await TestSources.SqliteShopAsync();
      QueryEngine engine = sources.Engine();
      string single = await TextAsync(engine, "shop.orders.where(id == $b).select(id)", new QueryParameters().Add("b", 1001));
      string list = await TextAsync(engine, "shop.orders.where(id in [$a, $b]).select(id).orderBy(id)", new QueryParameters().Add("a", 1004L).Add("b", 1001));
      $"single:\n{single}\nlist:\n{list}".ShouldBe($"single:\n1001\nlist:\n{Lines("1001", "1004")}");
   }

   /// <summary>like() follows the database's collation: the probe databases compare case-sensitively.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task LikeIsCaseSensitiveInACaseSensitiveDatabase(ServerKind server)
   {
      await using TestSources sources = await ProbeSourcesAsync(server);
      await GivesAsync(sources, "shop.p.where(like(s, 'acme%') or like(s, 'X%')).select(id).orderBy(id)", "7", merged: false);
   }

   /// <summary>The reference: SQLite's LIKE ignores ASCII case though its = doesn't (not an M9 change).</summary>
   [Fact]
   public async Task SqliteLikeIsCaseSensitive()
   {
      await using TestSources sqlite = await SqliteProbeAsync();
      (await TextAsync(sqlite.Engine(), "shop.p.where(like(s, 'acme%') or like(s, 'X%')).select(id).orderBy(id)")).ShouldBe("7");
   }

   /// <summary>Division by zero fails on the servers (SQLite gives null, the merge engine infinity): the language doesn't say which.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task DivisionByZeroFailsOnTheServers(ServerKind server)
   {
      await using TestSources sources = await ProbeSourcesAsync(server);
      (await TextAsync(sources.Engine(), "shop.p.where(id == 5).select(q: m / n, r: m % n)")).ShouldStartWith("!QueryExecutionException");
   }

   /// <summary>Decimal division and averages have each database's digits: the language gives them no precision.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres, "0.83333333333333333333")]
   [InlineData(ServerKind.SqlServer, "0.8333333333")]
   public async Task DecimalDivisionHasTheDatabasesDigits(ServerKind server, string third)
   {
      await using TestSources sources = await ProbeSourcesAsync(server);
      (await TextAsync(sources.Engine(), "shop.p.where(id == 1).select(q: toDecimal(2.5) / 3)")).ShouldBe(third);
   }

   /// <summary>Text longer than 4000 characters in SQL Server, and lengths of the longest nvarchar and varchar.</summary>
   [Fact]
   public async Task SqlServerLengthOfTheLongestNvarcharAndVarchar()
   {
      await using TestSources sources = await ExoticSourcesAsync(ServerKind.SqlServer);
      await GivesAsync(sources, "k.exo.orderBy(id).select(id, l: length(big), lv: length(bigv))", Lines("1 | 4000 | 8000", "2 | 4000 | 1"));
   }

   /// <summary>SQL Server counts a character outside the BMP as two (its UTF-16 units) without an _SC collation; the merge engine as one.</summary>
   [Fact]
   public async Task SqlServerLengthCountsUtf16UnitsWithoutAnScCollation()
   {
      await using TestSources sources = await ExoticSourcesAsync(ServerKind.SqlServer);
      await GivesAsync(sources, "k.exo.orderBy(id).select(id, l: length(emoji))", Lines("1 | 2", "2 | 1"), merged: false);
   }

   #endregion

   #region 3. The same value in the server's SQL and in the merge engine

   public static TheoryData<string, string> PostgresMergeCases => new()
   {
      { "toString-timestamp", "toString(ts)" },
      { "toString-timestamptz", "toString(tstz)" },
      { "toString-time", "toString(t)" },
      { "toString-date", "toString(d)" },
      { "toString-guid", "toString(uid)" },
      { "toString-double", "toString(i32 / 7)" },
      { "toString-real", "toString(f32)" },
      { "toString-numeric", "toString(num)" },
      { "toString-decimal", "toString(dec)" },
      { "toString-interval", "toString(iv)" },
      { "toString-bool", "toString(b)" },
      { "toString-jsonb", "toString(jsb)" },
      { "enum-lower", "lower(en)" },
      { "enum-startsWith", "startsWith(en, 'ha')" },
      { "enum-length", "length(en)" },
      { "enum-concat", "en + '!'" },
      { "domain-concat", "cd + '!'" },
      { "interval-hours", "toString(iv)" },
      { "timestamptz-startOfWeek", "startOfWeek(tstz)" },
      { "timestamp-startOfQuarter", "startOfQuarter(ts)" },
      { "date-dayOfWeek", "dayOfWeek(d)" },
   };

   [Theory]
   [MemberData(nameof(PostgresMergeCases))]
   public async Task PostgresValueIsTheSameInTheMergeEngine(string probe, string expression)
   {
      _ = probe;
      await using TestSources sources = await (await servers.KindsAsync(ServerKind.Postgres)).SourcesAsync("k");
      await SameDirectAndMergedAsync(sources, $"k.kinds.where(id == 1).select(v: {expression})");
   }

   public static TheoryData<string, string> SqlServerMergeCases => new()
   {
      { "toString-smalldatetime", "toString(sdt)" },
      { "toString-date", "toString(d)" },
      { "toString-money", "toString(mo)" },
      { "toString-guid", "toString(uid)" },
      { "toString-real", "toString(f32)" },
      { "toString-decimal", "toString(dec)" },
      { "toString-bool", "toString(b)" },
      { "length-char", "length(ch)" },
      { "concat-char", "ch + '|'" },
      { "length-nchar", "length(nch)" },
      { "length-varchar-max", "length(vmax)" },
      { "tinyint-negate", "-u8" },
      { "money-multiply", "mo * 3" },
      { "datetime-addDays", "addDays(dt, 1)" },
      { "datetime-date", "date(dt)" },
      { "dto-hour", "hour(dto)" },
      { "dto-addMonths", "addMonths(dto, 1)" },
      { "datetime-startOfWeek", "startOfWeek(dt)" },
      { "dto-startOfMonth", "startOfMonth(dto)" },
      { "smalldatetime-startOfQuarter", "startOfQuarter(sdt)" },
      { "dto-dayOfWeek", "dayOfWeek(dto)" },
      { "alias-type-concat", "cd + '!'" },
   };

   [Theory]
   [MemberData(nameof(SqlServerMergeCases))]
   public async Task SqlServerValueIsTheSameInTheMergeEngine(string probe, string expression)
   {
      _ = probe;
      await using TestSources sources = await (await servers.KindsAsync(ServerKind.SqlServer)).SourcesAsync("k");
      await SameDirectAndMergedAsync(sources, $"k.kinds.where(id == 1).select(v: {expression})");
   }

   #endregion

   #region 4. Exotic types: read, compared and passed through the merge engine

   /// <summary>Numerics a decimal can't hold (too large, too many digits) fail naming their row and column, and are null when lenient.</summary>
   [Theory]
   [InlineData(2)]
   [InlineData(4)]
   public async Task PostgresNumericADecimalCantHoldFailsNamingItsColumn(int id)
   {
      await using TestSources sources = await ExoticSourcesAsync(ServerKind.Postgres);
      string query = $"k.exo.where(id == {id.ToString(CultureInfo.InvariantCulture)}).select(num)";
      (await TextAsync(sources.Engine(), query)).ShouldStartWith("!QueryExecutionException: Row 1, column 'num'");
      (await TextAsync(sources.Engine(options: new QueryEngineOptions { LenientConversion = true }), query)).ShouldBe("null");
   }

   /// <summary>NaN has no decimal: a failure, but one that says where (QueryExecutionException).</summary>
   [Fact]
   public async Task PostgresNumericNaNFailsAsAQueryExecutionException()
   {
      await using TestSources sources = await ExoticSourcesAsync(ServerKind.Postgres);
      (await TextAsync(sources.Engine(), "k.exo.where(id == 1).select(num)")).ShouldStartWith("!QueryExecutionException");
   }

   /// <summary>infinity, -infinity and the year 2300 come back the same through the merge engine.</summary>
   [Fact]
   public async Task PostgresInfiniteAndFarDatesSurviveTheMergeEngine()
   {
      await using TestSources sources = await ExoticSourcesAsync(ServerKind.Postgres);
      await SameToTheMicrosecondDirectAndMergedAsync(sources, "k.exo.orderBy(id).select(id, dinf, tsinf)");
   }

   /// <summary>A composite type is unknown: it can be selected (the catalog lets it), directly and through the merge engine.</summary>
   [Fact]
   public async Task PostgresCompositeColumnIsReadable()
   {
      await using TestSources sources = await ExoticSourcesAsync(ServerKind.Postgres);
      await SameDirectAndMergedAsync(sources, "k.exo.where(id < 3).orderBy(id).select(id, comp)");
   }

   [Fact]
   public async Task PostgresExoticColumnsAreReadable()
   {
      await using TestSources sources = await ExoticSourcesAsync(ServerKind.Postgres);
      await SameDirectAndMergedAsync(sources, "k.exo.where(id < 3).orderBy(id).select(id, ttz, rng, ci, lv, dd)");
   }

   [Fact]
   public async Task PostgresVeryLongTextIsRead()
   {
      await using TestSources sources = await ExoticSourcesAsync(ServerKind.Postgres);
      await GivesAsync(sources, "k.exo.where(id == 1).select(l: length(big))", "1000000");
   }

   /// <summary>citext compares as citext and a domain over an enum as the enum (documented behaviour of untyped text).</summary>
   [Fact]
   public async Task PostgresCitextAndEnumDomainsCompareWithText()
   {
      await using TestSources sources = await ExoticSourcesAsync(ServerKind.Postgres);
      string citext = await TextAsync(sources.Engine(), "k.exo.where(ci == 'mixed').select(id)");
      string domain = await TextAsync(sources.Engine(), "k.exo.where(lv == 'hi').select(id)");
      string enumColumn = await TextAsync(sources.Engine(), "k.kinds.where(en == 'happy' and en in ['happy', 'ok']).select(id)");
      $"citext: {citext}\ndomain over enum: {domain}\nenum: {enumColumn}".ShouldBe("citext: 1\ndomain over enum: 1\nenum: 1");
   }

   /// <summary>A row whose numeric is Infinity, read whole (as a grid would): at worst a QueryExecutionException naming the source.</summary>
   [Fact]
   public async Task PostgresInfinityNumericFailsAsAQueryExecutionException()
   {
      await using TestSources sources = await ExoticSourcesAsync(ServerKind.Postgres);
      (await TextAsync(sources.Engine(), "k.exo.where(id == 3)")).ShouldStartWith("!QueryExecutionException");
   }

   /// <summary>PostgreSQL's time 24:00:00 has no TimeOnly: a value or a QueryExecutionException, not a raw exception.</summary>
   [Fact]
   public async Task PostgresTime2400IsReadOrReported()
   {
      await using TestSources sources = await ExoticSourcesAsync(ServerKind.Postgres);
      string text = await TextAsync(sources.Engine(), "k.exo.where(id < 3).orderBy(id).select(id, tm)");
      (text.StartsWith('!') && !text.StartsWith("!QueryExecutionException", StringComparison.Ordinal)).ShouldBeFalse(text);
   }

   /// <summary>timestamptz infinity through the merge engine: the same instant (DuckDB's TIMESTAMPTZ keeps microseconds, so the 100 ns digit goes; a documented-style limitation).</summary>
   [Fact]
   public async Task PostgresInfiniteTimestamptzSurvivesTheMergeEngine()
   {
      await using TestSources sources = await ExoticSourcesAsync(ServerKind.Postgres);
      await SameInstantsDirectAndMergedAsync(sources, "k.exo.orderBy(id).select(id, tzinf)");
   }

   /// <summary>datetimeoffset's range through the merge engine (to the microsecond, as above).</summary>
   [Fact]
   public async Task SqlServerDatetimeoffsetExtremesSurviveTheMergeEngine()
   {
      await using TestSources sources = await ExoticSourcesAsync(ServerKind.SqlServer);
      await SameInstantsDirectAndMergedAsync(sources, "k.exo.orderBy(id).select(id, dtox)");
   }

   /// <summary>Column 1's instants, direct and through the merge engine, agree to the microsecond.</summary>
   private static async Task SameInstantsDirectAndMergedAsync(TestSources sources, string query)
   {
      async Task<List<DateTimeOffset?>> ReadAsync(QueryEngine engine)
      {
         await using QueryResult result = await engine.ExecuteAsync(new QueryRequest(query), Token);
         return (await result.ToListAsync(Token)).Select(r => (DateTimeOffset?)r[1]).ToList();
      }
      List<DateTimeOffset?> direct = await ReadAsync(sources.Engine());
      List<DateTimeOffset?> merged = await ReadAsync(sources.Engine(options: NoPushDown));
      string Micro(DateTimeOffset? value) => value is { } v ? v.UtcTicks.ToString(CultureInfo.InvariantCulture)[..^1] : "null";
      string.Join(", ", merged.Select(Micro)).ShouldBe(string.Join(", ", direct.Select(Micro)));
   }

   /// <summary>In a database that ignores case, keys that differ only in case fetch the row once, and the merge engine joins exactly.</summary>
   [Theory]
   [InlineData(BindJoinMode.Always, 1)]
   [InlineData(BindJoinMode.Always, 2000)]
   [InlineData(BindJoinMode.Never, 2000)]
   public async Task CaseInsensitiveServerTextKeys(BindJoinMode mode, int batch)
   {
      await using TestSources sources = await (await servers.DatabaseAsync(ServerKind.SqlServer, "shop", serverCollation: true)).SourcesAsync();
      await sources.AddDuckDbAsync("d", "CREATE TABLE names (n VARCHAR); INSERT INTO names VALUES ('Acme Ltd'), ('ACME LTD'), ('Beta Corp ');");
      (await TextAsync(sources.Engine(options: new QueryEngineOptions { BindJoins = mode, MaxBindBatch = batch }),
         "d.names.leftJoin(shop.customers, outer.n == inner.name, a: outer, b: inner).select(n: a.n, id: b.id).orderBy(n)"))
         .ShouldBe(Lines("'ACME LTD' | null", "'Acme Ltd' | 1", "'Beta Corp ' | null"));
   }

   /// <summary>A domain over a domain over integer is an integer.</summary>
   [Fact]
   public async Task PostgresDomainOverADomainIsItsBaseType()
   {
      SourceSchema schema = await IntrospectAsync(await servers.DatabaseAsync(ServerKind.Postgres, "kinds", PostgresExotic));
      TableSchema exo = schema.Tables.Single(t => t.Name == "exo");
      (exo.Columns.Single(c => c.Name == "dd").Type.Kind, exo.Columns.Single(c => c.Name == "lv").Type.Kind).ShouldBe((ScalarKind.Int32, ScalarKind.String));
   }

   /// <summary>A varchar with a UTF-8 collation holds any text; a constant compared with it must reach it intact.</summary>
   [Fact]
   public async Task SqlServerUtf8VarcharMatchesUnicodeText()
   {
      await using TestSources sources = await ExoticSourcesAsync(ServerKind.SqlServer);
      string constant = await TextAsync(sources.Engine(), "k.exo.where(u8 == '日本').select(id)");
      string parameter = await TextAsync(sources.Engine(), "k.exo.where(u8 == $v).select(id)", new QueryParameters().Add("v", "日本"));
      string read = await TextAsync(sources.Engine(), "k.exo.where(id == 1).select(u8)");
      $"constant: {constant}\nparameter: {parameter}\nread: {read}".ShouldBe("constant: 1\nparameter: 1\nread: '日本'");
   }

   /// <summary>Text with a character the database's code page lacks, compared with a plain varchar: it must not match the '?' it would be converted to.</summary>
   [Fact]
   public async Task SqlServerVarcharDoesNotMatchTextOutsideItsCodePage()
   {
      await using TestSources sources = await ExoticSourcesAsync(ServerKind.SqlServer);
      string constant = await TextAsync(sources.Engine(), "k.exo.where(q == 'a日b').select(id)");
      string parameter = await TextAsync(sources.Engine(), "k.exo.where(q == $v).select(id)", new QueryParameters().Add("v", "a日b"));
      string merged = await TextAsync(sources.Engine(options: NoPushDown), "k.exo.where(q == 'a日b').select(id)");
      $"constant: {constant}\nparameter: {parameter}\nmerged: {merged}".ShouldBe("constant: \nparameter: \nmerged: ");
   }

   /// <summary>A datetime2 of 9999-12-31 (a common "no end" value) in a query that combines SQL Server with DuckDB.</summary>
   [Fact]
   public async Task SqlServerFarDatesInAQueryThatCombinesSources()
   {
      await using TestSources sources = await ExoticSourcesAsync(ServerKind.SqlServer);
      await sources.AddDuckDbAsync("d", "CREATE TABLE ids AS SELECT i AS id FROM range(1, 3) AS t(i);");
      (await TextAsync(sources.Engine(), "d.ids.join(k.exo, outer.id == inner.id, a: outer, b: inner).select(id: a.id, lo: b.dmin, hi: b.dmax).orderBy(id)"))
         .ShouldBe(Lines("1 | 0001-01-01 00:00:00 | 9999-12-31 23:59:59.999999", "2 | 1900-01-01 00:00:00 | 2262-04-12 00:00:00"));
   }

   /// <summary>sysname is nvarchar(128): text that can be compared, not an unknown type.</summary>
   [Fact]
   public async Task SqlServerSysnameColumnIsText()
   {
      await using TestSources sources = await ExoticSourcesAsync(ServerKind.SqlServer);
      (await TextAsync(sources.Engine(), "k.exo.where(sn == 'name').select(id)")).ShouldBe("1");
   }

   /// <summary>datetime2's range (0001 to 9999) through the merge engine.</summary>
   [Fact]
   public async Task SqlServerDatetime2ExtremesSurviveTheMergeEngine()
   {
      await using TestSources sources = await ExoticSourcesAsync(ServerKind.SqlServer);
      await SameToTheMicrosecondDirectAndMergedAsync(sources, "k.exo.orderBy(id).select(id, dmin, dmax)");
   }

   [Fact]
   public async Task SqlServerExoticColumnsAreReadable()
   {
      await using TestSources sources = await ExoticSourcesAsync(ServerKind.SqlServer);
      await SameDirectAndMergedAsync(sources, "k.exo.orderBy(id).select(id, geo, sp, sn, cc, u8)");
      await SameDirectAndMergedAsync(sources, "k.sps.orderBy(id)");
   }

   #endregion

   #region 5. Parameters

   /// <summary>A value longer than the column isn't cut to its length (which would make 'P-100' + spaces + 'X' find 'P-100').</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task TextLongerThanTheColumnIsNotTruncated(ServerKind server)
   {
      await using TestSources sources = await (await servers.ShopAsync(server)).SourcesAsync();
      QueryEngine engine = sources.Engine();
      (await TextAsync(engine, "shop.order_lines.where(product_code == $v).select(line_no)", new QueryParameters().Add("v", "P-100" + new string(' ', 16) + "X"))).ShouldBe(string.Empty);
      (await TextAsync(engine, "shop.order_lines.where(product_code == 'P-100                X').select(line_no)")).ShouldBe(string.Empty);
      (await TextAsync(engine, "shop.order_lines.where(startsWith(product_code, $v)).count()", new QueryParameters().Add("v", "P-100" + new string('x', 30)))).ShouldBe("0");
   }

   /// <summary>A decimal with more scale than the column keeps it: 99.505 isn't 99.50.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task DecimalsWithMoreScaleThanTheColumnKeepIt(ServerKind server)
   {
      await using TestSources sources = await (await servers.ShopAsync(server)).SourcesAsync();
      QueryEngine engine = sources.Engine();
      (await TextAsync(engine, "shop.orders.where(total == $v).select(id)", new QueryParameters().Add("v", 99.505m))).ShouldBe(string.Empty);
      (await TextAsync(engine, "shop.orders.where(total == $v).select(id)", new QueryParameters().Add("v", 12.250m))).ShouldBe("1003");
      (await TextAsync(engine, "shop.order_lines.where(qty * price == $v).select(order_id)", new QueryParameters().Add("v", 200.001m))).ShouldBe(string.Empty);
      (await TextAsync(engine, "shop.orders.where(total == 99.505).select(id)")).ShouldBe(string.Empty);
      (await TextAsync(engine, "shop.orders.where(total > $v).select(id).orderBy(id)", new QueryParameters().Add("v", 99.4999999999m))).ShouldBe(Lines("1001", "1002"));
   }

   /// <summary>A DateTimeOffset of any offset is an instant.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task DateTimeOffsetParametersAreInstants(ServerKind server)
   {
      await using TestSources sources = await (await servers.ShopAsync(server)).SourcesAsync();
      QueryEngine engine = sources.Engine();
      (await TextAsync(engine, "shop.orders.where(placed_at == $v).select(id)", new QueryParameters().Add("v", new DateTimeOffset(2026, 1, 5, 3, 30, 0, TimeSpan.FromHours(-5))))).ShouldBe("1001");
      (await TextAsync(engine, "shop.orders.where(placed_at > $v).select(id)", new QueryParameters().Add("v", new DateTimeOffset(2026, 1, 5, 17, 59, 0, TimeSpan.FromHours(9))))).ShouldBe("1002");
   }

   /// <summary>A DateTimeOffset parameter compared with a date-time without offset, as SQLite compares it.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task DateTimeOffsetParameterAgainstADateTime(ServerKind server) =>
      await MatchesSqliteAsync(server, "shop.orders.where(toDateTime(placed_at) == $v).select(id)",
         new QueryParameters().Add("v", new DateTimeOffset(2026, 1, 5, 10, 30, 0, TimeSpan.FromHours(2))));

   /// <summary>One $param compared with a column and used elsewhere too.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task TheSameParameterInTwoRoles(ServerKind server)
   {
      await using TestSources sources = await (await servers.ShopAsync(server)).SourcesAsync();
      (await TextAsync(sources.Engine(), "shop.customers.where(name == $v).select(id, e: $v + '!', l: length($v))", new QueryParameters().Add("v", "Acme Ltd")))
         .ShouldBe("1 | 'Acme Ltd!' | 8");
   }

   /// <summary>The same constant compared with a column and used elsewhere (constants are parameters too).</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task TheSameConstantInTwoRoles(ServerKind server)
   {
      await using TestSources sources = await (await servers.ShopAsync(server)).SourcesAsync();
      (await TextAsync(sources.Engine(), "shop.customers.where(name == 'Acme Ltd').select(id, e: 'Acme Ltd' + '!')")).ShouldBe("1 | 'Acme Ltd!'");
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task NullParameterComparedWithAColumn(ServerKind server)
   {
      await using TestSources sources = await (await servers.ShopAsync(server)).SourcesAsync();
      (await TextAsync(sources.Engine(), "shop.customers.where(city == $v).select(id)", new QueryParameters().Add("v", null))).ShouldBe(string.Empty);
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task NullParameterSelected(ServerKind server)
   {
      await using TestSources sources = await (await servers.ShopAsync(server)).SourcesAsync();
      (await TextAsync(sources.Engine(), "shop.customers.select(id, x: $v).orderBy(id)", new QueryParameters().Add("v", null))).ShouldBe(Lines("1 | null", "2 | null", "3 | null"));
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task NullParameterTestedForNull(ServerKind server)
   {
      await using TestSources sources = await (await servers.ShopAsync(server)).SourcesAsync();
      QueryEngine engine = sources.Engine();
      string untyped = await TextAsync(engine, "shop.customers.where($v == null).count()", new QueryParameters().Add("v", null));
      string typed = await TextAsync(engine, "shop.customers.where(city == $v or $v == null).count()", new QueryParameters().Add("v", null, ScalarType.Text()));
      string both = await TextAsync(engine, "shop.customers.where(city == $v or $v == null).count()", new QueryParameters().Add("v", null));
      $"untyped: {untyped}\ntyped: {typed}\nboth: {both}".ShouldBe("untyped: 3\ntyped: 3\nboth: 3");
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task ParametersInListsFlagsAndPaging(ServerKind server)
   {
      await using TestSources sources = await (await servers.ShopAsync(server)).SourcesAsync();
      QueryEngine engine = sources.Engine();
      (await TextAsync(engine, "shop.orders.where(status in [$a, $b]).select(id).orderBy(id)", new QueryParameters().Add("a", "open").Add("b", "shipped"))).ShouldBe(Lines("1001", "1002", "1003"));
      (await TextAsync(engine, "shop.orders.where(id in [$a, $b]).select(id).orderBy(id)", new QueryParameters().Add("a", 1004L).Add("b", 1001L))).ShouldBe(Lines("1001", "1004"));
      (await TextAsync(engine, "shop.orders.where((total > 100) == $v).select(id)", new QueryParameters().Add("v", true))).ShouldBe("1001");
      (await TextAsync(engine, "shop.orders.where($v and status == 'open').select(id).orderBy(id)", new QueryParameters().Add("v", true))).ShouldBe(Lines("1001", "1003"));
      (await TextAsync(engine, "shop.orders.orderBy(id).skip($s).take($t).select(id)", new QueryParameters().Add("s", 1L).Add("t", 2L))).ShouldBe(Lines("1002", "1003"));
      (await TextAsync(engine, "shop.orders.where(order_date == $v).select(id)", new QueryParameters().Add("v", "2026-01-09"))).ShouldBe("1002");
      (await TextAsync(engine, "shop.crm.contacts.where(id == $v).select(email)", new QueryParameters().Add("v", "2F1C0000-0000-4000-8000-000000000001"))).ShouldBe("'ann@acme.test'");
      (await TextAsync(engine, "shop.customers.where(id == $v).select(name)", new QueryParameters().Add("v", 2L))).ShouldBe("'Beta Corp'");
   }

   /// <summary>SQL Server compares a datetime only with a datetime exactly: also when the column comes out of a derived table.</summary>
   [Fact]
   public async Task SqlServerDatetimeComparedThroughADerivedTable()
   {
      await using TestSources sources = await (await servers.KindsAsync(ServerKind.SqlServer)).SourcesAsync("k");
      QueryEngine engine = sources.Engine();
      QueryParameters value = new QueryParameters().Add("v", new DateTime(2026, 3, 1, 13, 45, 30, 123));
      string table = await TextAsync(engine, "k.kinds.where(dt == $v).select(id)", value);
      string derived = await TextAsync(engine, "k.kinds.orderBy(id).take(5).where(dt == $v).select(id)", value);
      string constant = await TextAsync(engine, "k.kinds.orderBy(id).take(5).where(dt == toDateTime('2026-03-01 13:45:30.123')).select(id)");
      string union = await TextAsync(engine, "k.kinds.select(id, dt).concat(k.kinds.select(id, dt)).where(dt == $v).count()", value);
      $"table: {table}\nderived: {derived}\nconstant: {constant}\nunion: {union}".ShouldBe("table: 1\nderived: 1\nconstant: 1\nunion: 2");
   }

   /// <summary>Request paging (stabilized by the key) and counts on the servers.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task RequestPagingAndCounts(ServerKind server)
   {
      await using TestSources sources = await (await servers.ShopAsync(server)).SourcesAsync();
      QueryEngine engine = sources.Engine();
      List<string> got = [];
      foreach (string query in new[] { "shop.orders.select(id, total)", "shop.orders.orderBy(status).select(id, status)", "shop.orders.groupBy(customer).select(customer.name, n: count()).orderBy(desc(n))" })
      {
         await using QueryResult page = await engine.ExecuteAsync(new QueryRequest(query) { Paging = new PageRequest(1, 2) }, Token);
         got.Add(TestSources.Format(await page.ToListAsync(Token), page.Schema).ReplaceLineEndings(" / ").Trim());
         await using QueryResult count = await engine.Prepare(query).ForCount().ExecuteAsync(Token);
         got.Add(TestSources.Format(await count.ToListAsync(Token)).Trim());
      }
      string.Join("\n", got).ShouldBe(Lines(
         "1002 | 99.50 / 1003 | 12.25 /", "4",
         "1001 | 'open' / 1003 | 'open' /", "4",
         "'Beta Corp' | 1 / 'Gamma Inc' | 1 /", "3"));
   }

   /// <summary>More constants than SQL Server takes parameters (2,100).</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task ManyTextConstantsInOneQuery(ServerKind server)
   {
      await using TestSources sources = await (await servers.ShopAsync(server)).SourcesAsync();
      string names = string.Join(", ", Enumerable.Range(0, 2200).Select(i => $"'n{i.ToString(CultureInfo.InvariantCulture)}'"));
      (await TextAsync(sources.Engine(), $"shop.customers.where(name in [{names}, 'Acme Ltd']).select(id)")).ShouldBe("1");
   }

   /// <summary>A char(3) holds 'ch ': text compares with it without its padding on both servers (the documented collation behaviour).</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task CharColumnsCompareWithoutPadding(ServerKind server)
   {
      await using TestSources sources = await (await servers.KindsAsync(server)).SourcesAsync("k");
      (await TextAsync(sources.Engine(), "k.kinds.where(ch == $v).select(id)", new QueryParameters().Add("v", "ch"))).ShouldBe("1");
   }

   #endregion

   #region 6. Bind joins: fetched by keys of each type, the rows are those of a full fetch

   private static readonly string KeyedRows = Lines("1 | 1", "2 | 2", "3 | null", "4 | 7", "5 | 5");

   /// <summary>DuckDB's keys of each type fetch the server's rows (two keys a statement), as a full fetch does.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres, "s")]
   [InlineData(ServerKind.Postgres, "g")]
   [InlineData(ServerKind.Postgres, "d")]
   [InlineData(ServerKind.Postgres, "dm")]
   [InlineData(ServerKind.Postgres, "ts")]
   [InlineData(ServerKind.Postgres, "z")]
   [InlineData(ServerKind.SqlServer, "s")]
   [InlineData(ServerKind.SqlServer, "g")]
   [InlineData(ServerKind.SqlServer, "d")]
   [InlineData(ServerKind.SqlServer, "dm")]
   [InlineData(ServerKind.SqlServer, "ts")]
   [InlineData(ServerKind.SqlServer, "z")]
   public async Task BindJoinByKeysOfEachType(ServerKind server, string column)
   {
      await using TestSources sources = await ProbeSourcesAsync(server);
      await sources.AddDuckDbAsync("d", DuckDbKeys);
      string query = $"d.keys.leftJoin(shop.p, outer.{column} == inner.{column}, a: outer, b: inner).select(k: a.k, id: b.id).orderBy(k, id)";
      QueryEngine bound = sources.Engine(options: new QueryEngineOptions { BindJoins = BindJoinMode.Always, MaxBindBatch = 2 });
      QueryEngine full = sources.Engine(options: new QueryEngineOptions { BindJoins = BindJoinMode.Never });
      $"bound:\n{await TextAsync(bound, query)}\nfull:\n{await TextAsync(full, query)}".ShouldBe($"bound:\n{KeyedRows}\nfull:\n{KeyedRows}");
      if (column != "s")
      {
         await using QueryResult result = await bound.ExecuteAsync(new QueryRequest(query), Token);
         await result.ToListAsync(Token);
         result.Stats.Fragments.Single(f => f.Source == "shop").Strategy.ShouldBe(FetchStrategy.Keys);
      }
   }

   /// <summary>Keys fetched from a PostgreSQL enum column: a key that isn't a label finds nothing (it can't fail the query).</summary>
   [Theory]
   [InlineData(BindJoinMode.Always)]
   [InlineData(BindJoinMode.Adaptive)]
   [InlineData(BindJoinMode.Never)]
   public async Task PostgresEnumKeysOutsideTheEnumFindNothing(BindJoinMode mode)
   {
      await using TestSources sources = await ProbeSourcesAsync(ServerKind.Postgres);
      await sources.AddDuckDbAsync("d", DuckDbKeys);
      (await TextAsync(sources.Engine(options: new QueryEngineOptions { BindJoins = mode }), "d.wants.leftJoin(shop.paint, outer.c == inner.c, w: outer, p: inner).select(c: w.c, n: p.n).orderBy(c)"))
         .ShouldBe(Lines("'blue' | null", "'red' | 1"));
   }

   /// <summary>PostgreSQL and SQL Server joined on keys of each type, fetched by keys.</summary>
   [Theory]
   [InlineData("s")]
   [InlineData("g")]
   [InlineData("d")]
   [InlineData("dm")]
   [InlineData("ts")]
   [InlineData("z")]
   public async Task CrossServerJoinByKeysOfEachType(string column)
   {
      await using TestSources sources = new();
      await (await ProbeDatabaseAsync(ServerKind.Postgres)).AddToAsync(sources, "pg");
      await (await ProbeDatabaseAsync(ServerKind.SqlServer)).AddToAsync(sources, "ms");
      string expected = Lines("1 | 1", "2 | 2", "3 | 3", "4 | null", "5 | 5", "6 | 6", "7 | 7");
      QueryEngine engine = sources.Engine(options: new QueryEngineOptions { BindJoins = BindJoinMode.Always, MaxBindBatch = 2 });
      string there = await TextAsync(engine, $"pg.p.leftJoin(ms.p, outer.{column} == inner.{column}, a: outer, b: inner).select(x: a.id, y: b.id).orderBy(x)");
      string back = await TextAsync(engine, $"ms.p.leftJoin(pg.p, outer.{column} == inner.{column}, a: outer, b: inner).select(x: a.id, y: b.id).orderBy(x)");
      $"pg->ms:\n{there}\nms->pg:\n{back}".ShouldBe($"pg->ms:\n{expected}\nms->pg:\n{expected}");
   }

   #endregion

   #region 7. Names

   private const string Odd = "shop[\"odd schema\"][\"Mixed Case\"]";

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task OddNamesAreQuoted(ServerKind server)
   {
      await using TestSources sources = await ProbeSourcesAsync(server);
      await GivesAsync(sources, Odd + ".where(it[\"with space\"] > 5 and it[\"order\"] == 20).select(Id, s: it[\"select\"], q: it['a\"b'], b: it[\"c]d\"], u: it[\"Ünï\"]).orderBy(Id)",
         "1 | 'sel' | 'quote' | 'bracket' | 'u'");
      await GivesAsync(sources, Odd + ".orderBy(Id).take(5).where(it[\"with space\"] > 5).select(Id, s: it[\"select\"], q: it['a\"b'], b: it[\"c]d\"])",
         "1 | 'sel' | 'quote' | 'bracket'");
      await GivesAsync(sources, Odd + ".groupBy(sel: it[\"select\"]).select(sel, n: count(), w: sum(it[\"with space\"]))", "'sel' | 1 | 10");
      await GivesAsync(sources, "shop[\"dot.table\"].select(id, v)", "1 | 'dot'");
   }

   /// <summary>Names longer than PostgreSQL's 63 bytes (which it truncates) or SQL Server's 128 (which it rejects), unique only at the end.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres, 70)]
   [InlineData(ServerKind.SqlServer, 70)]
   [InlineData(ServerKind.Postgres, 130)]
   [InlineData(ServerKind.SqlServer, 130)]
   public async Task LongNamesThatDifferOnlyAtTheEnd(ServerKind server, int length)
   {
      await using TestSources sources = await (await servers.ShopAsync(server)).SourcesAsync();
      string a = new('a', length);
      (await TextAsync(sources.Engine(), $"x := shop.orders.select({a}1: id, {a}2: total).take(10); x.where({a}2 > 50).select({a}1).orderBy({a}1)"))
         .ShouldBe(Lines("1001", "1002"));
   }

   #endregion

   #region 8. Introspection

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task CompositeAndCrossSchemaForeignKeysAreRead(ServerKind server)
   {
      SourceSchema schema = await IntrospectAsync(await KeysDatabaseAsync(server));
      ForeignKeySchema det = schema.Tables.Single(t => t.Schema == "sales" && t.Name == "det").ForeignKeys.ShouldHaveSingleItem();
      (string.Join(",", det.Columns), det.RefSchema, det.RefTable, string.Join(",", det.RefColumns), det.IsEnforced).ShouldBe(("y,x", "sales", "hdr", "b,a", true));
      ForeignKeySchema xref = schema.Tables.Single(t => t.Name == "xref").ForeignKeys.ShouldHaveSingleItem();
      // Deferrable (PostgreSQL) is still enforced; NOCHECK (SQL Server) isn't.
      (string.Join(",", xref.Columns), xref.RefSchema, xref.RefTable, string.Join(",", xref.RefColumns), xref.IsEnforced).ShouldBe(("a,b", "sales", "hdr", "a,b", server == ServerKind.Postgres));
      TableSchema hdr = schema.Tables.Single(t => t.Name == "hdr");
      (string.Join(",", hdr.PrimaryKey!.Columns), string.Join(",", hdr.UniqueKeys.Single().Columns)).ShouldBe(("a,b", "b,a"));
      QueryCatalog catalog = new CatalogBuilder().AddSource(new SourceInfo("shop", server == ServerKind.Postgres ? "postgres" : "sqlserver", schema.DefaultSchema), schema).Build();
      catalog.Diagnostics.Where(d => d.Severity == Diagnostics.DiagnosticSeverity.Error).ShouldBeEmpty();
      catalog.FindEntity("shop.sales.det").ShouldNotBeNull().Navigations.ShouldNotBeEmpty();
   }

   /// <summary>Schemas with odd names are filtered by name; estimates are the tables' rows.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task OddSchemasFilterAndRowEstimates(ServerKind server)
   {
      ServerDatabase probe = await ProbeDatabaseAsync(server);
      (await IntrospectAsync(probe, new IntrospectionOptions { IncludeSchemas = ["ODD SCHEMA"] })).Tables.Select(t => t.Name).ShouldBe(["Mixed Case"]);
      (await IntrospectAsync(probe, new IntrospectionOptions { ExcludeSchemas = ["odd schema"] })).Tables.ShouldNotContain(t => t.Name == "Mixed Case");
      SourceSchema schema = await IntrospectAsync(probe);
      (schema.Tables.Single(t => t.Name == "p").RowCountEstimate, schema.Tables.Single(t => t.Name == "dot.table").RowCountEstimate).ShouldBe((7L, 1L));
      (await IntrospectAsync(probe, new IntrospectionOptions { IncludeRowCountEstimates = false })).Tables.ShouldAllBe(t => t.RowCountEstimate == null);
   }

   /// <summary>PostgreSQL 18's NOT ENFORCED foreign keys aren't enforced.</summary>
   [Fact]
   public async Task PostgresNotEnforcedForeignKeyIsNotEnforced()
   {
      SourceSchema schema = await IntrospectAsync(await KeysDatabaseAsync(ServerKind.Postgres));
      schema.Tables.Single(t => t.Name == "ne").ForeignKeys.ShouldHaveSingleItem().IsEnforced.ShouldBeFalse();
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task IdentityComputedDefaultAndRowVersionFlagsAreRead(ServerKind server)
   {
      SourceSchema schema = await IntrospectAsync(await KeysDatabaseAsync(server));
      TableSchema idt = schema.Tables.Single(t => t.Name == "idt");
      ColumnSchema Column(string name) => idt.Columns.Single(c => c.Name == name);
      Column("a").IsIdentity.ShouldBeTrue();
      Column("c").DefaultSql.ShouldNotBeNull().ShouldContain("5");
      (Column("e").IsComputed, Column("e").DefaultSql).ShouldBe((true, null));
      if (server == ServerKind.Postgres) { Column("b").IsIdentity.ShouldBeTrue(); }
      else { Column("rv").IsRowVersion.ShouldBeTrue(); }
      IndexSchema filtered = idt.Indexes.Single(i => i.Name == "ux_idt_c");
      (filtered.IsUnique, filtered.Filter != null).ShouldBe((true, true));
      QueryCatalog catalog = new CatalogBuilder().AddSource(new SourceInfo("shop", server == ServerKind.Postgres ? "postgres" : "sqlserver", schema.DefaultSchema), schema).Build();
      catalog.FindEntity("shop.idt").ShouldNotBeNull().UniqueKeys.ShouldBeEmpty();
   }

   /// <summary>A partitioned table is one table (its partitions aren't listed), with an estimate once analyzed.</summary>
   [Fact]
   public async Task PostgresPartitionedTableIsOneTable()
   {
      ServerDatabase keys = await KeysDatabaseAsync(ServerKind.Postgres);
      SourceSchema schema = await IntrospectAsync(keys);
      schema.Tables.ShouldNotContain(t => t.Name == "meas_2026");
      TableSchema meas = schema.Tables.Single(t => t.Name == "meas");
      meas.RowCountEstimate.ShouldBe(2);
      await using TestSources sources = await keys.SourcesAsync();
      (await TextAsync(sources.Engine(), "shop.meas.where(at > toDate('2026-05-15')).select(id, v)")).ShouldBe("2 | 20");
   }

   [Fact]
   public async Task PostgresMaterializedViewIsRead()
   {
      ServerDatabase keys = await KeysDatabaseAsync(ServerKind.Postgres);
      TableSchema mv = (await IntrospectAsync(keys)).Tables.Single(t => t.Name == "mv");
      (mv.Kind, mv.Comment, mv.RowCountEstimate).ShouldBe((TableKind.MaterializedView, "mv comment", 3L));
      (await IntrospectAsync(keys, new IntrospectionOptions { IncludeViews = false })).Tables.ShouldNotContain(t => t.Name == "mv");
      await using TestSources sources = await keys.SourcesAsync();
      (await TextAsync(sources.Engine(), "shop.mv.orderBy(id).select(id)")).ShouldBe(Lines("1", "2", "3"));
   }

   /// <summary>A disabled unique index enforces nothing: customer 1 has two addresses, so customers.addresses is a collection.</summary>
   [Fact]
   public async Task SqlServerDisabledUniqueIndexIsNotAKey()
   {
      await using TestSources sources = await (await servers.DatabaseAsync(ServerKind.SqlServer, "shop", SqlServerDisabledIndex)).SourcesAsync();
      (await TextAsync(sources.Engine(), "shop.customers.select(name, n: addresses.count()).orderBy(name)")).ShouldBe(Lines("'Acme Ltd' | 2", "'Beta Corp' | 1", "'Gamma Inc' | 0"));
   }

   #endregion

   #region 9. Errors, cancellation and connections

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task AFailingStatementNamesTheSource(ServerKind server)
   {
      await using TestSources sources = await (await servers.ShopAsync(server)).SourcesAsync();
      string text = await TextAsync(sources.Engine(), "shop.orders.select(id, i: toInt(status))");
      text.ShouldStartWith("!QueryExecutionException");
      text.ShouldContain(server == ServerKind.Postgres ? "shop (PostgreSQL)" : "shop (SQL Server)");
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task AFailingFragmentNamesTheSource(ServerKind server)
   {
      await using TestSources sources = await (await servers.ShopAsync(server)).SourcesAsync();
      await sources.AddDuckDbAsync("d", DuckDbKeys);
      string text = await TextAsync(sources.Engine(), "d.picks.join(shop.orders.where(toInt(status) > 0), outer.n == inner.id, a: outer, b: inner).count()");
      text.ShouldStartWith("!QueryExecutionException");
      text.ShouldContain(server == ServerKind.Postgres ? "shop (PostgreSQL)" : "shop (SQL Server)");
      sources.Merge.ActiveSessions.ShouldBe(0);
   }

   private const string Slow = "k.numbers.selectMany(k.numbers, x: outer.n, y: inner.n).where((x * 7 + y * 3) % 1000 == 7).count()";

   /// <summary>Fifty queries (plain, failing, abandoned, cancelled, combined with DuckDB) leave no sessions behind.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task NoSessionsAreLeftBehind(ServerKind server)
   {
      ServerDatabase kinds = await servers.DatabaseAsync(server, "kinds", "-- sessions probe");
      await using TestSources sources = await kinds.SourcesAsync("k");
      await sources.AddDuckDbAsync("d", DuckDbKeys);
      QueryEngine engine = sources.Engine();
      const string Combined = "d.picks.join(k.numbers, outer.n == inner.n, a: outer, b: inner).count()";
      (await TextAsync(engine, "k.numbers.count()")).ShouldBe("3000");
      (await TextAsync(engine, Combined)).ShouldBe("4");
      int before = await SessionsAsync(kinds);
      for (int i = 0; i < 10; i++)
      {
         (await TextAsync(engine, "k.numbers.where(n < 3).select(n).orderBy(n)")).ShouldBe(Lines("1", "2"));
         (await TextAsync(engine, "k.codes.select(i: toInt(code))")).ShouldStartWith("!");
         await using (QueryResult abandoned = await engine.ExecuteAsync(new QueryRequest("k.numbers.select(n)"), Token))
         {
            (await abandoned.ReadAsync(Token)).ShouldBeTrue();
         }
         using (CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Token))
         {
            cancel.CancelAfter(TimeSpan.FromMilliseconds(150));
            try
            {
               await using QueryResult slow = await engine.ExecuteAsync(new QueryRequest(Slow), cancel.Token);
               await slow.ToListAsync(cancel.Token);
            }
            catch (OperationCanceledException)
            {
               // Expected.
            }
         }
         (await TextAsync(engine, "d.picks.join(k.codes.where(toInt(code) > 0), outer.n == inner.n, a: outer, b: inner).count()")).ShouldStartWith("!");
      }
      int after = 0;
      for (int attempt = 0; attempt < 20; attempt++)
      {
         after = await SessionsAsync(kinds);
         if (after <= before + 4) { break; }
         await Task.Delay(250, Token);
      }
      after.ShouldBeLessThanOrEqualTo(before + 4, $"{server}: {before} sessions before, {after} after");
      sources.Merge.ActiveSessions.ShouldBe(0);
   }

   private static async Task<int> SessionsAsync(ServerDatabase database)
   {
      await using DbConnection connection = database.Server == ServerKind.Postgres
         ? new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ToString())
         : new SqlConnection(new SqlConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ToString());
      await connection.OpenAsync(Token);
      string sql = database.Server == ServerKind.Postgres
         ? "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()"
         : "SELECT count(*) FROM sys.dm_exec_sessions WHERE database_id = DB_ID() AND session_id <> @@SPID";
      return int.Parse(await connection.ScalarTextAsync(sql, Token) ?? "0", CultureInfo.InvariantCulture);
   }

   #endregion

   #region 10. The CLI with server sources

   /// <summary>A connection string with a keyword the driver doesn't know is the user's mistake: exit 2, not "something went wrong inside gdq" (3).</summary>
   [Theory]
   [InlineData("postgres", "Foo=bar;Host=127.0.0.1")]
   [InlineData("sqlserver", "Foo=bar;Server=127.0.0.1")]
   public async Task CliReportsABadConnectionStringAsTheUsersMistake(string kind, string connectionString)
   {
      (int exit, _, string error) = await GdqAsync("run", "-s", $"shop={kind}:{connectionString}", "shop.orders.count()");
      (exit, error.Contains("inside gdq", StringComparison.Ordinal)).ShouldBe((2, false), error);
   }

   [Theory]
   [InlineData("postgres", "Host=127.0.0.1;Port=1;Username=postgres;Password=x;Timeout=2")]
   [InlineData("sqlserver", "Server=127.0.0.1,1;User ID=sa;Password=x;Connect Timeout=2;TrustServerCertificate=True")]
   public async Task CliReportsAnUnreachableServer(string kind, string connectionString)
   {
      (int exit, _, string error) = await GdqAsync("run", "-s", $"shop={kind}:{connectionString}", "shop.orders.count()");
      (exit, error.Contains("inside gdq", StringComparison.Ordinal)).ShouldBe((2, false), error);
   }

   /// <summary>-p city=null against a server source.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task CliNullParameterOnAServer(ServerKind server)
   {
      string source = $"shop={(server == ServerKind.Postgres ? "postgres" : "sqlserver")}:{(await servers.ShopAsync(server)).ConnectionString}";
      (int exit, string output, string error) = await GdqAsync("run", "-s", source, "-p", "city=null", "--format", "csv", "shop.customers.where(city == $city or $city == null).count()");
      (exit, output.Contains('3', StringComparison.Ordinal)).ShouldBe((0, true), output + error);
   }

   #endregion
}
