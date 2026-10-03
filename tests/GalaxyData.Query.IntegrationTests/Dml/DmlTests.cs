using System.Threading.Tasks;
using GalaxyData.Query.Dml;
using GalaxyData.Query.Execution;
using GalaxyData.Query.IntegrationTests.Execution;
using Shouldly;
using Xunit;
using static GalaxyData.Query.IntegrationTests.Dml.DmlScenarios;

namespace GalaxyData.Query.IntegrationTests.Dml;

/// <summary>Changes written to SQLite and DuckDB databases; each test has fresh ones.</summary>
public sealed class DmlTests
{
   /// <summary>The shop in the provider's database (SQLite checking foreign keys), and another in the other's.</summary>
   private static async Task<TestSources> SourcesAsync(string provider)
   {
      TestSources sources = new();
      string sqlite = Fixtures.Sql("shop.sqlite.sql");
      string duckdb = Fixtures.Sql("shop.duckdb.sql");
      return provider == "sqlite"
         ? await (await sources.AddSqliteAsync("shop", sqlite, enforceForeignKeys: true)).AddDuckDbAsync("other", duckdb)
         : await (await sources.AddDuckDbAsync("shop", duckdb)).AddSqliteAsync("other", sqlite, enforceForeignKeys: true);
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task InsertsGiveBackTheirRows(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      await InsertsGiveBackTheirRowsAsync(sources);
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task UpdatesAndDeletesFindTheirRows(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      await UpdatesAndDeletesFindTheirRowsAsync(sources);
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task AConflictWritesNothing(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      await AConflictWritesNothingAsync(sources);
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task AFailingStatementWritesNothing(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      await AFailingStatementWritesNothingAsync(sources);
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task ChangesAcrossConnectionsCommitTogether(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      await ChangesAcrossConnectionsCommitTogetherAsync(sources);
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task AFailedCommitIsReported(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      await AFailedCommitIsReportedAsync(sources);
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task EditedScriptsRunWhenTheyOnlyChangeData(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      await EditedScriptsRunWhenTheyOnlyChangeDataAsync(sources);
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task EditsOfResultRowsChangeTheirTables(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      await EditsOfResultRowsChangeTheirTablesAsync(sources);
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task CancellingWritesNothing(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      await CancellingWritesNothingAsync(sources);
   }

   /// <summary>
   /// SQLite checks foreign keys (and carries out their ON DELETE actions) as the source says; left as the connection
   /// has it, the SQLite Microsoft.Data.Sqlite bundles checks them.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   [InlineData(null)]
   public async Task SqliteChecksForeignKeysAsTheSourceSays(bool? enforce)
   {
      await using TestSources sources = await new TestSources().AddSqliteAsync("shop", Fixtures.Sql("shop.sqlite.sql"), enforceForeignKeys: enforce);
      QueryEngine engine = sources.Engine();
      DmlResult orphan = await CommitAsync(engine,
         new InsertRow(Table(engine, "shop.order_lines"), Row(("order_id", 9999L), ("line_no", 1), ("product_code", "P-100"), ("qty", 1), ("price", 1m))));
      DmlResult cascade = await CommitAsync(engine, new DeleteRow(Table(engine, "shop.orders"), Row(("id", 1002L))));
      cascade.Outcome.ShouldBe(DmlOutcome.Committed);
      if (enforce != false)
      {
         orphan.Failure!.Message.ShouldEndWith("failed to run the insert into shop.order_lines: SQLite Error 19: 'FOREIGN KEY constraint failed'.");
         (await RowsAsync(engine, "shop.order_lines.where(order_id == 1002).count()")).ShouldBe("0");
      }
      else
      {
         orphan.Outcome.ShouldBe(DmlOutcome.Committed);
         (await RowsAsync(engine, "shop.order_lines.where(order_id == 1002).count()")).ShouldBe("1");
      }
   }

   /// <summary>
   /// DuckDB updates an indexed column as a delete and an insert, which a foreign key referring to the row rejects
   /// (a limitation DuckDB documents); a row nothing refers to can be changed.
   /// </summary>
   [Fact]
   public async Task DuckDbCantUpdateAnIndexedColumnOfARowAForeignKeyRefersTo()
   {
      await using TestSources sources = await TestSources.DuckDbShopAsync();
      QueryEngine engine = sources.Engine();
      DmlResult result = await CommitAsync(engine, new UpdateRow(Table(engine, "shop.orders"), Row(("id", 1001L)), Row(("status", "shipped"))));
      result.Failure!.Message.ShouldContain("Violates foreign key constraint because key \"order_id: 1001\" is still referenced by a foreign key in a different table");
      result = await CommitAsync(engine, new UpdateRow(Table(engine, "shop.orders"), Row(("id", 1004L)), Row(("status", "void"))));
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
   }

   /// <summary>The trigger on SQLite's orders adds a row to the audit log; the update still changes one row.</summary>
   [Fact]
   public async Task RowsATriggerChangesDontCount()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      QueryEngine engine = sources.Engine();
      DmlResult result = await CommitAsync(engine, new UpdateRow(Table(engine, "shop.orders"), Row(("id", 1001L)), Row(("status", "shipped"))));
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      (await RowsAsync(engine, "shop.audit_log.select(message)")).ShouldBe("'order 1001 updated'");
   }

   /// <summary>Keys SQLite may keep in another form than the value's still find their row: a guid in lower case, a date with a time.</summary>
   [Fact]
   public async Task SqliteKeysInOtherFormsFindTheirRow()
   {
      await using TestSources sources = await new TestSources().AddSqliteAsync("shop", """
         CREATE TABLE contacts (id UNIQUEIDENTIFIER PRIMARY KEY, email TEXT NOT NULL);
         INSERT INTO contacts VALUES ('2f1c0000-0000-4000-8000-00000000000a', 'ann@acme.test');
         CREATE TABLE days (day DATE PRIMARY KEY, note TEXT);
         INSERT INTO days VALUES ('2026-01-05 00:00:00', 'a Monday');
         """);
      QueryEngine engine = sources.Engine();
      DmlResult result = await CommitAsync(engine,
         new UpdateRow(Table(engine, "shop.contacts"), Row(("id", "2F1C0000-0000-4000-8000-00000000000A")), Row(("email", "ann@acme.example"))),
         new UpdateRow(Table(engine, "shop.days"), Row(("day", new System.DateOnly(2026, 1, 5))), Row(("note", "the first Monday"))));
      result.Outcome.ShouldBe(DmlOutcome.Committed, result.ToString());
      (await RowsAsync(engine, "shop.days.select(note)")).ShouldBe("'the first Monday'");
   }
}
