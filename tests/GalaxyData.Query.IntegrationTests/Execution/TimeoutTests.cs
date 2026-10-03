using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Dml;
using GalaxyData.Query.Execution;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Execution;

/// <summary>
/// Queries and changes that run longer than they may are stopped, in every database (SQLite and DuckDB take no
/// command timeout), and fail as timeouts, not as cancellations.
/// </summary>
public sealed class TimeoutTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);

   /// <summary>A billion combinations of three numbers: longer than any timeout here.</summary>
   private const string SlowCount =
      "s.nums.selectMany(s.nums, a: outer.n, b: inner.n).selectMany(s.nums, a: outer.a, b: outer.b, c: inner.n).where((a * 7 + b * 3 + c) % 1000 == 7).count()";

   private static async Task<TestSources> NumbersAsync(string provider)
   {
      TestSources sources = new();
      return provider == "sqlite"
         ? await sources.AddSqliteAsync("s", "CREATE TABLE nums (n INTEGER NOT NULL); WITH RECURSIVE c(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM c WHERE n < 1000) INSERT INTO nums SELECT n FROM c;")
         : await sources.AddDuckDbAsync("s", "CREATE TABLE nums AS SELECT i AS n FROM range(1, 1001) t(i); CREATE VIEW endless AS SELECT i AS id FROM range(1000000000000) t(i);");
   }

   private static async Task<QueryTimeoutException> TimesOutAsync(Func<Task> run)
   {
      Stopwatch watch = Stopwatch.StartNew();
      QueryTimeoutException timeout = await Should.ThrowAsync<QueryTimeoutException>(run);
      watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "the query was stopped, not left to finish");
      return timeout;
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task AQueryOfOneSourceIsStopped(string provider)
   {
      await using TestSources sources = await NumbersAsync(provider);
      QueryEngine engine = sources.Engine(options: new QueryEngineOptions { Timeout = Short });
      QueryTimeoutException timeout = await TimesOutAsync(async () =>
      {
         await using QueryResult result = await engine.ExecuteAsync(new QueryRequest(SlowCount), Token);
         await result.ToListAsync(Token);
      });
      timeout.Message.ShouldBe("The query ran longer than 300 ms, the most it may, and was stopped");
      timeout.Timeout.ShouldBe(Short);
   }

   /// <summary>A query that combines sources is stopped while its fragments are fetched, or while the merge engine runs it.</summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task AQueryThatCombinesSourcesIsStopped(bool pushDown)
   {
      await using TestSources sources = await NumbersAsync("sqlite");
      QueryEngine engine = sources.Engine(options: new QueryEngineOptions { Timeout = Short, PushDown = pushDown });
      await TimesOutAsync(async () =>
      {
         await using QueryResult result = await engine.ExecuteAsync(new QueryRequest(SlowCount), Token);
         await result.ToListAsync(Token);
      });
      sources.Merge.ActiveSessions.ShouldBe(0);
   }

   /// <summary>The time counts while the rows are read: a result read too slowly is stopped, and its next read fails.</summary>
   [Fact]
   public async Task ReadingCountsToo()
   {
      await using TestSources sources = await NumbersAsync("duckdb");
      QueryEngine engine = sources.Engine(options: new QueryEngineOptions { Timeout = TimeSpan.FromMilliseconds(500) });
      await using QueryResult result = await engine.ExecuteAsync(new QueryRequest("s.endless.select(id)"), Token);
      (await result.ReadAsync(Token)).ShouldBeTrue();
      await Task.Delay(TimeSpan.FromMilliseconds(700), Token);
      await Should.ThrowAsync<QueryTimeoutException>(async () => await result.ReadAsync(Token));
   }

   /// <summary>A request's timeout overrides the engine's; with none, a query isn't stopped.</summary>
   [Fact]
   public async Task ARequestsTimeoutOverridesTheEngines()
   {
      await using TestSources sources = await NumbersAsync("duckdb");
      QueryEngine engine = sources.Engine(options: new QueryEngineOptions { Timeout = TimeSpan.FromHours(1) });
      await TimesOutAsync(async () =>
      {
         await using QueryResult result = await engine.ExecuteAsync(new QueryRequest(SlowCount) { Timeout = Short }, Token);
         await result.ToListAsync(Token);
      });
      await using QueryResult quick = await engine.ExecuteAsync(new QueryRequest("s.nums.count()") { Timeout = Short }, Token);
      (await TestSources.RowsAsync(quick)).TrimEnd().ShouldBe("1000");

      // Counting a request's rows keeps to its timeout.
      PreparedQuery rows = engine.Prepare(new QueryRequest(SlowCount[..SlowCount.LastIndexOf(".count()", StringComparison.Ordinal)]) { Timeout = Short });
      await TimesOutAsync(async () =>
      {
         await using QueryResult count = await rows.ForCount().ExecuteAsync(Token);
         await count.ToListAsync(Token);
      });
   }

   /// <summary>
   /// Cancelling is still cancelling: the caller's token gives OperationCanceledException, whatever the timeout, and
   /// stops a statement SQLite is busy with (Microsoft.Data.Sqlite's own Cancel doesn't).
   /// </summary>
   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task CancellingIsntATimeout(string provider)
   {
      await using TestSources sources = await NumbersAsync(provider);
      QueryEngine engine = sources.Engine(options: new QueryEngineOptions { Timeout = TimeSpan.FromMinutes(5) });
      using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
      cancel.CancelAfter(Short);
      Stopwatch watch = Stopwatch.StartNew();
      await Should.ThrowAsync<OperationCanceledException>(async () =>
      {
         await using QueryResult result = await engine.ExecuteAsync(new QueryRequest(SlowCount), cancel.Token);
         await result.ToListAsync(cancel.Token);
      });
      watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
   }

   /// <summary>Changes that take too long to write are rolled back, and the result says why.</summary>
   [Fact]
   public async Task ChangesThatTakeTooLongAreRolledBack()
   {
      await using TestSources sources = await NumbersAsync("sqlite");
      QueryEngine engine = sources.Engine(options: new QueryEngineOptions { Timeout = Short });
      DmlScript script = engine.ParseScript(engine.Catalog.FindSource("s")!,
         "UPDATE nums SET n = n + 1;\nUPDATE nums SET n = 0 WHERE (SELECT count(*) FROM nums a, nums b, nums c WHERE (a.n * 7 + b.n * 3 + c.n) % 1000 = 7) > 0");
      Stopwatch watch = Stopwatch.StartNew();
      DmlResult result = await engine.CommitAsync([script], Token);
      watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
      result.Outcome.ShouldBe(DmlOutcome.RolledBack);
      result.Failure!.Kind.ShouldBe(DmlFailureKind.Timeout);
      result.Failure.Message.ShouldBe("The changes took longer than 300 ms to write, the most they may, and were rolled back");
      await using QueryResult sum = await engine.ExecuteAsync(new QueryRequest("s.nums.groupBy().select(total: sum(n))"), Token);
      (await TestSources.RowsAsync(sum)).TrimEnd().ShouldBe("500500");
   }
}
