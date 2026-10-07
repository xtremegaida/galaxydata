using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Execution;
using GalaxyData.Query.IntegrationTests.Execution;
using GalaxyData.Query.Providers;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.ContainerTests;

/// <summary>What the servers do differently: SQL Server's parameter types and limit, collations that ignore case, cancelling.</summary>
public sealed class ServerTests(Servers servers)
{
   /// <summary>
   /// Values compared with columns are sent as the columns' types: a varchar(20) as varchar(20), not nvarchar, which
   /// SQL Server would convert the column to (and scan its index); a decimal(10,2) as decimal(10,2); a datetime as datetime.
   /// </summary>
   [Fact]
   public async Task SqlServerParametersHaveTheColumnsTypes()
   {
      // A database of its own, so its cached plans are this test's.
      ServerDatabase shop = await servers.DatabaseAsync(ServerKind.SqlServer, "shop", "-- parameter types");
      await using TestSources sources = await shop.SourcesAsync();
      QueryEngine engine = sources.Engine();
      (await RowsAsync(engine, "shop.order_lines.where(product_code == $v).select(line_no).orderBy(line_no)", "P-100")).ShouldBe(["1", "1"]);
      (await RowsAsync(engine, "shop.customers.where(name == $v).select(id)", "Acme Ltd")).ShouldBe(["1"]);
      (await RowsAsync(engine, "shop.orders.where(total == $v).select(id)", 250.00m)).ShouldBe(["1001"]);
      (await RowsAsync(engine, "shop.orders.where(status in ['open', 'shipped'] and customer_id == 1).select(id).orderBy(id)", null)).ShouldBe(["1001", "1002"]);

      Dictionary<string, string> plans = [];
      await using (DbConnection connection = shop.Open())
      {
         await connection.OpenAsync(TestContext.Current.CancellationToken);
         await foreach (DbDataReader row in connection.QueryAsync(
            "SELECT t.text, CAST(p.query_plan AS nvarchar(max)) FROM sys.dm_exec_cached_plans c CROSS APPLY sys.dm_exec_sql_text(c.plan_handle) t " +
            "CROSS APPLY sys.dm_exec_query_plan(c.plan_handle) p WHERE t.dbid = DB_ID() AND t.text LIKE '(@p0 %'", TestContext.Current.CancellationToken))
         {
            plans[row.GetString(0)] = row.IsDBNull(1) ? string.Empty : row.GetString(1);
         }
      }
      string Declared(string column) => Declaration(plans.Keys.Single(t => t.Contains(column, StringComparison.Ordinal)));
      Declared(".product_code =").ShouldBe("(@p0 varchar(20))");
      Declared(".name =").ShouldBe("(@p0 nvarchar(100))");
      Declared(".total =").ShouldBe("(@p0 decimal(10,2))");
      Declared(".status IN").ShouldBe("(@p0 varchar(20),@p1 varchar(20))");
      plans.Values.ShouldAllBe(plan => !plan.Contains("CONVERT_IMPLICIT", StringComparison.Ordinal));
   }

   /// <summary>
   /// Keys that are text are parameters, and SQL Server takes at most 2,100 in a statement (ClickHouse, 1,000 over
   /// HTTP): more are fetched in full. Whole-number keys are written into the SQL, in batches.
   /// </summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   [InlineData(ServerKind.ClickHouse)]
   public async Task BindJoinsKeepToTheServersParameterLimit(ServerKind server)
   {
      await using TestSources sources = await (await servers.KindsAsync(server)).SourcesAsync("k");
      await sources.AddDuckDbAsync("d", "CREATE TABLE picks AS SELECT 'C' || lpad(CAST(i AS VARCHAR), 5, '0') AS code, i AS n FROM range(1, 2501) AS t(i);");
      QueryEngine engine = sources.Engine(options: new QueryEngineOptions { BindJoins = BindJoinMode.Always, MaxBindBatch = 5000 });
      FragmentStats text = await CountAsync(engine, "d.picks.join(k.codes, outer.code == inner.code, p: outer, c: inner).count()", 2500);
      FragmentStats whole = await CountAsync(sources.Engine(options: new QueryEngineOptions { BindJoins = BindJoinMode.Always, MaxBindBatch = 1000 }),
         "d.picks.join(k.codes, outer.n == inner.n, p: outer, c: inner).count()", 2500);
      if (server is ServerKind.SqlServer or ServerKind.ClickHouse)
      {
         text.Strategy.ShouldBe(FetchStrategy.Full);
      }
      else
      {
         (text.Strategy, text.Keys, text.Batches).ShouldBe((FetchStrategy.Keys, 2500, 1));
      }
      (whole.Strategy, whole.Keys, whole.Batches).ShouldBe((FetchStrategy.Keys, 2500, 3));
   }

   /// <summary>Tables of other schemas, names that need quotes, and keywords as names, in each server's own quoting.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task NamesAreQuotedAsTheServerQuotesThem(ServerKind server)
   {
      await using TestSources sources = await (await servers.ShopAsync(server)).SourcesAsync();
      QueryEngine engine = sources.Engine();
      (await RowsAsync(engine, "shop.crm.contacts.where(it[\"Display Name\"] != null).select(email, shown: it[\"Display Name\"], customer: customer_ref)", null))
         .ShouldBe(["'ann@acme.test' | 'Ann at Acme' | 1"]);
      (await RowsAsync(engine, "shop.audit_log.select(at, message, select: 1, order: 'x').count()", null)).ShouldBe(["0"]);
      (await RowsAsync(engine, "shop.customers.select(\"from\": name, \"where\": city).where(it[\"where\"] == 'Cape Town')", null)).ShouldBe(["'Acme Ltd' | 'Cape Town'"]);
   }

   /// <summary>SQL Server databases usually compare text ignoring case: so do the queries that run there (the language's documented exception).</summary>
   [Fact]
   public async Task TextComparesAsTheDatabasesCollationDoes()
   {
      ServerDatabase shop = await servers.DatabaseAsync(ServerKind.SqlServer, "shop", serverCollation: true);
      await using TestSources sources = await shop.SourcesAsync();
      QueryEngine engine = sources.Engine();
      (await RowsAsync(engine, "shop.customers.where(name == 'ACME LTD').select(id)", null)).ShouldBe(["1"]);
      (await RowsAsync(engine, "shop.customers.where(startsWith(name, 'ac') or contains(name, 'ETA')).select(id).orderBy(id)", null)).ShouldBe(["1", "2"]);
      (await RowsAsync(engine, "shop.customers.where(icontains(name, 'CORP')).select(id)", null)).ShouldBe(["2"]);
      // In the merge engine, case counts.
      QueryEngine merged = sources.Engine(options: new QueryEngineOptions { PushDown = false });
      (await RowsAsync(merged, "shop.customers.where(name == 'ACME LTD').select(id)", null)).ShouldBeEmpty();
   }

   /// <summary>Three thousand numbers cubed, each triple summed: minutes of work for either server.</summary>
   private const string SlowCount =
      "k.numbers.selectMany(k.numbers, x: outer.n, y: inner.n).selectMany(k.numbers, x: outer.x, y: outer.y, z: inner.n).where((x * 7 + y * 3 + z) % 1000 == 7).count()";

   /// <summary>A query that runs longer than it may is stopped on the server, and fails as a timeout.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   [InlineData(ServerKind.ClickHouse)]
   public async Task AQueryThatRunsTooLongIsStopped(ServerKind server)
   {
      ServerDatabase kinds = await servers.KindsAsync(server);
      await using TestSources sources = await kinds.SourcesAsync("k");
      QueryEngine engine = sources.Engine(options: new QueryEngineOptions { Timeout = TimeSpan.FromMilliseconds(500) });
      Stopwatch watch = Stopwatch.StartNew();
      await Should.ThrowAsync<QueryTimeoutException>(() => RowsAsync(engine, SlowCount, null));
      watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
   }

   /// <summary>Cancelling stops a statement the server is busy with, and the server stops running it.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   [InlineData(ServerKind.ClickHouse)]
   public async Task CancellingStopsTheStatementOnTheServer(ServerKind server)
   {
      ServerDatabase kinds = await servers.DatabaseAsync(server, "kinds", "-- cancelling");
      await using TestSources sources = await kinds.SourcesAsync("k");
      QueryEngine engine = sources.Engine();
      using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
      Stopwatch watch = Stopwatch.StartNew();
      Task<List<string>> running = CancellableRowsAsync(engine, SlowCount, null, cancel.Token);
      await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
      running.IsCompleted.ShouldBeFalse("27 billion sums take longer than half a second");
      await cancel.CancelAsync();
      await Should.ThrowAsync<OperationCanceledException>(() => running);
      watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));

      string busy = server switch
      {
         ServerKind.Postgres => "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND state = 'active' AND query LIKE '%numbers%' AND pid <> pg_backend_pid()",
         ServerKind.SqlServer => "SELECT count(*) FROM sys.dm_exec_requests r CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t WHERE r.database_id = DB_ID() AND t.text LIKE '%numbers%' AND r.session_id <> @@SPID",
         _ => "SELECT count() FROM system.processes WHERE current_database = currentDatabase() AND query LIKE '%numbers%' AND query NOT LIKE '%system.processes%'",
      };
      await using DbConnection connection = kinds.Open();
      await connection.OpenAsync(TestContext.Current.CancellationToken);
      string? running2 = null;
      for (int i = 0; i < 50; i++)
      {
         running2 = await connection.ScalarTextAsync(busy, TestContext.Current.CancellationToken);
         if (running2 == "0") { break; }
         await Task.Delay(100, TestContext.Current.CancellationToken);
      }
      running2.ShouldBe("0");
      (await RowsAsync(engine, "k.numbers.count()", null)).ShouldBe(["3000"]);
   }

   /// <summary>The parameters' declaration that starts the text of a parameterized statement: <c>(@p0 varchar(20))</c>.</summary>
   private static string Declaration(string text)
   {
      int depth = 0;
      for (int i = 0; i < text.Length; i++)
      {
         if (text[i] == '(') { depth++; }
         else if (text[i] == ')' && --depth == 0) { return text[..(i + 1)]; }
      }
      return text;
   }

   private static async Task<FragmentStats> CountAsync(QueryEngine engine, string query, long expected)
   {
      await using QueryResult result = await engine.ExecuteAsync(new QueryRequest(query), TestContext.Current.CancellationToken);
      (await result.ToListAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem()[0].ShouldBe(expected);
      return result.Stats.Fragments.Single(f => f.Source == "k");
   }

   private static Task<List<string>> RowsAsync(QueryEngine engine, string query, object? value) =>
      CancellableRowsAsync(engine, query, value, TestContext.Current.CancellationToken);

   private static async Task<List<string>> CancellableRowsAsync(QueryEngine engine, string query, object? value, CancellationToken cancellationToken)
   {
      QueryParameters parameters = new();
      if (value != null) { parameters.Add("v", value); }
      await using QueryResult result = await engine.ExecuteAsync(new QueryRequest(query) { Parameters = parameters }, cancellationToken);
      return TestSources.Format(await result.ToListAsync(cancellationToken), result.Schema).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToList();
   }
}
