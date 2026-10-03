using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Dml;
using GalaxyData.Query.Execution;
using GalaxyData.Query.IntegrationTests.Dml;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Execution;

/// <summary>
/// One engine serving many queries at once, as the application's does: each gives the rows it gives alone, the merge
/// engine's sessions are all closed after, and changes written meanwhile don't disturb the queries that read.
/// </summary>
public sealed class ConcurrencyTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   private static async Task<string> RowsAsync(QueryEngine engine, string query)
   {
      await using QueryResult result = await engine.ExecuteAsync(new QueryRequest(query) { Parameters = Conformance.Parameters }, Token);
      return TestSources.Format(await result.ToListAsync(Token), result.Schema, hidden: true);
   }

   [Fact]
   public async Task QueriesAtOnceGiveTheRowsTheyGiveAlone()
   {
      await using TestSources sources = await Conformance.SplitShopAsync();
      QueryEngine engine = sources.Engine(Conformance.SplitOverlay, new QueryEngineOptions { BindJoins = BindJoinMode.Always, MaxParallelFetches = 2 });
      List<string> queries = Conformance.Queries.Select(Conformance.Split).ToList();
      Dictionary<string, string> alone = [];
      foreach (string query in queries) { alone[query] = await RowsAsync(engine, query); }

      // Every query eight times, all at once, in a shuffled order.
      Random random = new(7);
      List<string> runs = [.. Enumerable.Repeat(queries, 8).SelectMany(q => q).OrderBy(_ => random.Next())];
      string[] together = await Task.WhenAll(runs.Select(q => Task.Run(() => RowsAsync(engine, q), Token)));
      for (int i = 0; i < runs.Count; i++) { together[i].ShouldBe(alone[runs[i]], runs[i]); }
      sources.Merge.ActiveSessions.ShouldBe(0);
   }

   /// <summary>Changes written while queries read the same tables: each query sees the rows before or after, never part of a change.</summary>
   [Fact]
   public async Task ChangesWrittenWhileQueriesReadAreAllOrNothingToThem()
   {
      await using TestSources sources = await TestSources.DuckDbShopAsync();
      QueryEngine engine = sources.Engine();
      DmlPlan Move(decimal amount) => engine.PlanChanges(new ChangeSet(
      [
         new UpdateRow(DmlScenarios.Table(engine, "shop.orders"), DmlScenarios.Row(("id", 1001L)), DmlScenarios.Row(("total", 250m - amount))),
         new UpdateRow(DmlScenarios.Table(engine, "shop.orders"), DmlScenarios.Row(("id", 1003L)), DmlScenarios.Row(("total", 12.25m + amount))),
      ]));
      using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
      Task writer = Task.Run(async () =>
      {
         for (int i = 0; i < 40 && !stop.IsCancellationRequested; i++)
         {
            (await engine.CommitAsync(Move(i % 2 == 0 ? 100m : 0m), Token)).Outcome.ShouldBe(DmlOutcome.Committed);
         }
      }, Token);
      List<string> sums = [];
      while (!writer.IsCompleted)
      {
         sums.Add(await RowsAsync(engine, "shop.orders.where(id in [1001, 1003]).groupBy().select(total: sum(total))"));
      }
      await writer;
      sums.ShouldNotBeEmpty();
      sums.ShouldAllBe(s => s.Trim() == "262.25");
   }
}
