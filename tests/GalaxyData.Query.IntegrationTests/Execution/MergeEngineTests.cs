using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.DuckDb;
using GalaxyData.Query.Execution;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Execution;

/// <summary>The merge engine under load: more data than its memory, cancellation, and the limits on what a query fetches.</summary>
public sealed class MergeEngineTests
{
   private const int Items = 3_000_000;

   /// <summary>Items in DuckDB (wh), categories in SQLite (ref); an item's category is its id modulo 100.</summary>
   private static async Task<TestSources> WarehouseAsync(int items)
   {
      TestSources sources = new();
      await sources.AddDuckDbAsync("wh",
         "CREATE TABLE items AS SELECT i AS id, 'item ' || lpad(i::VARCHAR, 12, '0') || ' with a label long enough to take up room' AS label, " +
         $"i % 100 AS category_id FROM range({items}) t(i); " +
         "CREATE TABLE nums AS SELECT i AS n FROM range(100000) t(i); " +
         "CREATE VIEW endless AS SELECT i AS id FROM range(1000000000000) t(i);");
      await sources.AddSqliteAsync("ref",
         "CREATE TABLE categories (id INTEGER PRIMARY KEY, name TEXT NOT NULL); " +
         "WITH RECURSIVE c(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM c WHERE n < 99) INSERT INTO categories SELECT n, 'cat ' || n FROM c; " +
         "CREATE TABLE nums (n INTEGER NOT NULL); " +
         "WITH RECURSIVE c(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM c WHERE n < 99999) INSERT INTO nums SELECT n FROM c;");
      return sources;
   }

   /// <summary>The query schemas in the merge database other than the one of the session that looks.</summary>
   private static async Task<long> LeftoverSchemasAsync(DuckDbMergeEngine merge)
   {
      await using IMergeSession session = await merge.OpenSessionAsync(TestContext.Current.CancellationToken);
      await using DbCommand command = session.CreateCommand();
      command.CommandText = "SELECT count(*) FROM information_schema.schemata WHERE starts_with(schema_name, 'q_')";
      return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), CultureInfo.InvariantCulture) - 1;
   }

   private static readonly CatalogOverlay Overlay = new()
   {
      Relations = [new OverlayRelation("wh.items", ["category_id"], "ref.categories", ["id"]) { Name = "category", InverseName = "items" }],
   };

   [Fact(Timeout = 120_000)]
   public async Task MoreDataThanTheMemoryLimitSpillsToDisk()
   {
      string temp = Path.Combine(Path.GetTempPath(), "gdq-spill-" + Guid.NewGuid().ToString("N")[..8]);
      try
      {
         await using TestSources sources = await WarehouseAsync(Items);
         using DuckDbMergeEngine merge = new(new DuckDbMergeOptions { MemoryLimit = "64MB", TempDirectory = temp, Threads = 2 });
         QueryEngine engine = sources.Engine(Overlay, merge: merge);
         CancellationToken token = TestContext.Current.CancellationToken;

         // Every item is fetched (about 200 MB), joined to its category, and sorted in the merge engine: the sort is
         // by the category's name first, which the items' source doesn't have. Names sort as text (cat 99 ... cat 90,
         // cat 9, cat 89, ...), 30,000 items each, so row 1,000,000 is the 10,000th item of the 34th name, cat 69.
         await using (QueryResult result = await engine.ExecuteAsync(
            new QueryRequest("wh.items.select(id, label, cat: category.name).orderBy(desc(cat), desc(label)).skip(1000000).take(2)"), token))
         {
            (await TestSources.RowsAsync(result)).ShouldBe(
               "1999969 | 'item 000001999969 with a label long enough to take up room' | 'cat 69'" + Environment.NewLine +
               "1999869 | 'item 000001999869 with a label long enough to take up room' | 'cat 69'" + Environment.NewLine);
            result.Stats.FetchedRows.ShouldBe(Items + 100);
            // The session's tables are still there, mostly on disk.
            Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length).ShouldBeGreaterThan(64L << 20);
         }
         await using (QueryResult result = await engine.ExecuteAsync(
            new QueryRequest("wh.items.groupBy(category.name).select(name, n: count(), labels: countDistinct(label)).orderBy(name).take(2)"), token))
         {
            (await TestSources.RowsAsync(result)).ShouldBe(
               "'cat 0' | 30000 | 30000" + Environment.NewLine +
               "'cat 1' | 30000 | 30000" + Environment.NewLine);
         }
         merge.ActiveSessions.ShouldBe(0);
      }
      finally
      {
         if (Directory.Exists(temp)) { Directory.Delete(temp, recursive: true); }
      }
   }

   [Fact(Timeout = 60_000)]
   public async Task CancellingStopsAFetch()
   {
      await using TestSources sources = await WarehouseAsync(10);
      QueryEngine engine = sources.Engine(Overlay, new QueryEngineOptions { PushDown = false });
      using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
      cancel.CancelAfter(TimeSpan.FromMilliseconds(500));
      Stopwatch watch = Stopwatch.StartNew();
      // Without push-down the endless view is fetched whole into the merge engine.
      await Should.ThrowAsync<OperationCanceledException>(() => engine.ExecuteAsync(new QueryRequest("wh.endless.where(id < 0).count()"), cancel.Token));
      watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
      TestContext.Current.SendDiagnosticMessage($"fetch cancelled after {watch.ElapsedMilliseconds} ms");
      sources.Merge.ActiveSessions.ShouldBe(0);
      (await LeftoverSchemasAsync(sources.Merge)).ShouldBe(0);
   }

   [Fact(Timeout = 60_000)]
   public async Task CancellingStopsTheMergeQuery()
   {
      await using TestSources sources = await WarehouseAsync(10);
      QueryEngine engine = sources.Engine(Overlay);
      using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
      cancel.CancelAfter(TimeSpan.FromSeconds(2));
      Stopwatch watch = Stopwatch.StartNew();
      // 100,000 rows from each source are fetched quickly; comparing every pair of them takes the merge engine far longer.
      await Should.ThrowAsync<OperationCanceledException>(() =>
         engine.ExecuteAsync(new QueryRequest("wh.nums.join(ref.nums, outer.n != inner.n, a: outer.n, b: inner.n).count()"), cancel.Token));
      watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(12));
      TestContext.Current.SendDiagnosticMessage($"merge cancelled after {watch.ElapsedMilliseconds} ms");
      sources.Merge.ActiveSessions.ShouldBe(0);
      (await LeftoverSchemasAsync(sources.Merge)).ShouldBe(0);
   }

   [Fact(Timeout = 60_000)]
   public async Task CancellingStopsReadingAResult()
   {
      await using TestSources sources = await WarehouseAsync(10);
      QueryEngine engine = sources.Engine(Overlay);
      await using QueryResult result = await engine.ExecuteAsync(
         new QueryRequest("wh.nums.join(ref.nums, outer.n != inner.n, a: outer.n, b: inner.n)"), TestContext.Current.CancellationToken);
      using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
      cancel.CancelAfter(TimeSpan.FromMilliseconds(500));
      long rows = 0;
      await Should.ThrowAsync<OperationCanceledException>(async () =>
      {
         while (await result.ReadAsync(cancel.Token)) { rows++; }
      });
      rows.ShouldBeGreaterThan(0);
      sources.Merge.ActiveSessions.ShouldBe(1);
      await result.DisposeAsync();
      sources.Merge.ActiveSessions.ShouldBe(0);
      (await LeftoverSchemasAsync(sources.Merge)).ShouldBe(0);
   }

   [Fact]
   public async Task FetchingMoreRowsThanAllowedFails()
   {
      await using TestSources sources = await WarehouseAsync(1000);
      QueryEngine engine = sources.Engine(Overlay, new QueryEngineOptions { MaxFetchedRows = 500 });
      QueryExecutionException error = await Should.ThrowAsync<QueryExecutionException>(() =>
         engine.ExecuteAsync(new QueryRequest("wh.items.select(id, cat: category.name)"), TestContext.Current.CancellationToken));
      error.Message.ShouldContain("more than 500 rows");
      sources.Merge.ActiveSessions.ShouldBe(0);
      (await LeftoverSchemasAsync(sources.Merge)).ShouldBe(0);
   }

   [Fact]
   public async Task EachQueryHasItsOwnTablesUntilItsResultIsDisposed()
   {
      await using TestSources sources = await WarehouseAsync(1000);
      QueryEngine engine = sources.Engine(Overlay);
      CancellationToken token = TestContext.Current.CancellationToken;
      QueryResult first = await engine.ExecuteAsync(new QueryRequest("wh.items.where(id < 3).select(id, cat: category.name).orderBy(id)"), token);
      QueryResult second = await engine.ExecuteAsync(new QueryRequest("wh.items.where(id >= 998).select(id, cat: category.name).orderBy(id)"), token);
      sources.Merge.ActiveSessions.ShouldBe(2);
      (await TestSources.RowsAsync(second)).ShouldBe($"998 | 'cat 98'{Environment.NewLine}999 | 'cat 99'{Environment.NewLine}");
      (await TestSources.RowsAsync(first)).ShouldBe($"0 | 'cat 0'{Environment.NewLine}1 | 'cat 1'{Environment.NewLine}2 | 'cat 2'{Environment.NewLine}");
      await first.DisposeAsync();
      await second.DisposeAsync();
      sources.Merge.ActiveSessions.ShouldBe(0);

      // Many at once.
      string[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
      {
         await using QueryResult result = await engine.ExecuteAsync(new QueryRequest($"wh.items.where(id == {i}).select(id, cat: category.name)"), token);
         return await TestSources.RowsAsync(result);
      }));
      results.ShouldBe(Enumerable.Range(0, 8).Select(i => $"{i} | 'cat {i}'{Environment.NewLine}").ToArray());
      sources.Merge.ActiveSessions.ShouldBe(0);
   }
}
