using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Connectors;
using GalaxyData.Query.DuckDb;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Sqlite;
using GalaxyData.Testing;
using GalaxyData.Web.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Catalog;

/// <summary>Queries run on the catalog the application builds, connecting through its sources: one source, across sources, and a folder of workbooks.</summary>
public sealed class CatalogQueryTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   private static async Task<List<string>> RunAsync(WebAppFactory factory, string query)
   {
      CatalogState state = await factory.Services.GetRequiredService<CatalogService>().GetAsync(Token);
      QueryEngine engine = new(state.Catalog, factory.Services.GetRequiredService<SourceConnections>(), factory.Services.GetRequiredService<SourceProviders>().All,
         factory.Services.GetRequiredService<DuckDbMergeEngine>());
      await using QueryResult result = await engine.ExecuteAsync(new QueryRequest(query), Token);
      // Hidden columns (keys the links of others need) aren't the query's.
      int[] shown = result.Schema.VisibleColumns.Select(c => c.Ordinal).ToArray();
      return (await result.ToListAsync(Token))
         .Select(row => string.Join(" ", shown.Select(i => System.Convert.ToString(row[i], System.Globalization.CultureInfo.InvariantCulture))))
         .ToList();
   }

   [Fact]
   public async Task QueriesRunOnTheCatalog()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      await TestSources.AddSqliteAsync(admin, "shop", await TestSources.ShopAsync(factory));
      string warehouse = Path.Combine(TestSources.Files(factory), "wh.duckdb");
      await TestSources.DuckDbAsync(warehouse, "CREATE TABLE facts (customer_id INTEGER, amount DECIMAL(10,2)); INSERT INTO facts VALUES (1, 10.5), (2, 7)");
      int wh = await TestSources.AddAsync(admin, "wh", "duckdb", new { settings = new { DataSource = warehouse } });
      await TestSources.SettledAsync(admin, wh, "ready");
      string books = Path.Combine(TestSources.Files(factory), "books");
      Directory.CreateDirectory(books);
      new XlsxBuilder().Sheet("Budget", XlsxBuilder.Row("Item", "Amount"), XlsxBuilder.Row("Rent", 1200), XlsxBuilder.Row("Food", 450)).Save(Path.Combine(books, "home.xlsx"));
      await TestSources.SettledAsync(admin, await TestSources.AddAsync(admin, "xl", "excel", new { settings = new { Folder = books } }), "ready");

      (await RunAsync(factory, "shop.orders.where(status == \"open\").select(id).orderBy(id)")).ShouldBe(["1001", "1003"]);
      (await RunAsync(factory, "shop.customers.join(wh.facts, outer.id == inner.customer_id).select(outer.name, inner.amount).orderBy(name)"))
         .ShouldBe(["Acme Ltd 10.50", "Beta Corp 7.00"]);
      (await RunAsync(factory, "xl.home.Budget.where(Amount > 500).select(Item)")).ShouldBe(["Rent"]);

      // A connection deleted isn't reached any more, even by a query of the catalog before.
      CatalogState before = await factory.Services.GetRequiredService<CatalogService>().GetAsync(Token);
      (await admin.DeleteAsync($"/api/connections/{wh}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
      await factory.Services.GetRequiredService<CatalogService>().GetAsync(Token);
      QueryEngine stale = new(before.Catalog, factory.Services.GetRequiredService<SourceConnections>(), factory.Services.GetRequiredService<SourceProviders>().All);
      SourceUnavailableException gone = await Should.ThrowAsync<SourceUnavailableException>(async () =>
      {
         await using QueryResult result = await stale.ExecuteAsync(new QueryRequest("wh.facts.select(amount)"), Token);
         await result.ToListAsync(Token);
      });
      gone.Reason.ShouldBe("There is no such connection any more");
   }

   /// <summary>A folder's sheets load as its schema was read, until it is read again: options changed alone don't change what queries find.</summary>
   [Fact]
   public async Task AFolderLoadsAsItsSchemaWasRead()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      string books = Path.Combine(TestSources.Files(factory), "books");
      Directory.CreateDirectory(books);
      new XlsxBuilder().Sheet("Budget", XlsxBuilder.Row("Item", "Amount"), XlsxBuilder.Row("Rent", 1200)).Save(Path.Combine(books, "home.xlsx"));
      int id = await TestSources.AddAsync(admin, "xl", "excel", new { settings = new { Folder = books } });
      await TestSources.SettledAsync(admin, id, "ready");
      (await RunAsync(factory, "xl.home.Budget.select(Item)")).ShouldBe(["Rent"]);

      // The options changed, the schema not read again yet (as while it is read, or when reading it fails).
      await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
      {
         await scope.ServiceProvider.GetRequiredService<GalaxyData.Web.Metadata.MetadataDb>().Connections
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.OptionsJson, """{"headerRow":"false"}"""), Token);
      }
      factory.Services.GetRequiredService<CatalogService>().Invalidate();
      (await RunAsync(factory, "xl.home.Budget.select(Item)")).ShouldBe(["Rent"]);

      await TestSources.RefreshAsync(admin, id);
      (await RunAsync(factory, "xl.home.Budget.select(A).orderBy(A)")).ShouldBe(["Item", "Rent"]);
   }

   [Fact]
   public async Task AConnectorLetGoOpensNoMore()
   {
      string path = Path.Combine(Path.GetTempPath(), $"gd-connector-{System.Guid.NewGuid():N}.db");
      try
      {
         SourceConnector connector = new SqliteKind().Connector($"Data Source={path}");
         await using (System.Data.Common.DbConnection opened = await connector.OpenAsync(Token)) { opened.State.ShouldBe(System.Data.ConnectionState.Open); }
         await connector.DisposeAsync();
         await Should.ThrowAsync<System.ObjectDisposedException>(async () => await connector.OpenAsync(Token));
      }
      finally
      {
         File.Delete(path);
      }
   }
}
