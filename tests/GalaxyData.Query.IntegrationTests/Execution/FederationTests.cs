using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Execution;
using GalaxyData.Testing;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Execution;

/// <summary>Queries that combine sources: fragments fetched from each, combined in the merge engine.</summary>
public sealed partial class FederationTests
{
   /// <summary>
   /// The shop split in two: orders and their lines in SQLite (<c>sales</c>), customers and addresses in DuckDB
   /// (<c>crm</c>), linked by relations named as the foreign keys name them in one database.
   /// </summary>
   internal static async Task<TestSources> SplitShopAsync()
   {
      TestSources sources = new();
      await sources.AddSqliteAsync("sales", Fixtures.Sql("shop.sqlite.sql") + "\nPRAGMA foreign_keys = OFF; DROP TABLE customers; DROP TABLE addresses; DROP TABLE employees;");
      await sources.AddDuckDbAsync("crm", Fixtures.Sql("shop.duckdb.sql") + "\nDROP VIEW open_orders; DROP TABLE order_lines; DROP TABLE orders; DROP TABLE audit_log;");
      return sources;
   }

   internal static readonly CatalogOverlay SplitOverlay = new()
   {
      Relations =
      [
         new OverlayRelation("sales.orders", ["customer_id"], "crm.customers", ["id"]) { Name = "customer", InverseName = "orders" },
         new OverlayRelation("sales.orders", ["ship_address_id"], "crm.addresses", ["id"]) { Name = "ship_address", InverseName = "orders_by_ship_address" },
         new OverlayRelation("sales.orders", ["bill_address_id"], "crm.addresses", ["id"]) { Name = "bill_address", InverseName = "orders_by_bill_address" },
         new OverlayRelation("sales.orders", ["ship_address_id"], "reports.addr_x", ["id"]) { Name = "shipx" },
      ],
      VirtualEntities =
      [
         new OverlayVirtualEntity("reports.addr_x", "crm.addresses.select(id, line1, tag: coalesce(city, 'none'), known: city != null, label: 'addr')") { Key = ["id"] },
      ],
   };

   /// <summary>A query of the one-database shop, for the split shop: each table under the alias of the source that has it.</summary>
   internal static string Split(string query) => ShopTable().Replace(query, m => m.Groups[1].Value switch
   {
      "customers" or "addresses" or "employees" => "crm." + m.Groups[1].Value,
      _ => "sales." + m.Groups[1].Value,
   });

   [GeneratedRegex(@"\bshop\.(\w+)")]
   private static partial Regex ShopTable();

   [Fact]
   public async Task SplitSourcesReturnTheSameRowsAsOneDatabase()
   {
      await using TestSources whole = await TestSources.SqliteShopAsync();
      await using TestSources split = await SplitShopAsync();
      string expected = await ExecutionTests.RunAllAsync(whole);
      string actual = await ExecutionTests.RunAllAsync(split, SplitOverlay, rewrite: Split);
      actual.ShouldBe(expected);
      split.Merge.ActiveSessions.ShouldBe(0);
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task EverythingInTheMergeEngineReturnsTheSameRows(string provider)
   {
      await using TestSources sources = provider == "sqlite" ? await TestSources.SqliteShopAsync() : await TestSources.DuckDbShopAsync();
      string expected = await ExecutionTests.RunAllAsync(sources);
      string actual = await ExecutionTests.RunAllAsync(sources, options: new QueryEngineOptions { PushDown = false });
      actual.ShouldBe(expected);
      sources.Merge.ActiveSessions.ShouldBe(0);
   }

   [Fact]
   public async Task EachSourceRunsItsPartAndTheMergeEngineTheRest()
   {
      await using TestSources sources = await SplitShopAsync();
      PreparedQuery prepared = sources.Engine(SplitOverlay).Prepare(
         "sales.orders.where(status == 'open' and total > 10).select(id, total, who: customer.name, city: customer.city).orderBy(id)");
      prepared.Success.ShouldBeTrue();
      prepared.Fragments.Select(f => $"{f.Source.Alias} {f.Table}").ShouldBe(["sales f1", "crm f2"]);
      prepared.Merge.ShouldNotBeNull();
      Golden.Match(Explain(prepared));

      await using QueryResult result = await prepared.ExecuteAsync(TestContext.Current.CancellationToken);
      (await TestSources.RowsAsync(result)).ShouldBe(
         "1001 | 250.00 | 'Acme Ltd' | 'Cape Town'" + Environment.NewLine +
         "1003 | 12.25 | 'Beta Corp' | 'Johannesburg'" + Environment.NewLine);
      result.Stats.Fragments.OrderBy(f => f.Table).Select(f => $"{f.Source} {f.Table} {f.Rows}").ShouldBe(["sales f1 2", "crm f2 3"]);
      result.Stats.FetchedRows.ShouldBe(5);
   }

   /// <summary>Sorted rows filtered by any() or not any() (semi and anti joins) keep their order when they are a fragment.</summary>
   [Theory]
   [InlineData("shop.orders.orderBy(desc(total)).where(order_lines.any()).select(id, who: customer.name).take(1)")]
   [InlineData("shop.orders.orderBy(desc(total)).where(id in shop.order_lines.select(order_id)).select(id, who: customer.name).take(1)")]
   [InlineData("shop.orders.orderBy(desc(total)).where(order_lines.any()).take(2).select(id, who: customer.name)")]
   [InlineData("shop.orders.orderBy(total).where(not order_lines.any(qty > 1)).select(id, who: customer.name)")]
   [InlineData("shop.customers.orderBy(desc(name)).where(addresses.any()).select(name, n: orders.count())")]
   public async Task SortedSemiAndAntiJoinsKeepTheirOrderAcrossSources(string query)
   {
      await using TestSources whole = await TestSources.SqliteShopAsync();
      await using TestSources split = await SplitShopAsync();
      CancellationToken token = TestContext.Current.CancellationToken;
      await using QueryResult expected = await whole.Engine(ExecutionTests.Overlay).ExecuteAsync(new QueryRequest(query), token);
      await using QueryResult actual = await split.Engine(SplitOverlay).ExecuteAsync(new QueryRequest(Split(query)), token);
      (await TestSources.RowsAsync(actual)).ShouldBe(await TestSources.RowsAsync(expected));
   }

   [Fact]
   public async Task PagesOfSortedSemiJoinsKeepTheirOrderAcrossSources()
   {
      await using TestSources split = await SplitShopAsync();
      CancellationToken token = TestContext.Current.CancellationToken;
      await using QueryResult page = await split.Engine(SplitOverlay).ExecuteAsync(
         new QueryRequest("sales.orders.orderBy(desc(total)).where(order_lines.any()).select(id, who: customer.name)") { Paging = new PageRequest(0, 1) }, token);
      // 1001 has the highest total of the orders with lines.
      (await page.ToListAsync(token)).ShouldHaveSingleItem()[0].ShouldBe(1001L);
   }

   [Fact]
   public async Task PagesCountsAndFirstRowsWorkAcrossSources()
   {
      await using TestSources sources = await SplitShopAsync();
      QueryEngine engine = sources.Engine(SplitOverlay);
      CancellationToken token = TestContext.Current.CancellationToken;
      const string query = "sales.orders.where(customer.city != null)";

      await using (QueryResult page = await engine.ExecuteAsync(new QueryRequest(query + ".select(id, who: customer.name)") { Paging = new PageRequest(1, 2) }, token))
      {
         // Paging a projection keeps the query's order; there is none, so sort to compare.
         (await page.ToListAsync(token)).Count.ShouldBe(2);
      }
      await using (QueryResult page = await engine.ExecuteAsync(new QueryRequest(query) { Paging = new PageRequest(1, 2) }, token))
      {
         // Pages of an entity's rows are in key order.
         (await page.ToListAsync(token)).Select(r => r[0]).ShouldBe([1002L, 1003L]);
      }
      PreparedQuery count = engine.Prepare(query).ForCount();
      count.Merge.ShouldNotBeNull();
      await using (QueryResult result = await count.ExecuteAsync(token))
      {
         (await TestSources.RowsAsync(result)).ShouldBe("3" + Environment.NewLine);
      }
      // Neither side's columns are needed, only its rows.
      await using (QueryResult result = await engine.ExecuteAsync(new QueryRequest("sales.orders.selectMany(o => crm.customers).count()"), token))
      {
         (await TestSources.RowsAsync(result)).ShouldBe("12" + Environment.NewLine);
      }
      await using (QueryResult result = await engine.ExecuteAsync(new QueryRequest("sales.orders.where(customer.name == 'nobody').first()"), token))
      {
         (await Should.ThrowAsync<QueryExecutionException>(() => result.ToListAsync(token))).Message.ShouldContain("first() found no rows");
      }
      sources.Merge.ActiveSessions.ShouldBe(0);
   }

   [Fact]
   public async Task QueriesOfOneSourceDontUseTheMergeEngine()
   {
      await using TestSources sources = await SplitShopAsync();
      PreparedQuery prepared = sources.Engine(SplitOverlay).Prepare("sales.orders.where(status == 'open').select(id, lines: order_lines.count())");
      prepared.Merge.ShouldBeNull();
      prepared.Fragments.ShouldHaveSingleItem().Table.ShouldBeNull();
      prepared.Explain().Summary.ShouldStartWith("Runs as one SQLite query in sales");
   }

   [Fact]
   public async Task AFailingFragmentNamesItsSource()
   {
      await using TestSources sources = await SplitShopAsync();
      QueryEngine engine = sources.Engine(SplitOverlay);
      await sources.RunAsync("sales", "DROP VIEW open_orders");
      QueryExecutionException error = await Should.ThrowAsync<QueryExecutionException>(() =>
         engine.ExecuteAsync(new QueryRequest("sales.open_orders.join(crm.customers, outer.customer_id == inner.id, o: outer, c: inner).select(o.id, c.name)"),
                              TestContext.Current.CancellationToken));
      error.Message.ShouldStartWith("sales (SQLite) failed to run its part of the query:");
      error.Message.ShouldContain("no such table");
      sources.Merge.ActiveSessions.ShouldBe(0);
   }

   [Fact]
   public async Task ValuesThatDontConvertNameTheirSourceRowAndColumn()
   {
      await using TestSources sources = new();
      await sources.AddSqliteAsync("log", "CREATE TABLE events (id INTEGER PRIMARY KEY, happened DATE, who INTEGER); INSERT INTO events VALUES (1, '2026-01-05', 1), (2, 'soon', 2);");
      await sources.AddDuckDbAsync("crm", "CREATE TABLE people (id INTEGER PRIMARY KEY, name VARCHAR); INSERT INTO people VALUES (1, 'Ann'), (2, 'Ben');");
      CatalogOverlay overlay = new() { Relations = [new OverlayRelation("log.events", ["who"], "crm.people", ["id"]) { Name = "person" }] };
      const string query = "log.events.select(id, happened, name: person.name).orderBy(id)";
      QueryExecutionException error = await Should.ThrowAsync<QueryExecutionException>(() =>
         sources.Engine(overlay).ExecuteAsync(new QueryRequest(query), TestContext.Current.CancellationToken));
      error.Message.ShouldStartWith("Row 2 from log, column 'happened': log returned a value that isn't a date");

      QueryEngine lenient = sources.Engine(overlay, new QueryEngineOptions { LenientConversion = true });
      await using QueryResult result = await lenient.ExecuteAsync(new QueryRequest(query), TestContext.Current.CancellationToken);
      (await TestSources.RowsAsync(result)).ShouldBe($"1 | 2026-01-05 | 'Ann'{Environment.NewLine}2 | null | 'Ben'{Environment.NewLine}");
   }

   [Fact]
   public async Task LargeFetchesAreWarnedAbout()
   {
      await using TestSources sources = await SplitShopAsync();
      QueryEngine engine = sources.Engine(SplitOverlay, new QueryEngineOptions { LargeFetchRows = 2 });
      PreparedQuery prepared = engine.Prepare("sales.orders.select(id, who: customer.name)");
      prepared.Success.ShouldBeTrue();
      QueryDiagnostic warning = prepared.Diagnostics.ShouldHaveSingleItem();
      warning.Code.ShouldBe(DiagnosticCodes.LargeFetch);
      warning.Message.ShouldBe("This query fetches about 3 rows from crm into the merge engine");
   }

   private static string Explain(PreparedQuery prepared) => Query.Explain.ExplainTextRenderer.Render(prepared.Explain());
}
