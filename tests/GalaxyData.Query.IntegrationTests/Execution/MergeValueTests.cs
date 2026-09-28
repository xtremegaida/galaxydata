using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Execution;

/// <summary>
/// Values crossing into the merge engine and back: each query is run from its source alone and joined to another
/// source (so it goes through the merge engine), and the values must match.
/// </summary>
public sealed class MergeValueTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   private static async Task<object?[]> FirstRowAsync(QueryEngine engine, string query)
   {
      await using QueryResult result = await engine.ExecuteAsync(new QueryRequest(query), Token);
      IReadOnlyList<object?[]> rows = await result.ToListAsync(Token);
      return rows[0];
   }

   /// <summary>SQLite table <c>a.t</c> with the given columns and row, whose <c>who</c> is a person in DuckDB (<c>crm.people</c>).</summary>
   private static async Task<(TestSources, QueryEngine)> SourcesAsync(string columns, string values)
   {
      TestSources sources = new();
      await sources.AddSqliteAsync("a", $"CREATE TABLE t (id INTEGER PRIMARY KEY, {columns}, who INTEGER); INSERT INTO t VALUES {values};");
      await sources.AddDuckDbAsync("crm",
         "CREATE TABLE people (id INTEGER PRIMARY KEY, name VARCHAR, tags VARCHAR[], s STRUCT(k INTEGER, v VARCHAR), tz TIMETZ);" +
         "INSERT INTO people VALUES (1, 'Ann', ['x', 'y'], {'k': 1, 'v': 'one'}, TIMETZ '10:00:00+02');");
      CatalogOverlay overlay = new() { Relations = [new OverlayRelation("a.t", ["who"], "crm.people", ["id"]) { Name = "person", InverseName = "ts" }] };
      return (sources, sources.Engine(overlay));
   }

   [Theory]
   [InlineData("5")]
   [InlineData("2.5")]
   [InlineData("'text'")]
   [InlineData("x'0102'")]
   public async Task ValuesOfUnknownTypesComeBackAsTheSourceGaveThem(string value)
   {
      // A SQLite column declared without a type holds anything; its logical type is unknown.
      (TestSources sources, QueryEngine engine) = await SourcesAsync("v", $"(1, {value}, 1)");
      await using TestSources _ = sources;
      object? alone = (await FirstRowAsync(engine, "a.t.select(id, v)"))[1];
      object? merged = (await FirstRowAsync(engine, "a.t.select(id, v, name: person.name)"))[1];
      merged.ShouldBe(alone);
      sources.Merge.ActiveSessions.ShouldBe(0);
   }

   [Fact]
   public async Task UnknownValuesAreTextToTheMergeSql()
   {
      (TestSources sources, QueryEngine engine) = await SourcesAsync("v", "(1, 5, 1)");
      await using TestSources _ = sources;
      (await FirstRowAsync(engine, "a.t.select(id, s: toString(v), name: person.name)"))[1].ShouldBe("5");
      (await FirstRowAsync(engine, "a.t.where(v != null).select(id, name: person.name)"))[1].ShouldBe("Ann");
   }

   [Fact]
   public async Task DuckDbListsStructsAndTimesWithZonesComeBackToo()
   {
      (TestSources sources, QueryEngine engine) = await SourcesAsync("v", "(1, 'x', 1)");
      await using TestSources _ = sources;
      object?[] alone = await FirstRowAsync(engine, "crm.people.select(name, tags, s, tz)");
      object?[] merged = await FirstRowAsync(engine, "crm.people.select(name, tags, s, tz, n: ts.count())");
      merged[1].ShouldBeAssignableTo<IList>().ShouldBe((IList)alone[1]!);
      merged[2].ShouldBeAssignableTo<IDictionary>();
      merged[3].ShouldBe(alone[3]);
   }

   [Fact]
   public async Task DecimalsOfNoPrecisionKeepTheirDigits()
   {
      (TestSources sources, QueryEngine engine) = await SourcesAsync("ratio NUMERIC, total DECIMAL(10,2)", "(1, 0.000000000012345, 250, 1)");
      await using TestSources _ = sources;
      object?[] alone = await FirstRowAsync(engine, "a.t.select(id, ratio, third: total / 3)");
      object?[] merged = await FirstRowAsync(engine, "a.t.select(id, ratio, third: total / 3, name: person.name)");
      merged[1].ShouldBe(0.000000000012345m);
      merged[1].ShouldBe(alone[1]);
      merged[2].ShouldBe(alone[2]);
   }

   [Fact]
   public async Task DecimalsWiderThanDeclaredStillCross()
   {
      // SQLite keeps 999.99 in a DECIMAL(4,2) column.
      (TestSources sources, QueryEngine engine) = await SourcesAsync("small DECIMAL(4,2)", "(1, 999.99, 1)");
      await using TestSources _ = sources;
      (await FirstRowAsync(engine, "a.t.select(id, small)"))[1].ShouldBe(999.99m);
      (await FirstRowAsync(engine, "a.t.select(id, small, name: person.name)"))[1].ShouldBe(999.99m);
      sources.Merge.ActiveSessions.ShouldBe(0);
   }

   [Fact]
   public async Task DateTimesKeepTheirTicksAndOffsetsTheirInstant()
   {
      (TestSources sources, QueryEngine engine) = await SourcesAsync("at DATETIME, happened DATETIMEOFFSET", "(1, '2026-01-05 10:00:00.1234567', '2026-01-05 10:00:00+02:00', 1)");
      await using TestSources _ = sources;
      object?[] alone = await FirstRowAsync(engine, "a.t.select(id, at, happened)");
      object?[] merged = await FirstRowAsync(engine, "a.t.select(id, at, happened, name: person.name)");
      merged[1].ShouldBe(alone[1]);
      // The merge engine holds the instant, in UTC, as DuckDB sources give it.
      ((DateTimeOffset)merged[2]!).ShouldBe((DateTimeOffset)alone[2]!);
      ((DateTimeOffset)merged[2]!).Offset.ShouldBe(TimeSpan.Zero);
   }

   [Fact]
   public async Task AValueThatDoesntFitItsColumnFailsTheQueryNotTheProcess()
   {
      // Lenient conversion reads a bad date as null; a failing load names its source and row.
      (TestSources sources, QueryEngine engine) = await SourcesAsync("happened DATE", "(1, '2026-01-05', 1), (2, 'soon', 1)");
      await using TestSources _ = sources;
      QueryExecutionException error = await Should.ThrowAsync<QueryExecutionException>(async () =>
         await FirstRowAsync(engine, "a.t.select(id, happened, name: person.name)"));
      error.Message.ShouldStartWith("Row 2 from a, column 'happened'");
      sources.Merge.ActiveSessions.ShouldBe(0);
   }
}
