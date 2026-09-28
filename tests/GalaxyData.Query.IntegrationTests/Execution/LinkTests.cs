using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Language;
using GalaxyData.Query.Results;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Execution;

/// <summary>
/// Links, edit targets, counts, pages and first rows, followed on real data: each link's query finds the rows it
/// promises, on SQLite and DuckDB alike.
/// </summary>
public sealed class LinkTests
{
   private static async Task<TestSources> SourcesAsync(string provider) =>
      provider == "sqlite" ? await TestSources.SqliteShopAsync() : await TestSources.DuckDbShopAsync();

   private static async Task<(ResultSchema Schema, IReadOnlyList<object?[]> Rows)> RunAsync(QueryEngine engine, QueryRequest request)
   {
      await using QueryResult result = await engine.ExecuteAsync(request, TestContext.Current.CancellationToken);
      return (result.Schema, await result.ToListAsync(TestContext.Current.CancellationToken));
   }

   private static Task<(ResultSchema Schema, IReadOnlyList<object?[]> Rows)> RunAsync(QueryEngine engine, string text) => RunAsync(engine, new QueryRequest(text));

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task DrillingDownFindsTheGroupsRows(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      QueryEngine engine = sources.Engine();
      (ResultSchema schema, IReadOnlyList<object?[]> groups) = await RunAsync(engine,
         "shop.orders.groupBy(customer_id, open: o => o.status == 'open').select(customer_id, open, spend: sum(total), orders: count())");
      DrillDownLink link = schema.Find("orders")!.Link.ShouldBeOfType<DrillDownLink>();
      schema.Find("spend")!.Link.ShouldBeOfType<DrillDownLink>();
      groups.Count.ShouldBe(4);
      foreach (object?[] group in groups)
      {
         QueryRequest drill = link.Query(group);
         (ResultSchema drilled, IReadOnlyList<object?[]> rows) = await RunAsync(engine, drill);
         rows.Count.ShouldBe(Convert.ToInt32(group[3]), drill.Text);
         rows.Sum(r => (decimal)r[drilled.Find("total")!.Ordinal]!).ShouldBe((decimal)group[2]!, drill.Text);
      }
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task DrillingDownIntoANullKeyFindsTheRowsWithout(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      QueryEngine engine = sources.Engine();
      (ResultSchema schema, IReadOnlyList<object?[]> groups) = await RunAsync(engine, "shop.customers.groupBy(city).select(city, n: count()).orderBy(city)");
      object?[] nowhere = groups.Single(g => g[0] == null);
      QueryRequest drill = schema.Find("n")!.Link!.Query(nowhere)!;
      drill.Text.ShouldBe("(shop.customers).where(city == null)");
      (_, IReadOnlyList<object?[]> rows) = await RunAsync(engine, drill);
      rows.ShouldHaveSingleItem()[1].ShouldBe("Gamma Inc");
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task EditTargetsNameTheRowsTheValuesCameFrom(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      QueryEngine engine = sources.Engine();
      (ResultSchema schema, IReadOnlyList<object?[]> rows) = await RunAsync(engine, "shop.order_lines.select(qty, who: order.customer.name).orderBy(qty)");
      schema.VisibleColumns.Count.ShouldBe(2);
      foreach (object?[] row in rows)
      {
         foreach (ResultColumn column in schema.VisibleColumns)
         {
            EditTarget target = column.EditTarget.ShouldNotBeNull(column.Name);
            string key = string.Join(" and ", target.Entity.Key!.Columns.Select((c, i) => $"{QueryText.QuoteName(c.Name)} == $k{i}"));
            QueryParameters parameters = new();
            for (int i = 0; i < target.KeyOrdinals.Count; i++) { parameters.Add("k" + i, row[target.KeyOrdinals[i]], target.Entity.Key.Columns[i].Type.AsNonNullable()); }
            string text = QueryText.Compose(target.Entity.QualifiedName.ToString(), [key]) + ".select(" + QueryText.QuoteName(target.Column.Name) + ")";
            (_, IReadOnlyList<object?[]> found) = await RunAsync(engine, new QueryRequest(text) { Parameters = parameters });
            found.ShouldHaveSingleItem()[0].ShouldBe(row[column.Ordinal], text);
         }
      }
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task RowAndCollectionLinksFindTheirRows(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      QueryEngine engine = sources.Engine();
      (ResultSchema schema, IReadOnlyList<object?[]> rows) = await RunAsync(engine,
         "shop.customers.select(name, n: orders.count(), first: orders.orderBy(id).first()).orderBy(name)");
      CollectionLink orders = schema.Find("n")!.Link.ShouldBeOfType<CollectionLink>();
      RowLink first = schema.Find("first")!.Link.ShouldBeOfType<RowLink>();
      foreach (object?[] row in rows)
      {
         (_, IReadOnlyList<object?[]> found) = await RunAsync(engine, orders.Query(row)!);
         found.Count.ShouldBe(Convert.ToInt32(row[1]));
         (ResultSchema target, IReadOnlyList<object?[]> firstOrder) = await RunAsync(engine, first.Query(row)!);
         firstOrder.ShouldHaveSingleItem()[target.Find("status")!.Ordinal].ShouldBe(row[2]);
      }

      (ResultSchema lines, IReadOnlyList<object?[]> lineRows) = await RunAsync(engine, "shop.order_lines.orderBy(order_id, line_no)");
      RowLink order = lines.Find("order_id")!.Link.ShouldBeOfType<RowLink>();
      order.Navigation!.Name.ShouldBe("order");
      (_, IReadOnlyList<object?[]> orderRows) = await RunAsync(engine, order.Query(lineRows[0])!);
      orderRows.ShouldHaveSingleItem()[0].ShouldBe(lineRows[0][0]);

      (ResultSchema orderSchema, IReadOnlyList<object?[]> all) = await RunAsync(engine, "shop.orders.orderBy(id)");
      CollectionLink related = orderSchema.RowIdentity!.Related.OfType<CollectionLink>().Single(l => l.Navigation.Name == "order_lines");
      (_, IReadOnlyList<object?[]> firstLines) = await RunAsync(engine, related.Query(all[0])!);
      firstLines.Count.ShouldBe(2);
      // A null foreign key leads nowhere.
      RowLink shipTo = orderSchema.Find("ship_address_id")!.Link.ShouldBeOfType<RowLink>();
      shipTo.Query(all.Single(r => r[2] == null)).ShouldBeNull();
   }

   [Fact]
   public async Task CountsExplainTheirOwnPhases()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      Explain.QueryExplain explain = sources.Engine().Prepare("shop.orders.select(id, who: customer.name)").ForCount().Explain(verbose: true);
      explain.Phases.ShouldNotBeNull()[0].Plan.ShouldStartWith("Aggregate: count");
   }

   [Theory]
   [InlineData("shop.orders.count(shop.customers.first())")]
   [InlineData("shop.orders.groupBy(status).select(status, n: count(shop.customers.first()))")]
   public async Task FirstRowsWhereTheyCantBeUsedAreDiagnosed(string query)
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      PreparedQuery prepared = sources.Engine().Prepare(query);
      prepared.Success.ShouldBeFalse();
      prepared.Diagnostics.ShouldContain(d => d.Code == DiagnosticCodes.NotTranslatable);
      prepared.ForCount().Success.ShouldBeFalse();
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task CountsCountTheRowsWithoutJoiningOrSorting(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      QueryEngine engine = sources.Engine();
      string[] queries =
      [
         "shop.orders.where(status == 'open').select(id, total, who: customer.name, city: customer.city).orderBy(who)",
         "shop.orders.select(id, a: ship_address.city).orderBy(a)",
         "shop.orders.orderBy(total).take(3)",
         "shop.orders.groupBy(customer_id).select(customer_id: key, n: count())",
         "shop.customers.select(name, n: orders.count())",
         "shop.orders.select(status).distinct()",
         "shop.orders.orderBy(desc(total)).first()",
         "shop.orders.where(total > 1000000).firstOrDefault()",
         "shop.orders.count()",
         "shop.customers.selectMany(orders, who: outer.name, total: inner.total)",
      ];
      foreach (string query in queries)
      {
         PreparedQuery prepared = engine.Prepare(new QueryRequest(query) { Paging = new PageRequest(1, 1) });
         PreparedQuery count = prepared.ForCount();
         count.Success.ShouldBeTrue(query);
         count.Schema!.Columns.ShouldHaveSingleItem().Name.ShouldBe("count");
         (_, IReadOnlyList<object?[]> rows) = await RunAsync(engine, query);
         await using QueryResult counted = await count.ExecuteAsync(TestContext.Current.CancellationToken);
         (await counted.ToListAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem()[0].ShouldBe((long)rows.Count, query);
      }
      string sql = engine.Prepare(queries[0]).ForCount().Fragments[0].Sql;
      sql.ShouldNotContain("JOIN", Case.Insensitive);
      sql.ShouldNotContain("ORDER BY", Case.Insensitive);
      engine.Prepare(queries[4]).ForCount().Fragments[0].Sql.ShouldNotContain("JOIN", Case.Insensitive);
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task PagesOfTiedRowsDontRepeatOrSkip(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      QueryEngine engine = sources.Engine();
      List<object?> seen = [];
      for (long offset = 0; offset < 4; offset++)
      {
         QueryRequest page = new("shop.order_lines.orderBy(product_code)") { Paging = new PageRequest(offset, 1) };
         engine.Prepare(page).Fragments[0].Sql.ShouldContain("ORDER BY o.product_code, o.order_id, o.line_no");
         (_, IReadOnlyList<object?[]> rows) = await RunAsync(engine, page);
         object?[] row = rows.ShouldHaveSingleItem();
         seen.Add($"{row[0]}/{row[1]}");
      }
      seen.ShouldBe(["1001/1", "1002/1", "1001/2", "1003/1"]);
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task FirstFindsOneRowOrFails(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      QueryEngine engine = sources.Engine();
      (_, IReadOnlyList<object?[]> top) = await RunAsync(engine, "shop.orders.orderBy(desc(total)).first()");
      top.ShouldHaveSingleItem()[0].ShouldBe(1001L);
      (_, IReadOnlyList<object?[]> none) = await RunAsync(engine, "shop.orders.firstOrDefault(total > 1000000)");
      none.ShouldBeEmpty();
      await using QueryResult missing = await engine.ExecuteAsync(new QueryRequest("shop.orders.first(total > 1000000)"), TestContext.Current.CancellationToken);
      QueryExecutionException error = await Should.ThrowAsync<QueryExecutionException>(() => missing.ToListAsync(TestContext.Current.CancellationToken));
      error.Message.ShouldContain("firstOrDefault()");
      // As a value, the first row of nothing is null.
      (_, IReadOnlyList<object?[]> totals) = await RunAsync(engine, "shop.customers.select(name, t: orders.first(total > 100).total).orderBy(name)");
      totals.Select(r => r[1]).ShouldBe([250.00m, null, null]);
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task AFirstRowsValuesAllComeFromTheSameRow(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      QueryEngine engine = sources.Engine();
      // No order at all, and ties in the order: each value is its own subquery, so they must agree on the row.
      (_, IReadOnlyList<object?[]> rows) = await RunAsync(engine,
         "shop.customers.select(f: shop.orders.first(), s: shop.orders.first().status, t: shop.orders.first().total, " +
         "g: shop.orders.orderBy(status == 'open').first().id, h: shop.orders.orderBy(status == 'open').first().total).take(1)");
      object?[] row = rows.ShouldHaveSingleItem();
      (_, IReadOnlyList<object?[]> first) = await RunAsync(engine, "shop.orders.orderBy(id).select(status, total).take(1)");
      (row[1], row[2]).ShouldBe((first[0][0], first[0][1]));
      (_, IReadOnlyList<object?[]> tied) = await RunAsync(engine, $"shop.orders.where(id == {row[3]}).select(total)");
      tied.ShouldHaveSingleItem()[0].ShouldBe(row[4]);
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task AFirstRowsMissingRecordIsNoRow(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      QueryEngine engine = sources.Engine();
      (_, IReadOnlyList<object?[]> none) = await RunAsync(engine, "shop.orders.orderBy(desc(id)).first().ship_address");
      none.ShouldBeEmpty();
      (_, IReadOnlyList<object?[]> some) = await RunAsync(engine, "shop.orders.orderBy(id).first().ship_address");
      some.ShouldHaveSingleItem()[2].ShouldBe("1 Main Rd");
      PreparedQuery count = engine.Prepare("shop.orders.orderBy(desc(id)).first().ship_address").ForCount();
      await using QueryResult counted = await count.ExecuteAsync(TestContext.Current.CancellationToken);
      (await counted.ToListAsync(TestContext.Current.CancellationToken))[0][0].ShouldBe(0L);
      // A later page of first() is empty, not an error.
      (_, IReadOnlyList<object?[]> page) = await RunAsync(engine, new QueryRequest("shop.orders.orderBy(id).first()") { Paging = new PageRequest(1, 10) });
      page.ShouldBeEmpty();
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task AKeylessFirstRowIsThereWhenItsValuesAreNull(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      QueryEngine engine = sources.Engine();
      // The first city group is the null city: the row is there, its values are null.
      (_, IReadOnlyList<object?[]> selected) = await RunAsync(engine,
         "shop.customers.select(name, g: shop.customers.groupBy(city).orderBy(city).first()).where(g == null)");
      selected.ShouldBeEmpty();
      (_, IReadOnlyList<object?[]> filtered) = await RunAsync(engine,
         "shop.customers.where(shop.customers.groupBy(city).orderBy(city).first() == null)");
      filtered.ShouldBeEmpty();
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task PagesKeepTheQuerysOrderWhenTheKeyIsntWhereTheSortIs(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      CatalogOverlay overlay = new()
      {
         VirtualEntities = [new OverlayVirtualEntity("reports.by_total", "shop.orders.orderBy(total).select(oid: id, total, status)") { Key = ["oid"] }],
      };
      QueryEngine engine = sources.Engine(overlay);
      // Customers by name descending, then each one's orders by id (the tie the key breaks); orders by total.
      foreach ((string query, long[] expected) in new[]
      {
         ("shop.customers.orderBy(desc(name)).selectMany(orders)", new[] { 1004L, 1003L, 1001L, 1002L }),
         ("reports.by_total", new[] { 1004L, 1003L, 1002L, 1001L }),
      })
      {
         List<object?> paged = [];
         for (long offset = 0; offset < expected.Length; offset += 3)
         {
            (_, IReadOnlyList<object?[]> page) = await RunAsync(engine, new QueryRequest(query) { Paging = new PageRequest(offset, 3) });
            paged.AddRange(page.Select(r => r[0]));
         }
         paged.ShouldBe(expected.Cast<object?>().ToList(), query);
      }
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task ComposedQueriesFilterAndSortTheRows(string provider)
   {
      await using TestSources sources = await SourcesAsync(provider);
      QueryEngine engine = sources.Engine();
      string text = QueryText.Compose(
         "big := shop.orders.where(total > 10); big.select(id, total, who: customer.name)",
         [QueryText.QuoteName("who") + " != 'Beta Corp'"],
         [new QuerySortKey("total", Descending: true)],
         tiebreak: ["id"]);
      text.ShouldBe("big := shop.orders.where(total > 10); (big.select(id, total, who: customer.name)).where(who != 'Beta Corp').orderBy(desc(total), id)");
      (_, IReadOnlyList<object?[]> rows) = await RunAsync(engine, text);
      rows.Select(r => r[0]).ShouldBe([1001L, 1002L]);
   }
}
