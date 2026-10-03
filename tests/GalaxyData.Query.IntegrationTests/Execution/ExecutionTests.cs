using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Results;
using GalaxyData.Testing;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Execution;

public sealed class ExecutionTests
{
   [Fact]
   public async Task SqliteAndDuckDbReturnTheSameRows()
   {
      await using TestSources sqlite = await TestSources.SqliteShopAsync();
      await using TestSources duckdb = await TestSources.DuckDbShopAsync();
      string fromSqlite = await Conformance.RunAllAsync(sqlite);
      string fromDuckDb = await Conformance.RunAllAsync(duckdb);
      fromDuckDb.ShouldBe(fromSqlite);
      Golden.Match(fromSqlite);
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task ExampleOneRunsEndToEnd(string provider)
   {
      await using TestSources sources = provider == "sqlite" ? await TestSources.SqliteShopAsync() : await TestSources.DuckDbShopAsync();
      PreparedQuery prepared = sources.Engine().Prepare(Conformance.ExampleOne + ".orderBy(id)");
      prepared.Success.ShouldBeTrue();
      prepared.Fragments.ShouldHaveSingleItem().Source.Alias.ShouldBe("shop");

      await using QueryResult result = await prepared.ExecuteAsync(TestContext.Current.CancellationToken);
      result.Schema.VisibleColumns.Select(c => c.Name).ShouldBe(["id", "total", "who", "city"]);
      ResultColumn who = result.Schema.Columns[2];
      who.Lineage.Kind.ShouldBe(LineageKind.Direct);
      who.Lineage.NavigationPath.ShouldBe("customer");
      who.Lineage.Sources.ShouldHaveSingleItem().Column.ToString().ShouldBe("shop.customers.name");

      IReadOnlyList<object?[]> rows = await result.ToListAsync(TestContext.Current.CancellationToken);
      TestSources.Format(rows, result.Schema).ShouldBe(
         "1001 | 250.00 | 'Acme Ltd' | 'Cape Town'" + Environment.NewLine +
         "1003 | 12.25 | 'Beta Corp' | 'Johannesburg'" + Environment.NewLine);
      result.Stats.Rows.ShouldBe(2);
      sources.Opened.ShouldBe(1);
   }

   [Fact]
   public async Task PagingAppliesAfterTheQuery()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      QueryRequest request = new("shop.orders.orderBy(id).select(id)") { Paging = new PageRequest(1, 2) };
      await using QueryResult result = await sources.Engine().ExecuteAsync(request, TestContext.Current.CancellationToken);
      (await TestSources.RowsAsync(result)).ShouldBe($"1002{Environment.NewLine}1003{Environment.NewLine}");
   }

   [Fact]
   public async Task NowIsFixedWhenTheQueryStarts()
   {
      await using TestSources sources = await TestSources.DuckDbShopAsync();
      FixedClock clock = new(new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.Zero));
      QueryEngine engine = sources.Engine(options: new QueryEngineOptions { Clock = clock });
      await using QueryResult result = await engine.ExecuteAsync(
         new QueryRequest("shop.orders.where(order_date < today()).select(id, at: now(), age: daysBetween(order_date, today())).take(1)"),
         TestContext.Current.CancellationToken);
      (await TestSources.RowsAsync(result)).ShouldBe($"1001 | 2026-03-01 09:30:00 | 55{Environment.NewLine}");
   }

   [Fact]
   public async Task ParametersThatDontConvertFailWhenTheQueryRuns()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      PreparedQuery prepared = sources.Engine().Prepare("shop.orders.where(order_date >= $since)", new QueryParameters().Add("since", "soon"));
      prepared.Success.ShouldBeTrue();
      QueryExecutionException error = await Should.ThrowAsync<QueryExecutionException>(() => prepared.ExecuteAsync(TestContext.Current.CancellationToken));
      error.Message.ShouldContain("$since can't be used as a date");
   }

   [Fact]
   public async Task ValuesThatDontConvertNameTheirRowAndColumn()
   {
      const string script = "CREATE TABLE events (id INTEGER PRIMARY KEY, happened DATE); INSERT INTO events VALUES (1, '2026-01-05'), (2, 'soon');";
      await using TestSources sources = await new TestSources().AddSqliteAsync("log", script);
      await using (QueryResult strict = await sources.Engine().ExecuteAsync(new QueryRequest("log.events.orderBy(id)"), TestContext.Current.CancellationToken))
      {
         QueryExecutionException error = await Should.ThrowAsync<QueryExecutionException>(() => strict.ToListAsync(TestContext.Current.CancellationToken));
         error.Message.ShouldStartWith("Row 2, column 'happened'");
      }
      QueryEngine lenient = sources.Engine(options: new QueryEngineOptions { LenientConversion = true });
      await using QueryResult result = await lenient.ExecuteAsync(new QueryRequest("log.events.orderBy(id)"), TestContext.Current.CancellationToken);
      (await TestSources.RowsAsync(result)).ShouldBe($"1 | 2026-01-05{Environment.NewLine}2 | null{Environment.NewLine}");
   }

   [Fact]
   public async Task QueriesAcrossSourcesNeedAMergeEngine()
   {
      await using TestSources sources = new();
      await sources.AddSqliteAsync("shop", Fixtures.Sql("shop.sqlite.sql"));
      await sources.AddDuckDbAsync("wh", Fixtures.Sql("shop.duckdb.sql"));
      CatalogOverlay overlay = new() { Relations = [new OverlayRelation("wh.orders", ["customer_id"], "shop.customers", ["id"]) { Name = "shop_customer" }] };
      PreparedQuery prepared = sources.Engine(overlay, noMerge: true).Prepare("wh.orders.select(id, who: shop_customer.name)");
      prepared.Success.ShouldBeFalse();
      QueryDiagnostic error = prepared.Diagnostics.ShouldHaveSingleItem();
      error.Code.ShouldBe(DiagnosticCodes.CrossSourceQuery);
      error.Message.ShouldContain("reads from wh and shop; queries that combine sources need a merge engine");
      await Should.ThrowAsync<QueryException>(() => prepared.ExecuteAsync(TestContext.Current.CancellationToken));
      sources.Opened.ShouldBe(0);
   }

   [Theory]
   [InlineData("sqlite", "CREATE TABLE blobs (id INTEGER PRIMARY KEY, data BLOB); INSERT INTO blobs VALUES (1, X'AABB'), (2, NULL);")]
   [InlineData("duckdb", "CREATE TABLE blobs (id INTEGER PRIMARY KEY, data BLOB); INSERT INTO blobs VALUES (1, '\\xAA\\xBB'::BLOB), (2, NULL);")]
   public async Task BinaryValuesAreBytes(string provider, string script)
   {
      await using TestSources sources = provider == "sqlite" ? await new TestSources().AddSqliteAsync("b", script) : await new TestSources().AddDuckDbAsync("b", script);
      await using QueryResult result = await sources.Engine().ExecuteAsync(new QueryRequest("b.blobs.orderBy(id)"), TestContext.Current.CancellationToken);
      IReadOnlyList<object?[]> rows = await result.ToListAsync(TestContext.Current.CancellationToken);
      rows[0][1].ShouldBe(new byte[] { 0xAA, 0xBB });
      rows[1][1].ShouldBeNull();
   }

   [Fact]
   public async Task OffsetDateTimesCompareWithNowInUtcWhateverTheMachineZone()
   {
      const string script = "CREATE TABLE events (id INTEGER PRIMARY KEY, happened TIMESTAMPTZ); " +
                            "INSERT INTO events VALUES (1, TIMESTAMPTZ '2026-03-01 09:00:00+00'), (2, TIMESTAMPTZ '2026-03-01 10:00:00+00');";
      await using TestSources sources = await new TestSources().AddDuckDbAsync("log", script);
      QueryEngine engine = sources.Engine(options: new QueryEngineOptions { Clock = new FixedClock(new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.Zero)) });
      await using QueryResult result = await engine.ExecuteAsync(
         new QueryRequest("log.events.select(id, past: happened < now(), h: hour(happened), n: now()).orderBy(id)"), TestContext.Current.CancellationToken);
      (await TestSources.RowsAsync(result)).ShouldBe(
         "1 | true | 9 | 2026-03-01 09:30:00" + Environment.NewLine +
         "2 | false | 10 | 2026-03-01 09:30:00" + Environment.NewLine);
   }

   [Fact]
   public async Task CollectionsThatCantBeJoinedAreReported()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      PreparedQuery prepared = sources.Engine().Prepare("shop.customers.selectMany(c => shop.orders.where(o => o.customer_id == c.id).take(1)).select(id)");
      prepared.Success.ShouldBeFalse();
      prepared.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.NotTranslatable);
   }

   [Fact]
   public async Task CorrelatedSubqueriesRunWithoutTheOptimizerToo()
   {
      await using TestSources sources = await TestSources.DuckDbShopAsync();
      QueryEngine plain = sources.Engine(options: new QueryEngineOptions { Optimize = false });
      const string query = "shop.customers.select(name, n: orders.count(), spend: orders.sum(total), big: orders.any(total > 100)).orderBy(name)";
      await using QueryResult result = await plain.ExecuteAsync(new QueryRequest(query), TestContext.Current.CancellationToken);
      (await TestSources.RowsAsync(result)).ShouldBe(
         "'Acme Ltd' | 2 | 349.50 | true" + Environment.NewLine +
         "'Beta Corp' | 1 | 12.25 | false" + Environment.NewLine +
         "'Gamma Inc' | 1 | 0.00 | false" + Environment.NewLine);
   }

   [Fact]
   public async Task BindErrorsComeBackAsDiagnostics()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      PreparedQuery prepared = sources.Engine().Prepare("shop.orders.where(statuss == 'open')");
      prepared.Success.ShouldBeFalse();
      prepared.Diagnostics.ShouldHaveSingleItem().Message.ShouldContain("Did you mean 'status'?");
      prepared.Fragments.ShouldBeEmpty();
   }

   private sealed class FixedClock(DateTimeOffset now) : TimeProvider
   {
      public override DateTimeOffset GetUtcNow() => now;
   }
}
