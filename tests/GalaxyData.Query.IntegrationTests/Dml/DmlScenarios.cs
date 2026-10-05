using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Dml;
using GalaxyData.Query.Execution;
using GalaxyData.Query.IntegrationTests.Execution;
using GalaxyData.Query.Results;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Dml;

/// <summary>
/// Changes written to the shop fixture, the same for every provider: the sources have a fresh, writable shop as
/// <c>shop</c>, and, for changes across connections, another as <c>other</c> (of another provider, where there is one).
/// </summary>
internal static class DmlScenarios
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   public static Dictionary<string, object?> Row(params (string Column, object? Value)[] values) =>
      values.ToDictionary(v => v.Column, v => v.Value, StringComparer.Ordinal);

   public static TableEntity Table(QueryEngine engine, string path) =>
      engine.Catalog.FindEntity(EntityName.Parse(path)) as TableEntity ?? throw new KeyNotFoundException($"No table {path}");

   /// <summary>A query's rows as <see cref="TestSources.Format"/> writes them, without the last line break.</summary>
   public static async Task<string> RowsAsync(QueryEngine engine, string query)
   {
      await using QueryResult result = await engine.ExecuteAsync(new QueryRequest(query), Token);
      return TestSources.Format(await result.ToListAsync(Token), result.Schema).TrimEnd();
   }

   public static async Task<DmlResult> CommitAsync(QueryEngine engine, params RowChange[] changes)
   {
      DmlPlan plan = engine.PlanChanges(new ChangeSet(changes));
      plan.Issues.ShouldBeEmpty();
      return await engine.CommitAsync(plan, Token);
   }

   /// <summary>Inserts give back the rows the database made, identity and defaults included; lines given before their order go after it.</summary>
   public static async Task InsertsGiveBackTheirRowsAsync(TestSources sources)
   {
      QueryEngine engine = sources.Engine();
      DmlResult result = await CommitAsync(engine,
         new InsertRow(Table(engine, "shop.order_lines"), Row(("order_id", 2001L), ("line_no", 1), ("product_code", "P-100"), ("qty", 2), ("price", "6.25"))),
         new InsertRow(Table(engine, "shop.orders"), Row(("id", 2001L), ("customer_id", 1), ("total", 12.5m), ("order_date", "2026-03-01"))),
         new InsertRow(Table(engine, "shop.customers"), Row(("name", "Delta Ltd"), ("city", "Durban"), ("credit_limit", "750.5"))));
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      result.Scripts.Single().Statements.Select(s => s.Statement.ChangeIndex).ShouldBe([2, 1, 0]);
      result.Scripts.Single().Statements.ShouldAllBe(s => s.RowsChanged == 1);

      DmlStatementResult customer = result.ForChange(2)!;
      object id = customer.Value("id").ShouldNotBeNull();
      customer.Value("name").ShouldBe("Delta Ltd");
      customer.Value("credit_limit").ShouldBe(750.50m);
      customer.Value("created_at").ShouldNotBeNull();
      (await RowsAsync(engine, "shop.customers.where(name == 'Delta Ltd').select(id, city)"))
         .ShouldBe($"{Convert.ToString(id, CultureInfo.InvariantCulture)} | 'Durban'");
      result.ForChange(1)!.Value("status").ShouldBe("open");
      Convert.ToInt64(result.ForChange(0)!.Value("qty"), CultureInfo.InvariantCulture).ShouldBe(2);
      (await RowsAsync(engine, "shop.order_lines.where(order_id == 2001).select(line_no, qty, price)")).ShouldBe("1 | 2 | 6.25");
   }

   /// <summary>Updates and deletes find their row by its key, and change it when its original values still hold.</summary>
   public static async Task UpdatesAndDeletesFindTheirRowsAsync(TestSources sources)
   {
      QueryEngine engine = sources.Engine();
      DmlResult result = await CommitAsync(engine,
         new UpdateRow(Table(engine, "shop.orders"), Row(("id", 1001L)), Row(("total", 260m), ("placed_at", null)))
         {
            Original = Row(("status", "open"), ("total", 250.00m), ("ship_address_id", 1), ("order_date", new DateOnly(2026, 1, 5)),
                           ("placed_at", new DateTimeOffset(2026, 1, 5, 8, 30, 0, TimeSpan.Zero))),
         },
         new UpdateRow(Table(engine, "shop.customers"), Row(("id", 3)), Row(("city", "Pretoria"), ("credit_limit", 100m))) { Original = Row(("city", null), ("credit_limit", null)) },
         new DeleteRow(Table(engine, "shop.order_lines"), Row(("order_id", 1001L), ("line_no", 2))) { Original = Row(("product_code", "P-200"), ("qty", 1), ("price", 50.00m)) });
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      result.Scripts.Single().Statements.ShouldAllBe(s => s.RowsChanged == 1);
      (await RowsAsync(engine, "shop.orders.where(id == 1001).select(total, placed_at)")).ShouldBe("260.00 | null");
      (await RowsAsync(engine, "shop.customers.where(id == 3).select(city, credit_limit)")).ShouldBe("'Pretoria' | 100.00");
      (await RowsAsync(engine, "shop.order_lines.where(order_id == 1001).select(line_no)")).ShouldBe("1");
   }

   /// <summary>A row changed since it was read stops every change: nothing is written.</summary>
   public static async Task AConflictWritesNothingAsync(TestSources sources)
   {
      QueryEngine engine = sources.Engine();
      DmlResult result = await CommitAsync(engine,
         new UpdateRow(Table(engine, "shop.orders"), Row(("id", 1001L)), Row(("total", 1m))) { Original = Row(("status", "open")) },
         new UpdateRow(Table(engine, "shop.orders"), Row(("id", 1002L)), Row(("total", 1m))) { Original = Row(("status", "open")) });
      result.Outcome.ShouldBe(DmlOutcome.RolledBack);
      result.Failure!.Kind.ShouldBe(DmlFailureKind.Conflict);
      result.Failure.Statement!.ChangeIndex.ShouldBe(1);
      result.Failure.Message.ShouldEndWith("the update of shop.orders (id = 1002) found no row: it was changed or deleted since it was read");
      result.Scripts.Single().Status.ShouldBe(DmlScriptStatus.RolledBack);
      (await RowsAsync(engine, "shop.orders.where(id <= 1002).orderBy(id).select(status, total)")).ShouldBe("'open' | 250.00\n'shipped' | 99.50".ReplaceLineEndings());
   }

   /// <summary>A statement the database rejects stops every change, and says why.</summary>
   public static async Task AFailingStatementWritesNothingAsync(TestSources sources)
   {
      QueryEngine engine = sources.Engine();
      DmlResult result = await CommitAsync(engine,
         new UpdateRow(Table(engine, "shop.orders"), Row(("id", 1001L)), Row(("total", 1m))),
         new InsertRow(Table(engine, "shop.customers"), Row(("name", "Acme Ltd"))));
      result.Outcome.ShouldBe(DmlOutcome.RolledBack);
      result.Failure!.Kind.ShouldBe(DmlFailureKind.Statement);
      result.Failure.Message.ShouldContain("failed to run the insert into shop.customers: ");
      result.Failure.Exception.ShouldNotBeNull();
      (await RowsAsync(engine, "shop.orders.where(id == 1001).select(total)")).ShouldBe("250.00");
      (await RowsAsync(engine, "shop.customers.count()")).ShouldBe("3");
   }

   /// <summary>Changes of two connections commit together; a conflict on either writes nothing on both.</summary>
   public static async Task ChangesAcrossConnectionsCommitTogetherAsync(TestSources sources)
   {
      QueryEngine engine = sources.Engine();
      DmlPlan plan = engine.PlanChanges(new ChangeSet(
      [
         new UpdateRow(Table(engine, "shop.orders"), Row(("id", 1001L)), Row(("total", 260m))) { Original = Row(("status", "open")) },
         new UpdateRow(Table(engine, "other.orders"), Row(("id", 1002L)), Row(("total", 100m))) { Original = Row(("status", "shipped"), ("total", 99.50m)) },
      ]));
      plan.IsMultiConnection.ShouldBeTrue();
      DmlResult result = await engine.CommitAsync(plan, Token);
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      result.Scripts.Select(s => (s.Script.Source.Alias, s.Status)).ShouldBe([("shop", DmlScriptStatus.Committed), ("other", DmlScriptStatus.Committed)]);
      (await RowsAsync(engine, "shop.orders.where(id == 1001).select(total)")).ShouldBe("260.00");
      (await RowsAsync(engine, "other.orders.where(id == 1002).select(total)")).ShouldBe("100.00");

      result = await CommitAsync(engine,
         new UpdateRow(Table(engine, "shop.customers"), Row(("id", 1)), Row(("city", "Paarl"))),
         new UpdateRow(Table(engine, "other.customers"), Row(("id", 2)), Row(("city", "Soweto"))) { Original = Row(("city", "Nowhere")) });
      result.Outcome.ShouldBe(DmlOutcome.RolledBack);
      result.Failure!.Source.Alias.ShouldBe("other");
      result.Scripts.ShouldAllBe(s => s.Status == DmlScriptStatus.RolledBack);
      (await RowsAsync(engine, "shop.customers.where(id == 1).select(city)")).ShouldBe("'Cape Town'");

      // An edited script on one connection, a planned one on the other.
      DmlScript edited = engine.ParseScript(engine.Catalog.FindSource("shop")!, "UPDATE customers SET city = 'Paarl' WHERE id = 1");
      DmlPlan planned = engine.PlanChanges(new ChangeSet([new UpdateRow(Table(engine, "other.customers"), Row(("id", 2)), Row(("city", "Soweto")))]));
      result = await engine.CommitAsync([edited, .. planned.Scripts], Token);
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      (await RowsAsync(engine, "shop.customers.where(id == 1).select(city)")).ShouldBe("'Paarl'");
      (await RowsAsync(engine, "other.customers.where(id == 2).select(city)")).ShouldBe("'Soweto'");
   }

   /// <summary>
   /// A commit that fails after another connection's succeeded leaves the changes partly written, and says so; when
   /// the first fails, nothing is.
   /// </summary>
   public static async Task AFailedCommitIsReportedAsync(TestSources sources)
   {
      string? failing = null;
      QueryEngine engine = sources.Engine(options: new QueryEngineOptions
      {
         BeforeCommit = source => source.Alias == failing ? throw new InvalidOperationException("The connection was lost") : ValueTask.CompletedTask,
      });
      RowChange[] Changes(long id) =>
      [
         new UpdateRow(Table(engine, "shop.orders"), Row(("id", id)), Row(("total", 1m))),
         new UpdateRow(Table(engine, "other.orders"), Row(("id", id)), Row(("total", 1m))),
      ];

      failing = "other";
      DmlResult result = await CommitAsync(engine, Changes(1001));
      result.Outcome.ShouldBe(DmlOutcome.PartiallyCommitted, result.ToString());
      result.Failure!.Kind.ShouldBe(DmlFailureKind.Commit);
      result.Failure.Message.ShouldContain("failed to commit after shop had: their changes are written, and the others' aren't: The connection was lost");
      result.Scripts.Select(s => s.Status).ShouldBe([DmlScriptStatus.Committed, DmlScriptStatus.CommitFailed]);
      result.Scripts[1].Error.ShouldBe("The connection was lost");
      (await RowsAsync(engine, "shop.orders.where(id == 1001).select(total)")).ShouldBe("1.00");
      (await RowsAsync(engine, "other.orders.where(id == 1001).select(total)")).ShouldBe("250.00");

      failing = "shop";
      result = await CommitAsync(engine, Changes(1003));
      result.Outcome.ShouldBe(DmlOutcome.RolledBack, result.ToString());
      result.Failure!.Message.ShouldContain("failed to commit, so nothing was: The connection was lost");
      result.Scripts.Select(s => s.Status).ShouldBe([DmlScriptStatus.CommitFailed, DmlScriptStatus.RolledBack]);
      (await RowsAsync(engine, "shop.orders.where(id == 1003).select(total)")).ShouldBe("12.25");
      (await RowsAsync(engine, "other.orders.where(id == 1003).select(total)")).ShouldBe("12.25");
   }

   /// <summary>A script as shown, edited, runs; one that does more than change data doesn't, unless an administrator allows it.</summary>
   public static async Task EditedScriptsRunWhenTheyOnlyChangeDataAsync(TestSources sources)
   {
      QueryEngine engine = sources.Engine();
      SourceInfo shop = engine.Catalog.FindSource("shop")!;
      DmlPlan plan = engine.PlanChanges(new ChangeSet(
      [
         new UpdateRow(Table(engine, "shop.orders"), Row(("id", 1003L)), Row(("total", 20m))) { Original = Row(("status", "open")) },
         new DeleteRow(Table(engine, "shop.order_lines"), Row(("order_id", 1003L), ("line_no", 1))),
      ]));
      string text = plan.Scripts.Single().ToDisplayText().Replace("SET total = 20.00", "SET total = 21.00", StringComparison.Ordinal);
      DmlScript script = engine.ParseScript(shop, text);
      script.IsEdited.ShouldBeTrue();
      script.Statements.Select(s => s.Kind).ShouldBe([DmlStatementKind.Update, DmlStatementKind.Delete]);
      script.Statements.ShouldAllBe(s => s.ExpectedRows == null);
      DmlResult result = await engine.CommitAsync([script], Token);
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      result.Scripts.Single().Statements.Select(s => s.RowsChanged).ShouldBe([1L, 1L]);
      (await RowsAsync(engine, "shop.orders.where(id == 1003).select(total, lines: order_lines.count())")).ShouldBe("21.00 | 0");

      DmlScriptException rejected = Should.Throw<DmlScriptException>(() => engine.ParseScript(shop, "UPDATE orders SET status = 'x' WHERE id = 1001;\nDROP TABLE order_lines"));
      rejected.Problems.Single().Line.ShouldBe(2);
      rejected.Problems.Single().Message.ShouldStartWith("DROP statements can't run here");

      DmlScript any = engine.ParseScript(shop, "CREATE TABLE scratch (a int);\nINSERT INTO scratch (a) VALUES (1)", allowAnyStatement: true);
      any.Statements.Select(s => s.Kind).ShouldBe([DmlStatementKind.Other, DmlStatementKind.Insert]);
      (await engine.CommitAsync([any], Token)).Outcome.ShouldBe(DmlOutcome.Committed);
   }

   /// <summary>
   /// Line breaks in values are written by their codes in a script's text, so the script, its own line breaks made
   /// another kind (as an editor may), finds the row by them and writes them as they are.
   /// </summary>
   public static async Task LineBreaksInValuesStayAsTheyAreInEditedScriptsAsync(TestSources sources)
   {
      QueryEngine engine = sources.Engine();
      SourceInfo shop = engine.Catalog.FindSource("shop")!;
      const string before = "Cape\nTown's", after = "Port\r\nElizabeth\rEast\n";
      (await CommitAsync(engine, new UpdateRow(Table(engine, "shop.customers"), Row(("id", 1L)), Row(("city", before))))).Outcome.ShouldBe(DmlOutcome.Committed);
      DmlPlan plan = engine.PlanChanges(new ChangeSet(
         [new UpdateRow(Table(engine, "shop.customers"), Row(("id", 1L)), Row(("city", after))) { Original = Row(("city", before)) }]));
      string text = plan.Scripts.Single().ToDisplayText();
      text.ShouldNotContain("Cape\n");
      text.ShouldNotContain("Port\r");
      foreach (string breaks in new[] { "\n", "\r\n" })
      {
         DmlScript script = engine.ParseScript(shop, text.ReplaceLineEndings(breaks));
         script.IsEdited.ShouldBeTrue();
         DmlResult result = await engine.CommitAsync([script], Token);
         result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
         result.Scripts.Single().Statements.Single().RowsChanged.ShouldBe(1L, breaks.Length == 1 ? "the row is found by its city, the script's line breaks LF" : "the row is found by its city, the script's line breaks CRLF");
         (await FirstRowAsync(engine, "shop.customers.where(id == 1).select(city)")).Row[0].ShouldBe(after);
         (await CommitAsync(engine, new UpdateRow(Table(engine, "shop.customers"), Row(("id", 1L)), Row(("city", before))))).Outcome.ShouldBe(DmlOutcome.Committed);
      }
   }

   /// <summary>Edits of a query's rows become changes of the rows they came from, through the result's edit targets.</summary>
   public static async Task EditsOfResultRowsChangeTheirTablesAsync(TestSources sources)
   {
      QueryEngine engine = sources.Engine();
      (ResultSchema schema, object?[] row) = await FirstRowAsync(engine, "shop.orders.where(id == 1003).select(id, status, total, town: customer.city)");
      IReadOnlyList<UpdateRow> updates = ResultRowEditor.Update(schema, row, new Dictionary<int, object?> { [2] = 13m, [3] = "Sandton" });
      updates.Select(u => u.Entity.DisplayName).ShouldBe(["shop.orders", "shop.customers"]);
      updates[1].Original.ShouldBe(Row(("city", "Johannesburg")));
      DmlResult result = await CommitAsync(engine, [.. updates]);
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      (await RowsAsync(engine, "shop.orders.where(id == 1003).select(total, customer.city)")).ShouldBe("13.00 | 'Sandton'");

      // The row read before someone else changed it: its edit finds no row, and nothing is written.
      (schema, row) = await FirstRowAsync(engine, "shop.customers.where(id == 1).select(id, city)");
      await sources.RunAsync("shop", "UPDATE customers SET city = 'Stellenbosch' WHERE id = 1");
      DmlResult stale = await CommitAsync(engine, [.. ResultRowEditor.Update(schema, row, new Dictionary<int, object?> { [1] = "Paarl" })]);
      stale.Failure!.Kind.ShouldBe(DmlFailureKind.Conflict);
      (await RowsAsync(engine, "shop.customers.where(id == 1).select(city)")).ShouldBe("'Stellenbosch'");

      (schema, row) = await FirstRowAsync(engine, "shop.order_lines.where(order_id == 1001 and line_no == 1)");
      DeleteRow delete = ResultRowEditor.Delete(schema, row);
      delete.Key.Keys.ShouldBe(["order_id", "line_no"]);
      (await CommitAsync(engine, delete)).Outcome.ShouldBe(DmlOutcome.Committed);
      (await RowsAsync(engine, "shop.order_lines.where(order_id == 1001).select(line_no)")).ShouldBe("2");
   }

   /// <summary>Cancelled before it commits, nothing is written.</summary>
   public static async Task CancellingWritesNothingAsync(TestSources sources)
   {
      QueryEngine engine = sources.Engine();
      DmlPlan plan = engine.PlanChanges(new ChangeSet([new UpdateRow(Table(engine, "shop.orders"), Row(("id", 1001L)), Row(("status", "shipped")))]));
      using CancellationTokenSource cancelled = new();
      await cancelled.CancelAsync();
      await Should.ThrowAsync<OperationCanceledException>(() => engine.CommitAsync(plan, cancelled.Token));
      (await RowsAsync(engine, "shop.orders.where(id == 1001).select(status)")).ShouldBe("'open'");
   }

   private static async Task<(ResultSchema Schema, object?[] Row)> FirstRowAsync(QueryEngine engine, string query)
   {
      await using QueryResult result = await engine.ExecuteAsync(new QueryRequest(query), Token);
      (await result.ReadAsync(Token)).ShouldBeTrue();
      return (result.Schema, result.Current);
   }
}
