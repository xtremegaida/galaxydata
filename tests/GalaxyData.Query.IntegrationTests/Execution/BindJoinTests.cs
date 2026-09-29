using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Execution;

/// <summary>Fragments fetched by the keys of the fragments they're joined to, and values worked out first.</summary>
public sealed class BindJoinTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   /// <summary>
   /// 1,000 orders in SQLite (sales); their 300 customers, 40 products and 60 calendar days in DuckDB (crm). Order n
   /// is for customer n % 300 + 1, product P-(n % 40), placed on day n % 30 of 2026, and totals 1.5 n.
   /// </summary>
   private static async Task<TestSources> ShopAsync()
   {
      TestSources sources = new();
      await sources.AddSqliteAsync("sales",
         "CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL, code TEXT NOT NULL, placed DATE NOT NULL, total DECIMAL(10,2) NOT NULL); " +
         "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 1000) " +
         "INSERT INTO orders SELECT i, i % 300 + 1, 'P-' || (i % 40), date('2026-01-01', '+' || (i % 30) || ' days'), i * 1.5 FROM n;");
      await sources.AddDuckDbAsync("crm",
         "CREATE TABLE customers (id INTEGER PRIMARY KEY, name VARCHAR NOT NULL, city VARCHAR); " +
         "INSERT INTO customers SELECT i, 'customer ' || i, CASE WHEN i % 7 = 0 THEN NULL ELSE 'city ' || (i % 5) END FROM range(1, 301) t(i); " +
         "CREATE TABLE products (code VARCHAR PRIMARY KEY, name VARCHAR NOT NULL); " +
         "INSERT INTO products SELECT 'P-' || i, 'product ' || i FROM range(0, 40) t(i); " +
         "CREATE TABLE calendar (day DATE PRIMARY KEY, holiday BOOLEAN NOT NULL); " +
         "INSERT INTO calendar SELECT DATE '2026-01-01' + i::INTEGER, i % 6 = 0 FROM range(0, 60) t(i);");
      return sources;
   }

   private static readonly CatalogOverlay Overlay = new()
   {
      Relations =
      [
         new OverlayRelation("sales.orders", ["customer_id"], "crm.customers", ["id"]) { Name = "customer", InverseName = "orders" },
         new OverlayRelation("sales.orders", ["code"], "crm.products", ["code"]) { Name = "product", InverseName = "orders" },
         new OverlayRelation("sales.orders", ["placed"], "crm.calendar", ["day"]) { Name = "day", InverseName = "orders" },
      ],
   };

   private static async Task<(string Rows, ExecutionStats Stats)> RunAsync(QueryEngine engine, QueryRequest request)
   {
      await using QueryResult result = await engine.ExecuteAsync(request, Token);
      return (await TestSources.RowsAsync(result), result.Stats);
   }

   /// <summary>The rows with every fragment fetched in full, to compare with.</summary>
   private static async Task<string> InFullAsync(TestSources sources, QueryRequest request) =>
      (await RunAsync(sources.Engine(Overlay, new QueryEngineOptions { BindJoins = BindJoinMode.Never }), request)).Rows;

   private static FragmentStats Fragment(ExecutionStats stats, string source) => stats.Fragments.Single(f => f.Source == source);

   [Fact]
   public async Task APageSendsNoMoreKeysThanItHasRows()
   {
      await using TestSources sources = await ShopAsync();
      QueryRequest request = new("sales.orders.select(id, who: customer.name).orderBy(id)") { Paging = new PageRequest(100, 50) };
      PreparedQuery prepared = sources.Engine(Overlay).Prepare(request);
      // The page is picked in SQLite, before the join.
      prepared.Fragments.Single(f => f.Source.Alias == "sales").Sql.ShouldContain("LIMIT 50 OFFSET 100");

      (string rows, ExecutionStats stats) = await RunAsync(sources.Engine(Overlay), request);
      rows.ShouldBe(await InFullAsync(sources, request));
      rows.ShouldStartWith("101 | 'customer 102'");
      Fragment(stats, "sales").Rows.ShouldBe(50);
      FragmentStats customers = Fragment(stats, "crm");
      customers.Strategy.ShouldBe(FetchStrategy.Keys);
      customers.Keys.ShouldBeLessThanOrEqualTo(50);
      customers.Rows.ShouldBeLessThanOrEqualTo(50);
      stats.KeysSent.ShouldBeLessThanOrEqualTo(50);
   }

   [Fact]
   public async Task NoKeysMeansNoFetch()
   {
      await using TestSources sources = await ShopAsync();
      (string rows, ExecutionStats stats) = await RunAsync(sources.Engine(Overlay), new QueryRequest("sales.orders.where(total < 0).select(id, who: customer.name)"));
      rows.ShouldBeEmpty();
      Fragment(stats, "crm").Strategy.ShouldBe(FetchStrategy.Skipped);
      sources.OpenedTo("crm").ShouldBe(0);

      // No customer is in 'nowhere', so no order can matter to whether one has none.
      QueryRequest anti = new("crm.customers.where(city == 'nowhere' and not orders.any()).count()");
      (rows, stats) = await RunAsync(sources.Engine(Overlay), anti);
      rows.ShouldBe("0" + System.Environment.NewLine);
      Fragment(stats, "sales").Strategy.ShouldBe(FetchStrategy.Skipped);
   }

   [Fact]
   public async Task MoreKeysThanAllowedFetchInFull()
   {
      await using TestSources sources = await ShopAsync();
      QueryRequest request = new("sales.orders.select(id, who: customer.name).orderBy(id)") { Paging = new PageRequest(0, 50) };
      (string rows, ExecutionStats stats) = await RunAsync(sources.Engine(Overlay, new QueryEngineOptions { MaxBindKeys = 10 }), request);
      rows.ShouldBe(await InFullAsync(sources, request));
      FragmentStats customers = Fragment(stats, "crm");
      customers.Strategy.ShouldBe(FetchStrategy.Full);
      customers.Rows.ShouldBe(300);
      stats.KeysSent.ShouldBe(0);
   }

   [Fact]
   public async Task KeysGoInBatches()
   {
      await using TestSources sources = await ShopAsync();
      QueryRequest request = new("sales.orders.select(id, who: customer.name).orderBy(id)") { Paging = new PageRequest(0, 50) };
      (string rows, ExecutionStats stats) = await RunAsync(sources.Engine(Overlay, new QueryEngineOptions { MaxBindBatch = 7 }), request);
      rows.ShouldBe(await InFullAsync(sources, request));
      FragmentStats customers = Fragment(stats, "crm");
      customers.Keys.ShouldBe(50);
      customers.Batches.ShouldBe(8);
      customers.Rows.ShouldBe(50);
   }

   [Fact]
   public async Task AsManyKeysAsRowsFetchInFullUnlessAlways()
   {
      await using TestSources sources = await ShopAsync();
      // Every customer has orders: 300 keys for a table of about 300 rows.
      QueryRequest request = new("sales.orders.groupBy(customer.city).select(city, n: count()).orderBy(city)");
      (string adaptive, ExecutionStats stats) = await RunAsync(sources.Engine(Overlay), request);
      Fragment(stats, "crm").Strategy.ShouldBe(FetchStrategy.Full);
      (string always, ExecutionStats forced) = await RunAsync(sources.Engine(Overlay, new QueryEngineOptions { BindJoins = BindJoinMode.Always }), request);
      Fragment(forced, "crm").Strategy.ShouldBe(FetchStrategy.Keys);
      Fragment(forced, "crm").Keys.ShouldBe(300);
      always.ShouldBe(adaptive);
      adaptive.ShouldBe(await InFullAsync(sources, request));
   }

   [Fact]
   public async Task TextAndDateKeys()
   {
      await using TestSources sources = await ShopAsync();
      QueryRequest request = new("sales.orders.where(id <= 20).select(id, p: product.name, holiday: day.holiday).orderBy(id)");
      (string rows, ExecutionStats stats) = await RunAsync(sources.Engine(Overlay), request);
      rows.ShouldBe(await InFullAsync(sources, request));
      rows.ShouldStartWith("1 | 'product 1' | false");
      stats.Fragments.Where(f => f.Source == "crm").Select(f => (f.Strategy, f.Keys)).ShouldBe([(FetchStrategy.Keys, 20), (FetchStrategy.Keys, 20)], ignoreOrder: true);
   }

   [Fact]
   public async Task SemiJoinsFetchByKeys()
   {
      await using TestSources sources = await ShopAsync();
      QueryRequest request = new("crm.customers.where(orders.any(total > 1400)).select(id, name).orderBy(id)");
      (string rows, ExecutionStats stats) = await RunAsync(sources.Engine(Overlay), request);
      rows.ShouldBe(await InFullAsync(sources, request));
      Fragment(stats, "sales").Strategy.ShouldBe(FetchStrategy.Keys);
   }

   [Fact]
   public async Task ScalarSubqueriesOfOtherSourcesAreWorkedOutFirst()
   {
      await using TestSources sources = await ShopAsync();
      QueryEngine engine = sources.Engine(Overlay);
      // 300 customers: the orders totalling more are 201 to 1000.
      QueryRequest request = new("sales.orders.where(total > crm.customers.count()).count()");
      PreparedQuery prepared = engine.Prepare(request);
      QueryFragment value = prepared.Fragments.Single(f => f.Value != null);
      value.Source.Alias.ShouldBe("crm");
      QueryFragment orders = prepared.Fragments.Single(f => f.Source.Alias == "sales");
      orders.Statement.Parameters.ShouldContain(p => p.Source == Planning.ParameterSource.Runtime && p.ParameterName == value.Value);

      (string rows, ExecutionStats stats) = await RunAsync(engine, request);
      rows.ShouldBe("800" + System.Environment.NewLine);
      stats.Fragments.Single(f => f.Strategy == FetchStrategy.Value).Source.ShouldBe("crm");
      // Only the orders that pass the filter are fetched.
      Fragment(stats, "sales").Rows.ShouldBe(800);
      rows.ShouldBe(await InFullAsync(sources, request));
   }
}
