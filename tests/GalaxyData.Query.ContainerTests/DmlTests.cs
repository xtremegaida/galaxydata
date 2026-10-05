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

/// <summary>Changes written to PostgreSQL and SQL Server: the shared scenarios, and what is particular to each. Each test has databases of its own.</summary>
public sealed class DmlTests(Servers servers)
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   /// <summary>The shop on the server as <c>shop</c>, and on the other server as <c>other</c>.</summary>
   private async Task<TestSources> SourcesAsync(ServerKind server)
   {
      ServerDatabase shop = await servers.FreshAsync(server);
      ServerDatabase other = await servers.FreshAsync(server == ServerKind.Postgres ? ServerKind.SqlServer : ServerKind.Postgres);
      return await other.AddToAsync(await shop.SourcesAsync(), "other");
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task InsertsGiveBackTheirRows(ServerKind server)
   {
      await using TestSources sources = await SourcesAsync(server);
      await InsertsGiveBackTheirRowsAsync(sources);
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task UpdatesAndDeletesFindTheirRows(ServerKind server)
   {
      await using TestSources sources = await SourcesAsync(server);
      await UpdatesAndDeletesFindTheirRowsAsync(sources);
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task AConflictWritesNothing(ServerKind server)
   {
      await using TestSources sources = await SourcesAsync(server);
      await AConflictWritesNothingAsync(sources);
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task AFailingStatementWritesNothing(ServerKind server)
   {
      await using TestSources sources = await SourcesAsync(server);
      await AFailingStatementWritesNothingAsync(sources);
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task ChangesAcrossConnectionsCommitTogether(ServerKind server)
   {
      await using TestSources sources = await SourcesAsync(server);
      await ChangesAcrossConnectionsCommitTogetherAsync(sources);
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task AFailedCommitIsReported(ServerKind server)
   {
      await using TestSources sources = await SourcesAsync(server);
      await AFailedCommitIsReportedAsync(sources);
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task EditedScriptsRunWhenTheyOnlyChangeData(ServerKind server)
   {
      await using TestSources sources = await SourcesAsync(server);
      await EditedScriptsRunWhenTheyOnlyChangeDataAsync(sources);
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task LineBreaksInValuesStayAsTheyAreInEditedScripts(ServerKind server)
   {
      await using TestSources sources = await SourcesAsync(server);
      await LineBreaksInValuesStayAsTheyAreInEditedScriptsAsync(sources);
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task EditsOfResultRowsChangeTheirTables(ServerKind server)
   {
      await using TestSources sources = await SourcesAsync(server);
      await EditsOfResultRowsChangeTheirTablesAsync(sources);
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task CancellingWritesNothing(ServerKind server)
   {
      await using TestSources sources = await SourcesAsync(server);
      await CancellingWritesNothingAsync(sources);
   }

   /// <summary>
   /// A value of each type is written as it was read, and its row is found again by every value it has: the original
   /// values a change checks equal the stored ones (SQL Server's datetime, PostgreSQL's char(n) and enums, money,
   /// offsets). A row version stands for all of them, and changes with the row.
   /// </summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task AValueOfEachTypeIsWrittenAndItsRowFoundByIt(ServerKind server)
   {
      ServerDatabase database = await servers.FreshAsync(server, "kinds");
      await using TestSources sources = await database.SourcesAsync("k");
      QueryEngine engine = sources.Engine();
      TableEntity kinds = Table(engine, "k.kinds");
      List<ColumnDef> writable = kinds.Columns.Where(c => !c.IsKey && !c.IsRowVersion && !c.IsComputed && c.Type.Kind != Types.ScalarKind.Unknown).ToList();
      Dictionary<string, object?> filled = await ValuesAsync(engine, 1);
      Dictionary<string, object?> empty = await ValuesAsync(engine, 2);

      // Row 2's nulls become row 1's values, with its nulls as the originals.
      DmlResult result = await CommitAsync(engine, new UpdateRow(kinds, Row(("id", 2)), writable.ToDictionary(c => c.Name, c => filled[c.Name]))
      {
         Original = writable.ToDictionary(c => c.Name, c => empty[c.Name]),
      });
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      string Written(Dictionary<string, object?> values) => string.Join(", ", writable.Select(c => $"{c.Name} = {TestSources.Format([[values[c.Name]]]).TrimEnd()}"));
      Dictionary<string, object?> copied = await ValuesAsync(engine, 2);
      Written(copied).ShouldBe(Written(filled));

      // Every value it has finds the row again (those of unknown types aren't compared).
      Dictionary<string, object?> original = copied.Where(v => v.Key != "id" && kinds.FindColumn(v.Key).Item is { IsRowVersion: false }).ToDictionary();
      result = await CommitAsync(engine, new UpdateRow(kinds, Row(("id", 2)), Row(("i32", 7))) { Original = original });
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      string where = result.Scripts[0].Script.Statements[0].ToDisplayText().Replace("\"", "").Replace("[", "").Replace("]", "");
      foreach (string column in new[] { "dt", "dto", "en", "ch", "mo", "dec", "vc" }.Where(c => kinds.FindColumn(c).IsFound && writable.Any(w => w.Name == c)))
      {
         where.ShouldContain($"{column} = ", Case.Sensitive, $"{column} is compared");
      }

      if (kinds.Columns.FirstOrDefault(c => c.IsRowVersion) is { } version)
      {
         // The row version alone stands for every value; once the row has changed, the one read before finds nothing.
         object? stale = copied[version.Name];
         DmlResult changed = await CommitAsync(engine, new UpdateRow(kinds, Row(("id", 2)), Row(("i32", 8))) { Original = Row((version.Name, stale)) });
         changed.Outcome.ShouldBe(DmlOutcome.RolledBack);
         changed.Failure!.Kind.ShouldBe(DmlFailureKind.Conflict);
         object? current = (await ValuesAsync(engine, 2))[version.Name];
         result = await CommitAsync(engine, new UpdateRow(kinds, Row(("id", 2)), Row(("i32", 8))) { Original = Row((version.Name, current), ("i16", 999)) });
         result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      }
      result = await CommitAsync(engine, new DeleteRow(kinds, Row(("id", 2))) { Original = (await ValuesAsync(engine, 2)).Where(v => v.Key != "id").ToDictionary() });
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      (await RowsAsync(engine, "k.kinds.count()")).ShouldBe("1");
   }

   private static async Task<Dictionary<string, object?>> ValuesAsync(QueryEngine engine, int id)
   {
      await using QueryResult result = await engine.ExecuteAsync(new QueryRequest($"k.kinds.where(id == {id})"), Token);
      (await result.ReadAsync(Token)).ShouldBeTrue();
      return result.Schema.VisibleColumns.ToDictionary(c => c.Name, c => result.Current[c.Ordinal]);
   }

   /// <summary>PostgreSQL's deferred constraints are checked before any connection commits, so a change that breaks one writes nothing anywhere.</summary>
   [Fact]
   public async Task PostgresChecksDeferredConstraintsBeforeAnyConnectionCommits()
   {
      ServerDatabase postgres = await servers.FreshAsync(ServerKind.Postgres, then: """
         ALTER TABLE order_lines DROP CONSTRAINT order_lines_order_id_fkey;
         ALTER TABLE order_lines ADD CONSTRAINT order_lines_order_id_fkey FOREIGN KEY (order_id) REFERENCES orders(id) DEFERRABLE INITIALLY DEFERRED;
         """);
      await using TestSources sources = await postgres.AddToAsync(await TestSources.SqliteShopAsync(), "other");
      QueryEngine engine = sources.Engine();
      DmlResult result = await CommitAsync(engine,
         new UpdateRow(Table(engine, "shop.customers"), Row(("id", 1)), Row(("city", "Paarl"))),
         new InsertRow(Table(engine, "other.order_lines"), Row(("order_id", 9999L), ("line_no", 1), ("product_code", "P-100"), ("qty", 1), ("price", 1m))));
      result.Outcome.ShouldBe(DmlOutcome.RolledBack, result.ToString());
      result.Failure!.Message.ShouldStartWith("other (PostgreSQL) can't commit the changes: 23503: insert or update on table \"order_lines\" violates foreign key constraint");
      (await RowsAsync(engine, "shop.customers.where(id == 1).select(city)")).ShouldBe("'Cape Town'");
      (await RowsAsync(engine, "other.order_lines.where(order_id == 9999).count()")).ShouldBe("0");
   }

   /// <summary>SQL Server's identity columns take no value from an insert; the plan says so before anything runs.</summary>
   [Fact]
   public async Task SqlServerIdentitiesTakeNoValue()
   {
      ServerDatabase database = await servers.ShopAsync(ServerKind.SqlServer);
      await using TestSources sources = await database.SourcesAsync();
      QueryEngine engine = sources.Engine();
      DmlPlan plan = engine.PlanChanges(new ChangeSet([new InsertRow(Table(engine, "shop.customers"), Row(("id", 9), ("name", "Epsilon")))]));
      plan.Issues.Single().Message.ShouldBe("'id' is an identity column: the database gives its value");
   }

   /// <summary>Triggers' rows don't count as the statement's: SQL Server's orders log each update.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task RowsATriggerChangesDontCount(ServerKind server)
   {
      ServerDatabase database = await servers.FreshAsync(server);
      await using TestSources sources = await database.SourcesAsync();
      QueryEngine engine = sources.Engine();
      DmlResult result = await CommitAsync(engine, new UpdateRow(Table(engine, "shop.orders"), Row(("id", 1001L)), Row(("total", 1m))));
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      (await RowsAsync(engine, "shop.audit_log.select(message)")).ShouldBe("'order 1001 updated'");
   }

   /// <summary>An edited SQL Server statement that gives rows (OUTPUT) still counts the rows it changed, not its first value.</summary>
   [Fact]
   public async Task AnEditedStatementWithOutputCountsItsRows()
   {
      ServerDatabase database = await servers.FreshAsync(ServerKind.SqlServer);
      await using TestSources sources = await database.SourcesAsync();
      QueryEngine engine = sources.Engine();
      DmlScript script = engine.ParseScript(engine.Catalog.FindSource("shop")!, "UPDATE customers SET city = 'Durban' OUTPUT inserted.id WHERE id = 3");
      DmlResult result = await engine.CommitAsync([script], Token);
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      result.Scripts.Single().Statements.Single().RowsChanged.ShouldBe(1);
   }

   /// <summary>The CLR types a result has for a row are what an edit of it sends back.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task AnEditedEnumOrDatetimeIsWrittenAsTheColumnsType(ServerKind server)
   {
      ServerDatabase database = await servers.FreshAsync(server, "kinds");
      await using TestSources sources = await database.SourcesAsync("k");
      QueryEngine engine = sources.Engine();
      string column = server == ServerKind.Postgres ? "en" : "dt";
      object value = server == ServerKind.Postgres ? "ok" : new System.DateTime(2026, 3, 2, 10, 0, 0, 3);
      await using QueryResult read = await engine.ExecuteAsync(new QueryRequest($"k.kinds.where(id == 1).select(id, {column})"), Token);
      (await read.ReadAsync(Token)).ShouldBeTrue();
      ResultSchema schema = read.Schema;
      object?[] row = read.Current;
      DmlResult result = await CommitAsync(engine, [.. ResultRowEditor.Update(schema, row, new Dictionary<int, object?> { [1] = value })]);
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      (await RowsAsync(engine, $"k.kinds.where(id == 1).select({column})")).ShouldBe(server == ServerKind.Postgres ? "'ok'" : "2026-03-02 10:00:00.003");
   }
}
