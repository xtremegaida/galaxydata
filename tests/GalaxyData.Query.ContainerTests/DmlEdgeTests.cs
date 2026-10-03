using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Dml;
using GalaxyData.Query.Execution;
using GalaxyData.Query.IntegrationTests;
using GalaxyData.Query.IntegrationTests.Execution;
using GalaxyData.Query.Results;
using Shouldly;
using Xunit;
using static GalaxyData.Query.IntegrationTests.Dml.DmlScenarios;

namespace GalaxyData.Query.ContainerTests;

/// <summary>
/// Edges of changes on real databases: value round-trips, edited scripts that do exactly what the parameterized plan
/// does, concurrency, the executor's rollback and cancellation, result-row edits and the guard. SQLite and DuckDB run
/// in memory; PostgreSQL and SQL Server need the test servers.
/// </summary>
public sealed class DmlEdgeTests(Servers servers)
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   #region Helpers

   /// <summary>A small typed table in SQLite, a row of nulls at id 1.</summary>
   private static Task<TestSources> SqliteTypedAsync(CatalogOverlay? overlay = null) => new TestSources().AddSqliteAsync("s", """
      CREATE TABLE vals (
         id INTEGER PRIMARY KEY,
         txt TEXT,
         num DECIMAL(10,2),
         flag BOOLEAN,
         dbl DOUBLE,
         d DATE,
         dt DATETIME,
         dto DATETIMEOFFSET,
         g UNIQUEIDENTIFIER,
         bin BLOB,
         tm TIME
      );
      INSERT INTO vals (id) VALUES (1);
      """, enforceForeignKeys: false);

   private static Task<TestSources> DuckDbTypedAsync() => new TestSources().AddDuckDbAsync("s", """
      CREATE TABLE vals (
         id INTEGER PRIMARY KEY,
         txt VARCHAR,
         num DECIMAL(10,2),
         flag BOOLEAN,
         dbl DOUBLE,
         d DATE,
         dt TIMESTAMP,
         dto TIMESTAMPTZ,
         g UUID,
         bin BLOB,
         tm TIME
      );
      INSERT INTO vals (id) VALUES (1);
      """);

   private static readonly Guid Guid1 = new("2f1c0000-0000-4000-8000-00000000000a");

   private static Dictionary<string, object?> TypedValues() => Row(
      ("txt", "it's a \\ back 日本 \U0001F600 end"),
      ("num", 12.34m),
      ("flag", true),
      ("dbl", 2.5),
      ("d", new DateOnly(2026, 3, 1)),
      ("dt", new DateTime(2026, 3, 1, 13, 45, 30, 123)),
      ("dto", new DateTimeOffset(2026, 3, 1, 10, 30, 0, new TimeSpan(5, 30, 0))),
      ("g", Guid1),
      ("bin", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
      ("tm", new TimeOnly(13, 45, 30)));

   /// <summary>
   /// Runs a change parameterized on one database and as the edited (inline) display text on another, and returns the
   /// rows each leaves, which must be the same.
   /// </summary>
   private static async Task<(string Parameterized, string Edited)> ParameterizedAndEditedAsync(
      Func<Task<TestSources>> make, string alias, Func<QueryEngine, RowChange> change, string readQuery)
   {
      string parameterized;
      await using (TestSources a = await make())
      {
         QueryEngine ea = a.Engine();
         DmlResult result = await CommitAsync(ea, change(ea));
         result.Outcome.ShouldBe(DmlOutcome.Committed, "parameterized: " + result);
         parameterized = await RowsAsync(ea, readQuery);
      }
      string edited;
      await using (TestSources b = await make())
      {
         QueryEngine eb = b.Engine();
         DmlPlan plan = eb.PlanChanges(new ChangeSet([change(eb)]));
         plan.Issues.ShouldBeEmpty();
         DmlScript script = eb.ParseScript(eb.Catalog.FindSource(alias)!, plan.Scripts.Single().ToDisplayText());
         DmlResult result = await eb.CommitAsync([script], Token);
         result.Outcome.ShouldBe(DmlOutcome.Committed, "edited: " + result);
         edited = await RowsAsync(eb, readQuery);
      }
      return (parameterized, edited);
   }

   #endregion

   #region Edited display text must match the parameterized plan (Area 2)

   [Fact]
   public async Task SqliteEditedScriptWritesEveryTypeAsTheParameterizedPlanDoes()
   {
      (string parameterized, string edited) = await ParameterizedAndEditedAsync(
         () => SqliteTypedAsync(), "s",
         e => new UpdateRow(Table(e, "s.vals"), Row(("id", 1)), TypedValues()),
         "s.vals.where(id == 1)");
      edited.ShouldBe(parameterized);
   }

   [Fact]
   public async Task DuckDbEditedScriptWritesEveryTypeAsTheParameterizedPlanDoes()
   {
      (string parameterized, string edited) = await ParameterizedAndEditedAsync(
         () => DuckDbTypedAsync(), "s",
         e => new UpdateRow(Table(e, "s.vals"), Row(("id", 1)), TypedValues()),
         "s.vals.where(id == 1)");
      edited.ShouldBe(parameterized);
   }

   [Theory]
   [InlineData("it's")]
   [InlineData("a\\b\\c")]
   [InlineData("tab\there")]
   [InlineData("日本語テキスト")]
   [InlineData("emoji \U0001F600 here")]
   [InlineData("semi;colon and -- dashes /* c */")]
   [InlineData("percent %100% underscore _x_")]
   [InlineData("quote \" and apostrophes '' x")]
   public async Task SqliteEditedStringLiteralsRoundTrip(string value)
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      QueryEngine engine = sources.Engine();
      DmlPlan plan = engine.PlanChanges(new ChangeSet([new UpdateRow(Table(engine, "shop.customers"), Row(("id", 1)), Row(("city", value)))]));
      plan.Issues.ShouldBeEmpty();
      DmlScript script = engine.ParseScript(engine.Catalog.FindSource("shop")!, plan.Scripts.Single().ToDisplayText());
      (await engine.CommitAsync([script], Token)).Outcome.ShouldBe(DmlOutcome.Committed);
      (await RowsAsync(engine, "shop.customers.where(id == 1).select(city)")).ShouldBe("'" + value + "'");
   }

   [Theory]
   [InlineData("it's")]
   [InlineData("a\\b\\c")]
   [InlineData("日本語テキスト")]
   [InlineData("emoji \U0001F600 here")]
   [InlineData("semi;colon and -- dashes /* c */")]
   public async Task DuckDbEditedStringLiteralsRoundTrip(string value)
   {
      await using TestSources sources = await TestSources.DuckDbShopAsync();
      QueryEngine engine = sources.Engine();
      DmlPlan plan = engine.PlanChanges(new ChangeSet([new UpdateRow(Table(engine, "shop.customers"), Row(("id", 3)), Row(("city", value)))]));
      plan.Issues.ShouldBeEmpty();
      DmlScript script = engine.ParseScript(engine.Catalog.FindSource("shop")!, plan.Scripts.Single().ToDisplayText());
      (await engine.CommitAsync([script], Token)).Outcome.ShouldBe(DmlOutcome.Committed);
      (await RowsAsync(engine, "shop.customers.where(id == 3).select(city)")).ShouldBe("'" + value + "'");
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task ServerEditedScriptWritesEveryKindsValueAsTheParameterizedPlanDoes(ServerKind server)
   {
      async Task<TestSources> MakeAsync()
      {
         ServerDatabase database = await servers.FreshAsync(server, "kinds");
         return await database.SourcesAsync("k");
      }

      await using TestSources discover = await MakeAsync();
      QueryEngine de = discover.Engine();
      TableEntity kinds = Table(de, "k.kinds");
      List<string> writable = kinds.Columns
         .Where(c => !c.IsKey && !c.IsRowVersion && !c.IsComputed && !c.IsIdentity && c.Type.Kind != Types.ScalarKind.Unknown)
         .Select(c => c.Name).ToList();
      Dictionary<string, object?> filled = await KindRowAsync(de, 1);
      Dictionary<string, object?> values = writable.ToDictionary(c => c, c => filled[c], StringComparer.Ordinal);

      (string parameterized, string edited) = await ParameterizedAndEditedAsync(
         MakeAsync, "k",
         e => new UpdateRow(Table(e, "k.kinds"), Row(("id", 2)), values),
         "k.kinds.where(id == 2)");
      edited.ShouldBe(parameterized);
   }

   private static async Task<Dictionary<string, object?>> KindRowAsync(QueryEngine engine, int id)
   {
      await using QueryResult result = await engine.ExecuteAsync(new QueryRequest($"k.kinds.where(id == {id})"), Token);
      (await result.ReadAsync(Token)).ShouldBeTrue();
      return result.Schema.VisibleColumns.ToDictionary(c => c.Name, c => result.Current[c.Ordinal], StringComparer.Ordinal);
   }

   #endregion

   #region Value round-trips and find-by-original (SQLite, DuckDB)

   [Fact]
   public async Task SqliteTypedRoundTripFindsItsRowByEveryComparedOriginal()
   {
      await using TestSources sources = await SqliteTypedAsync();
      QueryEngine engine = sources.Engine();
      TableEntity vals = Table(engine, "s.vals");
      Dictionary<string, object?> values = TypedValues();
      (await CommitAsync(engine, new UpdateRow(vals, Row(("id", 1)), values))).Outcome.ShouldBe(DmlOutcome.Committed);

      Dictionary<string, object?> read = await ReadRowAsync(engine, "s.vals", 1);
      // Find the row again by every value it was read with: no false conflict (SQLite skips types it can't compare).
      Dictionary<string, object?> original = read.Where(v => v.Key != "id").ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
      DmlResult again = await CommitAsync(engine, new UpdateRow(vals, Row(("id", 1)), Row(("txt", "changed"))) { Original = original });
      again.Outcome.ShouldBe(DmlOutcome.Committed, again.ToString());
   }

   [Fact]
   public async Task DuckDbTypedRoundTripFindsItsRowByEveryComparedOriginal()
   {
      await using TestSources sources = await DuckDbTypedAsync();
      QueryEngine engine = sources.Engine();
      TableEntity vals = Table(engine, "s.vals");
      Dictionary<string, object?> values = TypedValues();
      (await CommitAsync(engine, new UpdateRow(vals, Row(("id", 1)), values))).Outcome.ShouldBe(DmlOutcome.Committed);

      Dictionary<string, object?> read = await ReadRowAsync(engine, "s.vals", 1);
      Dictionary<string, object?> original = read.Where(v => v.Key != "id").ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
      DmlResult again = await CommitAsync(engine, new UpdateRow(vals, Row(("id", 1)), Row(("txt", "changed"))) { Original = original });
      again.Outcome.ShouldBe(DmlOutcome.Committed, again.ToString());
   }

   [Fact]
   public async Task EmptyStringRoundTrips()
   {
      await using TestSources sources = await SqliteTypedAsync();
      QueryEngine engine = sources.Engine();
      (await CommitAsync(engine, new UpdateRow(Table(engine, "s.vals"), Row(("id", 1)), Row(("txt", ""))))).Outcome.ShouldBe(DmlOutcome.Committed);
      (await RowsAsync(engine, "s.vals.where(id == 1).select(txt)")).ShouldBe("''");
   }

   [Fact]
   public async Task NullOverwritesAValueAndIsFoundByIsNull()
   {
      await using TestSources sources = await SqliteTypedAsync();
      QueryEngine engine = sources.Engine();
      (await CommitAsync(engine, new UpdateRow(Table(engine, "s.vals"), Row(("id", 1)), Row(("txt", "hi"))))).Outcome.ShouldBe(DmlOutcome.Committed);
      DmlResult result = await CommitAsync(engine, new UpdateRow(Table(engine, "s.vals"), Row(("id", 1)), Row(("txt", null))) { Original = Row(("txt", "hi")) });
      result.Outcome.ShouldBe(DmlOutcome.Committed);
      (await RowsAsync(engine, "s.vals.where(id == 1).select(txt)")).ShouldBe("null");
   }

   [Fact]
   public async Task StringInputsConvertToColumnTypes()
   {
      await using TestSources sources = await SqliteTypedAsync();
      QueryEngine engine = sources.Engine();
      DmlResult result = await CommitAsync(engine, new UpdateRow(Table(engine, "s.vals"), Row(("id", 1)),
         Row(("num", "12.50"), ("d", "2026-03-01"), ("flag", "true"))));
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      (await RowsAsync(engine, "s.vals.where(id == 1).select(num, d)")).ShouldBe("12.50 | 2026-03-01");
   }

   private static async Task<Dictionary<string, object?>> ReadRowAsync(QueryEngine engine, string path, int id)
   {
      await using QueryResult result = await engine.ExecuteAsync(new QueryRequest($"{path}.where(id == {id})"), Token);
      (await result.ReadAsync(Token)).ShouldBeTrue();
      return result.Schema.VisibleColumns.ToDictionary(c => c.Name, c => result.Current[c.Ordinal], StringComparer.Ordinal);
   }

   #endregion

   #region Concurrency

   [Fact]
   public async Task AStaleOriginalIsAConflictAndWritesNothing()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      QueryEngine engine = sources.Engine();
      await sources.RunAsync("shop", "UPDATE customers SET city = 'Moved' WHERE id = 1");
      DmlResult result = await CommitAsync(engine, new UpdateRow(Table(engine, "shop.customers"), Row(("id", 1)), Row(("city", "New"))) { Original = Row(("city", "Cape Town")) });
      result.Outcome.ShouldBe(DmlOutcome.RolledBack);
      result.Failure!.Kind.ShouldBe(DmlFailureKind.Conflict);
      (await RowsAsync(engine, "shop.customers.where(id == 1).select(city)")).ShouldBe("'Moved'");
   }

   /// <summary>A key the overlay declares needn't be unique, so it serves navigation only: its table's rows can't be changed.</summary>
   [Fact]
   public async Task ADeclaredKeyDoesntMakeRowsChangeable()
   {
      CatalogOverlay overlay = new() { Entities = [new OverlayEntitySettings("s.dup") { Key = ["grp"] }] };
      await using TestSources sources = await new TestSources().AddSqliteAsync("s", """
         CREATE TABLE dup (grp TEXT, val TEXT);
         INSERT INTO dup VALUES ('a', '1'), ('a', '2'), ('b', '3');
         """);
      QueryEngine engine = sources.Engine(overlay);
      TableEntity dup = Table(engine, "s.dup");
      DmlPlan plan = engine.PlanChanges(new ChangeSet([new UpdateRow(dup, Row(("grp", "a")), Row(("val", "x")))]));
      plan.Issues.Single().Message.ShouldBe("s.dup has no primary key (the key the overlay declares serves navigation only), so its rows can't be told apart: they can be inserted, but not changed or deleted");
      dup.IsWritable.ShouldBeFalse();
      (await RowsAsync(engine, "s.dup.where(grp == 'a').orderBy(val).select(val)")).ShouldBe("'1'\n'2'".ReplaceLineEndings());
   }

   [Fact]
   public async Task AKeyMatchingNoRowIsAConflict()
   {
      await using TestSources sources = await new TestSources().AddSqliteAsync("s", """
         CREATE TABLE dup (grp TEXT PRIMARY KEY, val TEXT);
         INSERT INTO dup VALUES ('a', '1');
         """);
      QueryEngine engine = sources.Engine();
      DmlPlan plan = engine.PlanChanges(new ChangeSet([new UpdateRow(Table(engine, "s.dup"), Row(("grp", "z")), Row(("val", "x")))]));
      plan.Issues.ShouldBeEmpty();
      DmlResult result = await engine.CommitAsync(plan, Token);
      result.Outcome.ShouldBe(DmlOutcome.RolledBack);
      result.Failure!.Kind.ShouldBe(DmlFailureKind.Conflict);
   }

   /// <summary>SQL Server's rowversion stands for every original; once the row changes, the version read before finds nothing.</summary>
   [Fact]
   public async Task SqlServerRowVersionDetectsConcurrentChange()
   {
      ServerDatabase database = await servers.FreshAsync(ServerKind.SqlServer);
      await using TestSources sources = await database.SourcesAsync();
      QueryEngine engine = sources.Engine();
      Dictionary<string, object?> read = await ReadRowAsync(engine, "shop.orders", 1001);
      object? version = read["version"];
      await sources.RunAsync("shop", "UPDATE orders SET total = 999 WHERE id = 1001");
      DmlResult result = await CommitAsync(engine, new UpdateRow(Table(engine, "shop.orders"), Row(("id", 1001L)), Row(("total", 1m))) { Original = Row(("version", version)) });
      result.Outcome.ShouldBe(DmlOutcome.RolledBack);
      result.Failure!.Kind.ShouldBe(DmlFailureKind.Conflict);
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task ATriggersRowsDoNotCountAsTheStatements(ServerKind server)
   {
      ServerDatabase database = await servers.FreshAsync(server);
      await using TestSources sources = await database.SourcesAsync();
      QueryEngine engine = sources.Engine();
      DmlResult result = await CommitAsync(engine, new UpdateRow(Table(engine, "shop.orders"), Row(("id", 1001L)), Row(("total", 7m))));
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      (await RowsAsync(engine, "shop.audit_log.count()")).ShouldBe("1");
   }

   #endregion

   #region Executor

   [Fact]
   public async Task EmptyChangeSetCommitsNothingAndSucceeds()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      QueryEngine engine = sources.Engine();
      DmlPlan plan = engine.PlanChanges(new ChangeSet());
      plan.Success.ShouldBeTrue();
      plan.Scripts.ShouldBeEmpty();
      DmlResult result = await engine.CommitAsync(plan, Token);
      result.Outcome.ShouldBe(DmlOutcome.Committed);
      result.Scripts.ShouldBeEmpty();
   }

   [Fact]
   public async Task TwoScriptsForOneSourceAreRejected()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      QueryEngine engine = sources.Engine();
      SourceInfo shop = engine.Catalog.FindSource("shop")!;
      DmlScript a = engine.ParseScript(shop, "UPDATE customers SET city = 'A' WHERE id = 1");
      DmlScript b = engine.ParseScript(shop, "UPDATE customers SET city = 'B' WHERE id = 2");
      await Should.ThrowAsync<ArgumentException>(() => engine.CommitAsync([a, b], Token));
   }

   [Fact]
   public async Task AFailingStatementLeavesTheConnectionUsableAfterwards()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      QueryEngine engine = sources.Engine();
      // Insert a duplicate primary key: the statement fails and nothing commits.
      DmlResult failed = await CommitAsync(engine, new InsertRow(Table(engine, "shop.customers"), Row(("id", 1), ("name", "Dup Co"))));
      failed.Outcome.ShouldBe(DmlOutcome.RolledBack);
      failed.Failure!.Kind.ShouldBe(DmlFailureKind.Statement);
      (await RowsAsync(engine, "shop.customers.count()")).ShouldBe("3");
      // The next change on the same source still works: no leaked transaction.
      DmlResult ok = await CommitAsync(engine, new UpdateRow(Table(engine, "shop.customers"), Row(("id", 1)), Row(("city", "Later"))));
      ok.Outcome.ShouldBe(DmlOutcome.Committed);
   }

   [Fact]
   public async Task ACancelledCommitWritesNothingAndLeavesTheSourceUsable()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      QueryEngine engine = sources.Engine();
      using CancellationTokenSource cancelled = new();
      await cancelled.CancelAsync();
      DmlPlan plan = engine.PlanChanges(new ChangeSet([new UpdateRow(Table(engine, "shop.customers"), Row(("id", 1)), Row(("city", "Nope")))]));
      await Should.ThrowAsync<OperationCanceledException>(() => engine.CommitAsync(plan, cancelled.Token));
      (await RowsAsync(engine, "shop.customers.where(id == 1).select(city)")).ShouldBe("'Cape Town'");
      // Not left blocked by a leaked connection or transaction.
      (await CommitAsync(engine, new UpdateRow(Table(engine, "shop.customers"), Row(("id", 1)), Row(("city", "Fine"))))).Outcome.ShouldBe(DmlOutcome.Committed);
   }

   [Fact]
   public async Task CancellationDuringStatementsWritesNothing()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      using CancellationTokenSource cancel = new();
      QueryEngine engine = sources.Engine(options: new QueryEngineOptions { BeforeCommit = _ => throw new InvalidOperationException("should never reach commit") });
      DmlPlan plan = engine.PlanChanges(new ChangeSet(
      [
         new UpdateRow(Table(engine, "shop.customers"), Row(("id", 1)), Row(("city", "One"))),
         new UpdateRow(Table(engine, "shop.customers"), Row(("id", 2)), Row(("city", "Two"))),
      ]));
      await cancel.CancelAsync();
      await Should.ThrowAsync<OperationCanceledException>(() => engine.CommitAsync(plan, cancel.Token));
      (await RowsAsync(engine, "shop.customers.where(id <= 2).orderBy(id).select(city)")).ShouldBe("'Cape Town'\n'Johannesburg'".ReplaceLineEndings());
   }

   [Fact]
   public async Task AFirstCommitFailureWritesNothingAcrossConnections()
   {
      await using TestSources sources = await (await new TestSources().AddSqliteAsync("shop", Fixtures.Sql("shop.sqlite.sql"))).AddDuckDbAsync("other", Fixtures.Sql("shop.duckdb.sql"));
      QueryEngine engine = sources.Engine(options: new QueryEngineOptions
      {
         BeforeCommit = s => s.Alias == "shop" ? throw new InvalidOperationException("boom") : ValueTask.CompletedTask,
      });
      DmlResult result = await CommitAsync(engine,
         new UpdateRow(Table(engine, "shop.customers"), Row(("id", 1)), Row(("city", "X"))),
         new UpdateRow(Table(engine, "other.customers"), Row(("id", 2)), Row(("city", "Y"))));
      result.Outcome.ShouldBe(DmlOutcome.RolledBack, result.ToString());
      (await RowsAsync(engine, "shop.customers.where(id == 1).select(city)")).ShouldBe("'Cape Town'");
      (await RowsAsync(engine, "other.customers.where(id == 2).select(city)")).ShouldBe("'Johannesburg'");
   }

   #endregion

   #region Ordering with real foreign keys

   [Fact]
   public async Task SqliteInsertsAcrossTablesAreOrderedParentsFirst()
   {
      await using TestSources sources = await new TestSources().AddSqliteAsync("shop", Fixtures.Sql("shop.sqlite.sql"), enforceForeignKeys: true);
      QueryEngine engine = sources.Engine();
      DmlResult result = await CommitAsync(engine,
         new InsertRow(Table(engine, "shop.order_lines"), Row(("order_id", 2001L), ("line_no", 1), ("product_code", "P-1"), ("qty", 1), ("price", 1m))),
         new InsertRow(Table(engine, "shop.orders"), Row(("id", 2001L), ("customer_id", 1), ("total", 1m), ("order_date", "2026-03-01"))));
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      (await RowsAsync(engine, "shop.order_lines.where(order_id == 2001).count()")).ShouldBe("1");
   }

   [Fact]
   public async Task SqliteDeletesAcrossTablesAreOrderedChildrenFirst()
   {
      await using TestSources sources = await new TestSources().AddSqliteAsync("shop", Fixtures.Sql("shop.sqlite.sql"), enforceForeignKeys: true);
      QueryEngine engine = sources.Engine();
      // Delete the order and its line in one change set: the line (child) must go first.
      DmlResult result = await CommitAsync(engine,
         new DeleteRow(Table(engine, "shop.orders"), Row(("id", 1003L))),
         new DeleteRow(Table(engine, "shop.order_lines"), Row(("order_id", 1003L), ("line_no", 1))));
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      (await RowsAsync(engine, "shop.orders.where(id == 1003).count()")).ShouldBe("0");
   }

   #endregion

   #region ResultRowEditor

   [Fact]
   public async Task EditThroughANavigationChangesBothEntities()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      QueryEngine engine = sources.Engine();
      (ResultSchema schema, object?[] row) = await FirstRowAsync(engine, "shop.orders.where(id == 1003).select(id, status, town: customer.city)");
      IReadOnlyList<UpdateRow> updates = ResultRowEditor.Update(schema, row, new Dictionary<int, object?> { [1] = "shipped", [2] = "Sandton" });
      updates.Select(u => u.Entity.DisplayName).OrderBy(x => x).ShouldBe(["shop.customers", "shop.orders"]);
      (await CommitAsync(engine, [.. updates])).Outcome.ShouldBe(DmlOutcome.Committed);
      (await RowsAsync(engine, "shop.orders.where(id == 1003).select(status, customer.city)")).ShouldBe("'shipped' | 'Sandton'");
   }

   [Fact]
   public async Task EditingAComputedResultColumnThrows()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      QueryEngine engine = sources.Engine();
      (ResultSchema schema, object?[] row) = await FirstRowAsync(engine, "shop.orders.where(id == 1001).select(id, doubled: total * 2)");
      Should.Throw<ArgumentException>(() => ResultRowEditor.Update(schema, row, new Dictionary<int, object?> { [1] = 5m }));
   }

   [Fact]
   public async Task DeleteViaRowIdentityRemovesTheRow()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      QueryEngine engine = sources.Engine();
      (ResultSchema schema, object?[] row) = await FirstRowAsync(engine, "shop.order_lines.where(order_id == 1001 and line_no == 1)");
      DeleteRow delete = ResultRowEditor.Delete(schema, row);
      delete.Key.Keys.OrderBy(k => k).ShouldBe(["line_no", "order_id"]);
      (await CommitAsync(engine, delete)).Outcome.ShouldBe(DmlOutcome.Committed);
      (await RowsAsync(engine, "shop.order_lines.where(order_id == 1001).select(line_no)")).ShouldBe("2");
   }

   [Fact]
   public async Task DeleteOfAnAggregateResultThrows()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      QueryEngine engine = sources.Engine();
      (ResultSchema schema, object?[] row) = await FirstRowAsync(engine, "shop.orders.groupBy(status).select(status, n: count())");
      Should.Throw<ArgumentException>(() => ResultRowEditor.Delete(schema, row));
   }

   /// <summary>A result that doesn't select the key still carries it as a hidden column, so edits find their row.</summary>
   [Fact]
   public async Task EditWithAHiddenKeyColumnFindsItsRow()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      QueryEngine engine = sources.Engine();
      (ResultSchema schema, object?[] row) = await FirstRowAsync(engine, "shop.customers.orderBy(id).select(city)");
      int ordinal = schema.Columns.First(c => c.Name == "city").Ordinal;
      IReadOnlyList<UpdateRow> updates = ResultRowEditor.Update(schema, row, new Dictionary<int, object?> { [ordinal] = "Gqeberha" });
      updates.Single().Key.ContainsKey("id").ShouldBeTrue("the hidden key column should be carried into the change");
      (await CommitAsync(engine, [.. updates])).Outcome.ShouldBe(DmlOutcome.Committed);
      (await RowsAsync(engine, "shop.customers.where(id == 1).select(city)")).ShouldBe("'Gqeberha'");
   }

   /// <summary>Editing a value that came through a navigation that was null (an absent related row) has no key, so it is refused.</summary>
   [Fact]
   public async Task EditThroughANullNavigationIsRefused()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      QueryEngine engine = sources.Engine();
      (ResultSchema schema, object?[] row) = await FirstRowAsync(engine, "shop.orders.where(id == 1004).select(id, shipCity: ship_address.city)");
      int ordinal = schema.Columns.First(c => c.Name == "shipCity").Ordinal;
      Should.Throw<ArgumentException>(() => ResultRowEditor.Update(schema, row, new Dictionary<int, object?> { [ordinal] = "Nowhere" }));
   }

   [Fact]
   public async Task EditingTheSameColumnTwiceWithDifferentValuesThrows()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      QueryEngine engine = sources.Engine();
      // Select the same table column under two aliases, then give them conflicting edits.
      (ResultSchema schema, object?[] row) = await FirstRowAsync(engine, "shop.customers.where(id == 1).select(a: city, b: city)");
      int a = schema.Columns.First(c => c.Name == "a").Ordinal;
      int b = schema.Columns.First(c => c.Name == "b").Ordinal;
      Should.Throw<ArgumentException>(() => ResultRowEditor.Update(schema, row, new Dictionary<int, object?> { [a] = "X", [b] = "Y" }));
   }

   private static async Task<(ResultSchema Schema, object?[] Row)> FirstRowAsync(QueryEngine engine, string query)
   {
      await using QueryResult result = await engine.ExecuteAsync(new QueryRequest(query), Token);
      (await result.ReadAsync(Token)).ShouldBeTrue();
      return (result.Schema, result.Current);
   }

   #endregion

   #region Guard execution: smuggled statements are refused, and nothing runs

   [Theory]
   [InlineData("UPDATE customers SET city = 'x' WHERE id = 1;\nDROP TABLE order_lines")]
   [InlineData("PRAGMA foreign_keys = OFF")]
   [InlineData("ATTACH DATABASE 'other.db' AS o")]
   [InlineData("DELETE FROM customers WHERE id = 1; VACUUM")]
   public async Task SqliteGuardRefusesSmuggledStatementsAndWritesNothing(string script)
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      QueryEngine engine = sources.Engine();
      SourceInfo shop = engine.Catalog.FindSource("shop")!;
      Should.Throw<DmlScriptException>(() => engine.ParseScript(shop, script));
      // The refused script never ran: the first customer is untouched.
      (await RowsAsync(engine, "shop.customers.where(id == 1).select(city)")).ShouldBe("'Cape Town'");
      (await RowsAsync(engine, "shop.customers.count()")).ShouldBe("3");
   }

   [Theory]
   [InlineData("COPY customers TO 'out.csv'")]
   [InlineData("INSTALL httpfs")]
   [InlineData("INSERT INTO customers SELECT * FROM read_csv('x.csv')")]
   public async Task DuckDbGuardRefusesSmuggledStatements(string script)
   {
      await using TestSources sources = await TestSources.DuckDbShopAsync();
      QueryEngine engine = sources.Engine();
      SourceInfo shop = engine.Catalog.FindSource("shop")!;
      Should.Throw<DmlScriptException>(() => engine.ParseScript(shop, script));
   }

   [Theory]
   [InlineData(ServerKind.Postgres, "TRUNCATE orders")]
   [InlineData(ServerKind.Postgres, "UPDATE orders SET total = pg_read_file('/etc/passwd')::numeric WHERE id = 1001")]
   [InlineData(ServerKind.SqlServer, "UPDATE orders SET total = 1 WHERE id = 1001; DROP TABLE order_lines")]
   [InlineData(ServerKind.SqlServer, "UPDATE orders SET total = 1 WHERE id = 1001 EXEC sp_who")]
   public async Task ServerGuardRefusesSmuggledStatements(ServerKind server, string script)
   {
      ServerDatabase database = await servers.FreshAsync(server);
      await using TestSources sources = await database.SourcesAsync();
      QueryEngine engine = sources.Engine();
      SourceInfo shop = engine.Catalog.FindSource("shop")!;
      Should.Throw<DmlScriptException>(() => engine.ParseScript(shop, script));
   }

   [Fact]
   public async Task AnEditedScriptThatOnlyChangesDataRuns()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      QueryEngine engine = sources.Engine();
      SourceInfo shop = engine.Catalog.FindSource("shop")!;
      DmlScript script = engine.ParseScript(shop, "UPDATE customers SET city = 'Paarl' WHERE id = 1;\nDELETE FROM order_lines WHERE order_id = 1001 AND line_no = 2");
      (await engine.CommitAsync([script], Token)).Outcome.ShouldBe(DmlOutcome.Committed);
      (await RowsAsync(engine, "shop.customers.where(id == 1).select(city)")).ShouldBe("'Paarl'");
   }

   #endregion

   #region Lengths, keys and schemas

   /// <summary>
   /// A value the planner accepts fits its column as the database counts lengths: SQL Server's nvarchar(n) counts
   /// UTF-16 units, so a character outside the BMP takes two.
   /// </summary>
   [Fact]
   public async Task SqlServerNvarcharAcceptsEveryValueThePlannerDoes()
   {
      ServerDatabase database = await servers.FreshAsync(ServerKind.SqlServer, "shop",
         then: "CREATE TABLE tiny (id int CONSTRAINT pk_tiny PRIMARY KEY, v nvarchar(5));\nINSERT INTO tiny VALUES (1, 'x');");
      await using TestSources sources = await database.SourcesAsync();
      QueryEngine engine = sources.Engine();
      string fiveAstral = "\U0001F600\U0001F601\U0001F602\U0001F603\U0001F604"; // 5 runes, 10 UTF-16 code units
      DmlPlan plan = engine.PlanChanges(new ChangeSet([new UpdateRow(Table(engine, "shop.tiny"), Row(("id", 1)), Row(("v", fiveAstral)))]));
      plan.Issues.Single().Message.ShouldEndWith("takes text of at most 5 characters; '\U0001F600\U0001F601\U0001F602\U0001F603\U0001F604' has 10");
      plan = engine.PlanChanges(new ChangeSet([new UpdateRow(Table(engine, "shop.tiny"), Row(("id", 1)), Row(("v", "\U0001F600\U0001F601a")))]));
      DmlResult result = await engine.CommitAsync(plan, Token);
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
   }

   [Fact]
   public async Task PostgresVarcharAcceptsEveryValueThePlannerDoes()
   {
      ServerDatabase database = await servers.FreshAsync(ServerKind.Postgres, "shop",
         then: "CREATE TABLE tiny (id int PRIMARY KEY, v varchar(5));\nINSERT INTO tiny VALUES (1, 'x');");
      await using TestSources sources = await database.SourcesAsync();
      QueryEngine engine = sources.Engine();
      string fiveAstral = "\U0001F600\U0001F601\U0001F602\U0001F603\U0001F604";
      DmlPlan plan = engine.PlanChanges(new ChangeSet([new UpdateRow(Table(engine, "shop.tiny"), Row(("id", 1)), Row(("v", fiveAstral)))]));
      plan.Issues.ShouldBeEmpty();
      (await engine.CommitAsync(plan, Token)).Outcome.ShouldBe(DmlOutcome.Committed, "PostgreSQL varchar(n) counts characters, as the planner does");
   }

   [Fact]
   public async Task SqliteDatetimeKeyFindsItsRow()
   {
      await using TestSources sources = await new TestSources().AddSqliteAsync("s", """
         CREATE TABLE ev (at DATETIME PRIMARY KEY, note TEXT);
         INSERT INTO ev VALUES ('2026-03-01 13:45:30', 'x');
         """);
      QueryEngine engine = sources.Engine();
      DmlResult result = await CommitAsync(engine, new UpdateRow(Table(engine, "s.ev"), Row(("at", new DateTime(2026, 3, 1, 13, 45, 30))), Row(("note", "y"))));
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      (await RowsAsync(engine, "s.ev.select(note)")).ShouldBe("'y'");
   }

   [Fact]
   public async Task SqliteDatetimeKeyStoredWithT_FindsItsRow()
   {
      await using TestSources sources = await new TestSources().AddSqliteAsync("s", """
         CREATE TABLE ev (at DATETIME PRIMARY KEY, note TEXT);
         INSERT INTO ev VALUES ('2026-03-01T13:45:30', 'x');
         """);
      QueryEngine engine = sources.Engine();
      DmlResult result = await CommitAsync(engine, new UpdateRow(Table(engine, "s.ev"), Row(("at", new DateTime(2026, 3, 1, 13, 45, 30))), Row(("note", "y"))));
      result.Outcome.ShouldBe(DmlOutcome.Committed, "a datetime key stored with a 'T' separator is still found");
      (await RowsAsync(engine, "s.ev.select(note)")).ShouldBe("'y'");
   }

   [Fact]
   public async Task DuckDbIntervalEditedScriptMatchesParameterized()
   {
      Task<TestSources> MakeAsync() => new TestSources().AddDuckDbAsync("s", """
         CREATE TABLE iv (id INTEGER PRIMARY KEY, span INTERVAL);
         INSERT INTO iv VALUES (1, NULL);
         """);
      (string parameterized, string edited) = await ParameterizedAndEditedAsync(
         MakeAsync, "s",
         e => new UpdateRow(Table(e, "s.iv"), Row(("id", 1)), Row(("span", new TimeSpan(0, 2, 3, 4)))),
         "s.iv.where(id == 1).select(span)");
      edited.ShouldBe(parameterized);
   }

   [Fact]
   public async Task DuckDbIntervalIsFoundByItsOriginal()
   {
      await using TestSources sources = await new TestSources().AddDuckDbAsync("s", """
         CREATE TABLE iv (id INTEGER PRIMARY KEY, span INTERVAL, note VARCHAR);
         INSERT INTO iv VALUES (1, NULL, 'x');
         """);
      QueryEngine engine = sources.Engine();
      TimeSpan span = new(0, 2, 3, 4);
      (await CommitAsync(engine, new UpdateRow(Table(engine, "s.iv"), Row(("id", 1)), Row(("span", span))))).Outcome.ShouldBe(DmlOutcome.Committed);
      Dictionary<string, object?> read = await ReadRowAsync(engine, "s.iv", 1);
      DmlResult again = await CommitAsync(engine, new UpdateRow(Table(engine, "s.iv"), Row(("id", 1)), Row(("note", "y"))) { Original = Row(("span", read["span"])) });
      again.Outcome.ShouldBe(DmlOutcome.Committed, again.ToString());
   }

   [Fact]
   public async Task DuckDbChangeInANonDefaultSchemaWorks()
   {
      await using TestSources sources = await TestSources.DuckDbShopAsync();
      QueryEngine engine = sources.Engine();
      TableEntity contacts = engine.Catalog.FindEntity(EntityName.Parse("shop.crm.contacts")) as TableEntity
         ?? throw new Xunit.Sdk.XunitException("crm.contacts not found; adjust the entity path");
      DmlResult result = await CommitAsync(engine, new UpdateRow(contacts, Row(("id", "2f1c0000-0000-4000-8000-000000000001")), Row(("email", "ann@new.test"))));
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      (await RowsAsync(engine, "shop.crm.contacts.select(email)")).ShouldBe("'ann@new.test'");
   }

   #endregion
}
