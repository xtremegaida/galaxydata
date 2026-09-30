using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Excel;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Explain;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.IntegrationTests.Execution;
using GalaxyData.Query.Types;
using GalaxyData.Testing;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Excel;

/// <summary>
/// A folder of workbooks next to the SQLite shop: <c>Budget 2024.xlsx</c> (a spaced sheet, another, a hidden one,
/// a chart sheet and an empty one) and <c>customers.xlsx</c>, with a lock file, a text file, a folder with a workbook
/// in it and a file that isn't a workbook, which are all left out.
/// </summary>
internal sealed class ExcelShop : IAsyncDisposable
{
   private ExcelShop(TestSources sources, TempFolder folder)
   {
      Sources = sources;
      Folder = folder;
   }

   public TestSources Sources { get; }

   public TempFolder Folder { get; }

   public string Customers => Folder.File("customers.xlsx");

   public static object?[][] CustomerRows { get; } = [["id", "name", "tier"], [1, "Acme Ltd", "gold"], [2, "Beta Corp", "silver"], [4, "Delta", "bronze"]];

   public static async Task<ExcelShop> OpenAsync(ExcelFolderSetup? setup = null)
   {
      TempFolder folder = new();
      new XlsxBuilder()
         .Sheet("Sheet 1",
            ["dept", "amount", "month", "customer_id"],
            ["Sales", 1200.5, new DateOnly(2024, 1, 1), 1],
            ["Sales", 800, new DateOnly(2024, 2, 1), 2],
            ["Ops", 300, new DateOnly(2024, 1, 1), 1],
            ["Ops", 450.25, new DateOnly(2024, 3, 1), 3],
            ["R&D", 2000, new DateOnly(2024, 2, 1), 9])
         .Sheet("Q2", ["quarter", "target"], ["Q2", 5000])
         .HiddenSheet("Secret", ["x"], [1])
         .ChartSheet("Chart")
         .Sheet("Blank")
         .Save(folder.File("Budget 2024.xlsx"));
      new XlsxBuilder().Sheet("list", CustomerRows).Save(folder.File("customers.xlsx"));
      await File.WriteAllTextAsync(folder.File("~$Budget 2024.xlsx"), "lock");
      await File.WriteAllTextAsync(folder.File("notes.txt"), "not a workbook");
      await File.WriteAllTextAsync(folder.File("broken.xlsx"), "not a workbook either");
      Directory.CreateDirectory(folder.File("sub"));
      new XlsxBuilder().Sheet("inner", ["a"], [1]).Save(Path.Combine(folder.File("sub"), "inner.xlsx"));
      TestSources sources = await TestSources.SqliteShopAsync();
      await sources.AddExcelAsync("xl", folder.Path, setup?.Invoke(folder.Path));
      return new ExcelShop(sources, folder);
   }

   /// <summary>Writes the customers workbook again, a minute later than before, so it reads as changed.</summary>
   public void WriteCustomers(params object?[][] rows)
   {
      DateTime before = File.GetLastWriteTimeUtc(Customers);
      new XlsxBuilder().Sheet("list", rows).Save(Customers);
      File.SetLastWriteTimeUtc(Customers, before.AddMinutes(1));
   }

   public async ValueTask DisposeAsync()
   {
      await Sources.DisposeAsync();
      Folder.Dispose();
   }
}

internal delegate ExcelFolderOptions ExcelFolderSetup(string path);

/// <summary>Excel folders as sources: their schemas, queries of them alone, and with the SQLite shop.</summary>
public sealed class ExcelSourceTests
{
   private const string Budget = "xl[\"Budget 2024\"][\"Sheet 1\"]";

   private static async Task<string> RowsAsync(QueryEngine engine, string query, QueryParameters? parameters = null)
   {
      await using QueryResult result = await engine.Prepare(query, parameters).ExecuteAsync(TestContext.Current.CancellationToken);
      return await TestSources.RowsAsync(result);
   }

   private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;

   [Fact]
   public async Task AFolderIsASchemaForEachWorkbookAndATableForEachSheet()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      Golden.Match(IntrospectionJson.Serialize(shop.Sources.Schemas["xl"], indented: true), "json");
      QueryCatalog catalog = shop.Sources.Catalog();
      catalog.Diagnostics.ShouldBeEmpty();
      TableEntity sheet = (TableEntity)catalog.FindEntity(Budget)!;
      sheet.DisplayName.ShouldBe("xl['Budget 2024']['Sheet 1']");
      sheet.IsWritable.ShouldBeFalse();
      sheet.Source.Catalog.ShouldBe("excel_xl");
      sheet.Columns.Select(c => $"{c.Name} {c.Type}").ShouldBe(["dept string?", "amount double?", "month date?", "customer_id int64?"]);
   }

   [Fact]
   public async Task TheSchemaReadsTheSameThroughAConnection()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      ExcelSourceProvider excel = shop.Sources.Excel;
      await using DbConnection connection = await excel.OpenConnectionAsync(excel.Source("xl"), shop.Sources, TestContext.Current.CancellationToken);
      SourceSchema schema = await excel.Introspector.IntrospectAsync(connection, IntrospectionOptions.Default, TestContext.Current.CancellationToken);
      IntrospectionJson.Serialize(schema).ShouldBe(IntrospectionJson.Serialize(shop.Sources.Schemas["xl"]));
   }

   [Fact]
   public async Task AQueryOfOneFolderRunsInItsCatalog()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      QueryEngine engine = shop.Sources.Engine();
      string query = Budget + ".where(amount > 400).orderBy(desc(amount)).select(dept, amount, month)";
      PreparedQuery prepared = engine.Prepare(query);
      prepared.Merge.ShouldBeNull();
      prepared.Fragments.Single().Sql.ShouldContain("FROM excel_xl.\"Budget 2024\".\"Sheet 1\" AS s");
      (await RowsAsync(engine, query)).ShouldBe(Lines(
         "'R&D' | 2000 | 2024-02-01",
         "'Sales' | 1200.5 | 2024-01-01",
         "'Sales' | 800 | 2024-02-01",
         "'Ops' | 450.25 | 2024-03-01"));
      (await RowsAsync(engine, Budget + ".groupBy(dept).select(dept: key, total: sum(amount), n: count()).orderBy(dept)")).ShouldBe(Lines(
         "'Ops' | 750.25 | 2",
         "'R&D' | 2000 | 1",
         "'Sales' | 2000.5 | 2"));
      (await RowsAsync(engine, "xl[\"Budget 2024\"].Q2.select(quarter, target)")).ShouldBe(Lines("'Q2' | 5000"));
      shop.Sources.OpenedTo("xl").ShouldBe(0);
   }

   [Fact]
   public async Task HiddenAndChartSheetsAndOtherFilesAreLeftOut()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      QueryEngine engine = shop.Sources.Engine();
      foreach (string query in new[] { "xl[\"Budget 2024\"].Secret", "xl[\"Budget 2024\"].Chart", "xl[\"Budget 2024\"].Blank", "xl.sub.inner", "xl.broken.x" })
      {
         engine.Prepare(query).Success.ShouldBeFalse(query);
      }
      shop.Sources.Schemas["xl"].Warnings.ShouldBe(["'broken.xlsx' can't be read: it isn't an .xlsx workbook, or it is encrypted"]);
   }

   [Fact]
   public async Task HiddenSheetsAreReadWhenAskedFor()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync(path => new ExcelFolderOptions { Path = path, IncludeHiddenSheets = true, AllText = true });
      (await RowsAsync(shop.Sources.Engine(), "xl[\"Budget 2024\"].Secret.select(x)")).ShouldBe(Lines("'1'"));
   }

   /// <summary>The exit criterion of M8: a sheet joined with a SQLite table, along a relation of the overlay and with join().</summary>
   [Fact]
   public async Task ASheetJoinsASqliteTable()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      CatalogOverlay overlay = new() { Relations = [new OverlayRelation(Budget, ["customer_id"], "shop.customers", ["id"]) { Name = "customer" }] };
      shop.Sources.Catalog(overlay).Diagnostics.ShouldBeEmpty();
      QueryEngine engine = shop.Sources.Engine(overlay);
      string query = Budget + ".select(dept, amount, who: customer.name).orderBy(amount)";
      string expected = Lines(
         "'Ops' | 300 | 'Acme Ltd'",
         "'Ops' | 450.25 | 'Gamma Inc'",
         "'Sales' | 800 | 'Beta Corp'",
         "'Sales' | 1200.5 | 'Acme Ltd'",
         "'R&D' | 2000 | null");
      PreparedQuery prepared = engine.Prepare(query);
      prepared.Fragments.Select(f => $"{f.Source.Alias} {f.InMergeEngine}").Order().ShouldBe(["shop False", "xl True"]);
      prepared.Explain().Fragments.Single(f => f.Source == "xl").Strategy.ShouldBe("copied into f1 inside the merge engine");
      await using (QueryResult result = await prepared.ExecuteAsync(TestContext.Current.CancellationToken))
      {
         (await TestSources.RowsAsync(result)).ShouldBe(expected);
         result.Stats.Fragments.Single(f => f.Source == "xl").Rows.ShouldBe(5);
      }
      (await RowsAsync(shop.Sources.Engine(overlay, new QueryEngineOptions { PushDown = false }), query)).ShouldBe(expected);
      // With a merge engine of its own, the query fetches the sheet's rows as it would a database's.
      using (DuckDb.DuckDbMergeEngine other = new())
      {
         QueryEngine apart = shop.Sources.Engine(overlay, merge: other);
         apart.Prepare(query).Fragments.ShouldAllBe(f => !f.InMergeEngine);
         (await RowsAsync(apart, query)).ShouldBe(expected);
      }
      (await RowsAsync(engine, "shop.customers.join(xl.customers.list, outer.id == inner.id, c: outer, x: inner).select(name: c.name, tier: x.tier).orderBy(name)"))
         .ShouldBe(Lines("'Acme Ltd' | 'gold'", "'Beta Corp' | 'silver'"));
   }

   [Fact]
   public async Task ASheetIsFetchedByTheKeysOfATable()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      CatalogOverlay overlay = new()
      {
         Entities = [new OverlayEntitySettings("xl.customers.list") { Key = ["id"] }],
         Relations = [new OverlayRelation("shop.orders", ["customer_id"], "xl.customers.list", ["id"]) { Name = "listed" }],
      };
      QueryEngine engine = shop.Sources.Engine(overlay, new QueryEngineOptions { BindJoins = BindJoinMode.Always });
      await using QueryResult result = await engine.Prepare("shop.orders.select(id, tier: listed.tier).orderBy(id)").ExecuteAsync(TestContext.Current.CancellationToken);
      (await TestSources.RowsAsync(result)).ShouldBe(Lines("1001 | 'gold'", "1002 | 'gold'", "1003 | 'silver'", "1004 | null"));
      FragmentStats sheet = result.Stats.Fragments.Single(f => f.Source == "xl");
      sheet.Strategy.ShouldBe(FetchStrategy.Keys);
      sheet.Keys.ShouldBe(3);
      sheet.Rows.ShouldBe(2);
   }

   [Fact]
   public async Task AValueOfASheetIsWorkedOutFirst()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      QueryEngine engine = shop.Sources.Engine();
      string query = "shop.orders.where(total * 20 >= xl[\"Budget 2024\"].Q2.max(target)).select(id)";
      engine.Prepare(query).Fragments.ShouldContain(f => f.Source.Alias == "xl" && f.Value != null);
      (await RowsAsync(engine, query)).ShouldBe(Lines("1001"));
   }

   [Fact]
   public async Task ASheetIsLoadedAgainWhenItsWorkbookChanges()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      QueryEngine engine = shop.Sources.Engine();
      const string query = "xl.customers.list.select(id, name).orderBy(id)";
      (await RowsAsync(engine, query)).ShouldBe(Lines("1 | 'Acme Ltd'", "2 | 'Beta Corp'", "4 | 'Delta'"));
      shop.WriteCustomers([.. ExcelShop.CustomerRows, [5, "Epsilon", "gold"]]);
      string expected = Lines("1 | 'Acme Ltd'", "2 | 'Beta Corp'", "4 | 'Delta'", "5 | 'Epsilon'");
      string[] all = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => RowsAsync(engine, query))));
      all.ShouldAllBe(rows => rows == expected);
   }

   [Fact]
   public async Task AWorkbookThatChangedShapeSaysToRefreshTheSchema()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      QueryEngine engine = shop.Sources.Engine();
      const string query = "xl.customers.list.select(id)";
      shop.WriteCustomers(["id", "name"], [1, "Acme Ltd"]);
      (await Should.ThrowAsync<QueryExecutionException>(() => RowsAsync(engine, query)))
         .Message.ShouldBe("xl: the sheet 'list' of 'customers.xlsx' has no column 'tier' now; refresh the source's schema");
      File.Delete(shop.Customers);
      (await Should.ThrowAsync<QueryExecutionException>(() => RowsAsync(engine, query)))
         .Message.ShouldStartWith("xl: the workbook 'customers.xlsx' is no longer in ");
   }

   [Fact]
   public async Task AValueThatDoesntFitItsColumnFailsTheQueryOrIsNull()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      shop.WriteCustomers(["id", "name", "tier"], [1, "Acme Ltd", "gold"], ["n/a", "Beta Corp", "silver"]);
      const string query = "xl.customers.list.select(id, name).orderBy(name)";
      (await Should.ThrowAsync<QueryExecutionException>(() => RowsAsync(shop.Sources.Engine(), query)))
         .Message.ShouldBe("xl: row 3 of the sheet 'list' in 'customers.xlsx', column 'id' (cell A3): 'n/a' isn't a whole number");
      (await RowsAsync(shop.Sources.Engine(options: new QueryEngineOptions { LenientConversion = true }), query))
         .ShouldBe(Lines("1 | 'Acme Ltd'", "null | 'Beta Corp'"));
   }

   [Fact]
   public async Task AColumnOfATypeFromTheOverlayHoldsThatType()
   {
      await using ExcelShop shop = await ExcelShop.OpenAsync();
      const string query = "xl.customers.list.where(id > '1').select(id).orderBy(id)";
      CatalogOverlay overlay = new() { Entities = [new OverlayEntitySettings("xl.customers.list") { Columns = [new OverlayColumn("id") { Type = ScalarType.Text() }] }] };
      (await RowsAsync(shop.Sources.Engine(overlay), query)).ShouldBe(Lines("'2'", "'4'"));
      // The catalog without the overlay gets its numbers back.
      (await RowsAsync(shop.Sources.Engine(), "xl.customers.list.where(id > 1).select(id).orderBy(id)")).ShouldBe(Lines("2", "4"));
   }
}
