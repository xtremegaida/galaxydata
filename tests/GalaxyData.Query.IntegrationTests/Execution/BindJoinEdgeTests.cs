using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Execution;

/// <summary>
/// Edges of bind joins, runtime values and pages pushed below joins: values the sources hold oddly, key types,
/// fragments that shape their rows, and shapes under a page. Fetching by keys must give the rows a full fetch does.
/// </summary>
public sealed class BindJoinEdgeTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   private static readonly QueryEngineOptions Never = new() { BindJoins = BindJoinMode.Never };

   private static readonly QueryEngineOptions Always = new() { BindJoins = BindJoinMode.Always, MaxBindBatch = 1 };

   private static async Task<string> RowsAsync(QueryEngine engine, string query, PageRequest? page = null)
   {
      await using QueryResult result = await engine.ExecuteAsync(new QueryRequest(query) { Paging = page }, Token);
      return await TestSources.RowsAsync(result);
   }

   private static string Explain(QueryEngine engine, string query) => Query.Explain.ExplainTextRenderer.Render(engine.Prepare(query).Explain());

   /// <summary>Runs the query with bind joins off and forced (one key per batch); both must give the same rows.</summary>
   private static async Task SameAsync(TestSources sources, CatalogOverlay overlay, string query, string? expected = null)
   {
      string never = await RowsAsync(sources.Engine(overlay, Never), query);
      if (expected != null) { never.ShouldBe(expected, "Never: " + query); }
      string always = await RowsAsync(sources.Engine(overlay, Always), query);
      always.ShouldBe(never, query + Environment.NewLine + Explain(sources.Engine(overlay, Always), query));
      sources.Merge.ActiveSessions.ShouldBe(0);
   }

   // ---------------------------------------------------------------- keys compared differently in the source

   /// <summary>A DATE column of SQLite holding '2026-01-05 00:00:00' (as .NET DateTime parameters write it) reads as that date.</summary>
   [Fact]
   public async Task SqliteDatesWithATimePartAreFoundByKey()
   {
      await using TestSources sources = new();
      await sources.AddSqliteAsync("sales", "CREATE TABLE orders (id INTEGER PRIMARY KEY, placed DATE NOT NULL); " +
         "INSERT INTO orders VALUES (1, '2026-01-05 00:00:00'), (2, '2026-01-06'), (3, '2026-01-07T00:00:00');");
      await sources.AddDuckDbAsync("crm", "CREATE TABLE calendar (day DATE PRIMARY KEY, holiday BOOLEAN NOT NULL); " +
         "INSERT INTO calendar VALUES (DATE '2026-01-05', true), (DATE '2026-01-06', false), (DATE '2026-01-07', true);");
      CatalogOverlay overlay = new() { Relations = [new OverlayRelation("sales.orders", ["placed"], "crm.calendar", ["day"]) { Name = "day", InverseName = "orders" }] };
      await SameAsync(sources, overlay, "crm.calendar.select(day, n: orders.count()).orderBy(day)");
      await SameAsync(sources, overlay, "crm.calendar.where(orders.any()).select(day).orderBy(day)");
   }

   /// <summary>A DATETIME column of SQLite holding a date-time with milliseconds (strftime('%f') writes three digits).</summary>
   [Fact]
   public async Task SqliteDateTimesWithMillisecondsAreFoundByKey()
   {
      await using TestSources sources = new();
      await sources.AddSqliteAsync("log", "CREATE TABLE events (id INTEGER PRIMARY KEY, ts DATETIME NOT NULL); " +
         "INSERT INTO events VALUES (1, '2026-01-05 10:00:00.500'), (2, '2026-01-05 11:00:00');");
      await sources.AddDuckDbAsync("crm", "CREATE TABLE marks (ts TIMESTAMP PRIMARY KEY, label VARCHAR NOT NULL); " +
         "INSERT INTO marks VALUES (TIMESTAMP '2026-01-05 10:00:00.5', 'a'), (TIMESTAMP '2026-01-05 11:00:00', 'b');");
      CatalogOverlay overlay = new() { Relations = [new OverlayRelation("log.events", ["ts"], "crm.marks", ["ts"]) { Name = "mark", InverseName = "events" }] };
      await SameAsync(sources, overlay, "crm.marks.where(events.any()).select(label).orderBy(label)", "'a'" + Environment.NewLine + "'b'" + Environment.NewLine);
   }

   /// <summary>SQLite doesn't round what a DECIMAL(10,2) column holds; the engine does when it reads it.</summary>
   [Fact]
   public async Task SqliteDecimalsHeldUnroundedAreFoundByKey()
   {
      await using TestSources sources = new();
      await sources.AddSqliteAsync("sales", "CREATE TABLE prices (amount DECIMAL(10,2) PRIMARY KEY, label TEXT NOT NULL); " +
         "INSERT INTO prices VALUES (10.0 / 3, 'third'), (2.5, 'half');");
      await sources.AddDuckDbAsync("crm", "CREATE TABLE items (id INTEGER PRIMARY KEY, amount DECIMAL(10,2) NOT NULL); " +
         "INSERT INTO items VALUES (1, 3.33), (2, 2.50);");
      CatalogOverlay overlay = new() { Relations = [new OverlayRelation("crm.items", ["amount"], "sales.prices", ["amount"]) { Name = "price" }] };
      await SameAsync(sources, overlay, "crm.items.select(id, amount, l: price.label).orderBy(id)",
         "1 | 3.33 | 'third'" + Environment.NewLine + "2 | 2.50 | 'half'" + Environment.NewLine);
   }

   /// <summary>Guids SQLite holds as lower-case text read as guids; the key lookup sends upper-case text.</summary>
   [Fact]
   public async Task SqliteLowerCaseGuidsAreFoundByKey()
   {
      await using TestSources sources = new();
      await sources.AddSqliteAsync("sales", "CREATE TABLE accounts (id GUID PRIMARY KEY, name TEXT NOT NULL); " +
         "INSERT INTO accounts VALUES ('6f9619ff-8b86-d011-b42d-00c04fc964ff', 'acme');");
      await sources.AddDuckDbAsync("crm", "CREATE TABLE contacts (id INTEGER PRIMARY KEY, account UUID NOT NULL); " +
         "INSERT INTO contacts VALUES (1, '6f9619ff-8b86-d011-b42d-00c04fc964ff');");
      CatalogOverlay overlay = new() { Relations = [new OverlayRelation("crm.contacts", ["account"], "sales.accounts", ["id"]) { Name = "acct" }] };
      await SameAsync(sources, overlay, "crm.contacts.select(id, a: acct.name).orderBy(id)", "1 | 'acme'" + Environment.NewLine);
   }

   /// <summary>
   /// Keys that differ only in case are two keys; a NOCASE column finds the same row for both, so a row is fetched
   /// once per batch that has one of them.
   /// </summary>
   [Fact]
   public async Task CaseInsensitiveKeysInTwoBatchesDontDuplicateRows()
   {
      await using TestSources sources = new();
      await sources.AddSqliteAsync("sales", "CREATE TABLE products (code TEXT COLLATE NOCASE PRIMARY KEY, name TEXT NOT NULL); INSERT INTO products VALUES ('p-1', 'one');");
      await sources.AddDuckDbAsync("crm", "CREATE TABLE lines (id INTEGER PRIMARY KEY, code VARCHAR NOT NULL); INSERT INTO lines VALUES (1, 'p-1'), (2, 'P-1');");
      CatalogOverlay overlay = new() { Relations = [new OverlayRelation("crm.lines", ["code"], "sales.products", ["code"]) { Name = "product" }] };
      await SameAsync(sources, overlay, "crm.lines.select(id, p: product.name).orderBy(id)");
      await SameAsync(sources, overlay, "crm.lines.count(l => l.product.name == 'one')");
   }

   /// <summary>Ids declared NUMERIC(19) (as schemas moved from Oracle have them) are decimals; SQLite holds them as integers.</summary>
   [Fact]
   public async Task SqliteLargeNumericKeysAreFoundByKey()
   {
      await using TestSources sources = new();
      await sources.AddSqliteAsync("sales", "CREATE TABLE accounts (id NUMERIC(19,0) PRIMARY KEY, name TEXT NOT NULL); INSERT INTO accounts VALUES (1234567890123456789, 'big'), (7, 'small');");
      await sources.AddDuckDbAsync("crm", "CREATE TABLE contacts (id INTEGER PRIMARY KEY, account BIGINT NOT NULL); INSERT INTO contacts VALUES (1, 1234567890123456789), (2, 7);");
      CatalogOverlay overlay = new() { Relations = [new OverlayRelation("crm.contacts", ["account"], "sales.accounts", ["id"]) { Name = "acct" }] };
      await SameAsync(sources, overlay, "crm.contacts.select(id, a: acct.name).orderBy(id)", "1 | 'big'" + Environment.NewLine + "2 | 'small'" + Environment.NewLine);
   }

   /// <summary>The same with the engine's default options (adaptive bind joins).</summary>
   [Fact]
   public async Task SqliteLargeNumericKeysWithDefaultOptions()
   {
      await using TestSources sources = new();
      await sources.AddSqliteAsync("sales", "CREATE TABLE accounts (id NUMERIC(19,0) PRIMARY KEY, name TEXT NOT NULL); INSERT INTO accounts VALUES (1234567890123456789, 'big'), (7, 'small');");
      await sources.AddDuckDbAsync("crm", "CREATE TABLE contacts (id INTEGER PRIMARY KEY, account BIGINT NOT NULL); INSERT INTO contacts VALUES (1, 1234567890123456789), (2, 7);");
      CatalogOverlay overlay = new() { Relations = [new OverlayRelation("crm.contacts", ["account"], "sales.accounts", ["id"]) { Name = "acct" }] };
      (await RowsAsync(sources.Engine(overlay), "crm.contacts.select(id, a: acct.name).orderBy(id)")).ShouldBe("1 | 'big'" + Environment.NewLine + "2 | 'small'" + Environment.NewLine);
   }

   /// <summary>
   /// The same values as runtime values: the source compares the subquery's value with its rows, as it would the
   /// value written in the query. Large whole decimals are sent to SQLite as integers, so they find their rows;
   /// SQLite compares a date it holds with a time as text, as it does in a query of it alone (the second query).
   /// </summary>
   [Theory]
   [InlineData("sales.accounts.where(id == crm.contacts.max(account)).select(name)", "'big'\n")]
   [InlineData("sales.accounts.where(id == crm.contacts.max(account_d)).select(name)", "'big'\n")]
   [InlineData("sales.orders.where(placed == crm.calendar.min(day)).select(id)", "sales.orders.where(placed == toDate('2026-01-05')).select(id)")]
   [InlineData("sales.orders.where(placed <= crm.calendar.min(day)).select(id)", "sales.orders.where(placed <= toDate('2026-01-05')).select(id)")]
   public async Task RuntimeValuesComparedInSqlite(string query, string expected)
   {
      await using TestSources sources = new();
      await sources.AddSqliteAsync("sales", "CREATE TABLE accounts (id NUMERIC(19,0) PRIMARY KEY, name TEXT NOT NULL); INSERT INTO accounts VALUES (1234567890123456789, 'big'), (7, 'small'); " +
         "CREATE TABLE orders (id INTEGER PRIMARY KEY, placed DATE NOT NULL); INSERT INTO orders VALUES (1, '2026-01-05 00:00:00'), (2, '2026-01-06 00:00:00');");
      await sources.AddDuckDbAsync("crm", "CREATE TABLE contacts (id INTEGER PRIMARY KEY, account BIGINT NOT NULL, account_d DECIMAL(19,0) NOT NULL); " +
         "INSERT INTO contacts VALUES (1, 1234567890123456789, 1234567890123456789), (2, 7, 7); " +
         "CREATE TABLE calendar (day DATE PRIMARY KEY); INSERT INTO calendar VALUES (DATE '2026-01-05'), (DATE '2026-01-06');");
      QueryEngine engine = sources.Engine();
      if (expected.StartsWith("sales.", StringComparison.Ordinal)) { expected = (await RowsAsync(engine, expected)).Replace("\r\n", "\n"); }
      (await RowsAsync(engine, query)).Replace("\r\n", "\n").ShouldBe(expected, query + Environment.NewLine + Explain(engine, query));
   }

   /// <summary>Keys of other types between two DuckDB sources: time, interval, timestamptz.</summary>
   [Theory]
   [InlineData("TIME", "TIME '10:00:00'")]
   [InlineData("INTERVAL", "INTERVAL 3 DAY")]
   [InlineData("TIMESTAMPTZ", "TIMESTAMPTZ '2026-01-05 10:00:00+00'")]
   [InlineData("UBIGINT", "18446744073709551615")]
   public async Task OtherKeyTypes(string type, string value)
   {
      await using TestSources sources = new();
      await sources.AddDuckDbAsync("a", $"CREATE TABLE t (k {type}, name VARCHAR NOT NULL); INSERT INTO t VALUES ({value}, 'x');");
      await sources.AddDuckDbAsync("b", $"CREATE TABLE r (id INTEGER PRIMARY KEY, k {type} NOT NULL); INSERT INTO r VALUES (1, {value});");
      await SameAsync(sources, CatalogOverlay.Empty, "b.r.leftJoin(a.t, outer.k == inner.k, r: outer, t: inner).select(r.id, n: t.name).orderBy(id)", "1 | 'x'" + Environment.NewLine);
   }

   /// <summary>Target fragments whose SQL limits, de-duplicates or unions rows: the keys filter the result of it.</summary>
   [Theory]
   [InlineData("sales.orders.where(customer_id in crm.customers.orderBy(desc(id)).take(5).select(id)).count()", "15\n")]
   [InlineData("sales.orders.where(customer_id in crm.customers.select(id).distinct()).count()", "100\n")]
   [InlineData("sales.orders.where(customer_id in crm.customers.where(id < 3).select(id).union(crm.customers.where(id > 28).select(id))).count()", "13\n")]
   [InlineData("sales.orders.where(code in crm.products.orderBy(code).skip(1).take(2).select(code)).count()", "50\n")]
   [InlineData("crm.customers.where(id in sales.orders.orderBy(desc(total)).take(3).select(customer_id)).select(id).orderBy(id)", "9\n10\n11\n")]
   [InlineData("crm.customers.where(id in sales.orders.groupBy(customer_id).where(count() > 3).select(customer_id)).count()", "10\n")]
   public async Task TargetRootsThatShapeRows(string query, string expected)
   {
      await using TestSources sources = await ShopAsync();
      foreach (QueryEngineOptions options in new[] { Never, Always })
      {
         string rows = await RowsAsync(sources.Engine(ShopOverlay, options), query);
         rows.Replace("\r\n", "\n").ShouldBe(expected, query + Environment.NewLine + Explain(sources.Engine(ShopOverlay, options), query));
      }
   }

   /// <summary>A group key that is a subquery's value still groups: no rows in, no groups out.</summary>
   [Fact]
   public async Task AGroupKeyThatIsAValueOfAnotherSource()
   {
      await using TestSources sources = await ShopAsync();
      string rows = await RowsAsync(sources.Engine(ShopOverlay), "sales.orders.where(total < 0).groupBy(k: crm.customers.count()).select(k, n: count())");
      rows.ShouldBeEmpty(Explain(sources.Engine(ShopOverlay), "sales.orders.where(total < 0).groupBy(k: crm.customers.count()).select(k, n: count())"));
   }

   /// <summary>The same with a literal key in one database (to tell whether this is new).</summary>
   [Fact]
   public async Task AGroupKeyThatIsALiteral()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      string rows = await RowsAsync(sources.Engine(), "shop.orders.where(total < 0).groupBy(k: 1).select(k, n: count())");
      rows.ShouldBeEmpty();
   }

   /// <summary>The same with a subquery key in one database.</summary>
   [Fact]
   public async Task AGroupKeyThatIsASubqueryInOneDatabase()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      string rows = await RowsAsync(sources.Engine(), "shop.orders.where(total < 0).groupBy(k: shop.customers.count()).select(k, n: count())");
      rows.ShouldBeEmpty();
   }

   [Fact]
   public async Task CountsPagesAndParametersWithValues()
   {
      await using TestSources sources = await ShopAsync();
      QueryEngine engine = sources.Engine(ShopOverlay);
      PreparedQuery count = engine.Prepare("sales.orders.where(total > crm.customers.count())").ForCount();
      await using (QueryResult result = await count.ExecuteAsync(Token))
      {
         (await TestSources.RowsAsync(result)).ShouldBe("80" + Environment.NewLine);
      }
      (await RowsAsync(engine, "sales.orders.where(total > crm.customers.count()).select(id, who: customer.name).orderBy(id)", new PageRequest(2, 2)))
         .ShouldBe("23 | 'customer 24'" + Environment.NewLine + "24 | 'customer 25'" + Environment.NewLine);
      QueryRequest request = new("sales.orders.where(total > crm.customers.where(id > $n).count()).count()") { Parameters = new QueryParameters().Add("n", 20L) };
      await using (QueryResult result = await engine.ExecuteAsync(request, Token))
      {
         (await TestSources.RowsAsync(result)).ShouldBe("94" + Environment.NewLine);
      }
      sources.Merge.ActiveSessions.ShouldBe(0);
   }

   // ---------------------------------------------------------------- split vs one database, with irregular data

   private const string OrdersSqlite =
      "CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER, code TEXT, placed DATE NOT NULL, total DECIMAL(10,2) NOT NULL); " +
      "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 200) " +
      "INSERT INTO orders SELECT i, CASE WHEN i % 17 = 0 THEN NULL WHEN i % 13 = 0 THEN 999 ELSE i % 40 + 1 END, CASE WHEN i % 11 = 0 THEN NULL ELSE 'P-' || (i % 6) END, " +
      "date('2026-01-01', '+' || (i % 15) || ' days'), i * 1.25 FROM n; " +
      "CREATE TABLE lines (order_id INTEGER NOT NULL, line_no INTEGER NOT NULL, qty INTEGER NOT NULL, PRIMARY KEY (order_id, line_no)); " +
      "INSERT INTO lines SELECT id, 1, id % 5 FROM orders WHERE id % 3 <> 0; INSERT INTO lines SELECT id, 2, 2 FROM orders WHERE id % 4 = 0;";

   private const string CrmDuckDb =
      "CREATE TABLE customers (id INTEGER PRIMARY KEY, name VARCHAR NOT NULL, city VARCHAR, limit_ DECIMAL(10,2)); " +
      "INSERT INTO customers SELECT i, 'customer ' || i, CASE WHEN i % 7 = 0 THEN NULL ELSE 'city ' || (i % 5) END, i * 6.5 FROM range(1, 41) t(i); " +
      "CREATE TABLE products (code VARCHAR PRIMARY KEY, name VARCHAR NOT NULL); INSERT INTO products SELECT 'P-' || i, 'product ' || i FROM range(0, 5) t(i); " +
      "CREATE TABLE calendar (day DATE PRIMARY KEY, holiday BOOLEAN NOT NULL); INSERT INTO calendar SELECT DATE '2026-01-01' + i::INTEGER, i % 4 = 0 FROM range(0, 12) t(i);";

   private const string OrdersDuckDb =
      "CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER, code VARCHAR, placed DATE NOT NULL, total DECIMAL(10,2) NOT NULL); " +
      "INSERT INTO orders SELECT i, CASE WHEN i % 17 = 0 THEN NULL WHEN i % 13 = 0 THEN 999 ELSE i % 40 + 1 END, CASE WHEN i % 11 = 0 THEN NULL ELSE 'P-' || (i % 6) END, " +
      "DATE '2026-01-01' + (i % 15)::INTEGER, i * 1.25 FROM range(1, 201) t(i); " +
      "CREATE TABLE lines (order_id INTEGER NOT NULL, line_no INTEGER NOT NULL, qty INTEGER NOT NULL, PRIMARY KEY (order_id, line_no)); " +
      "INSERT INTO lines SELECT id, 1, id % 5 FROM orders WHERE id % 3 <> 0; INSERT INTO lines SELECT id, 2, 2 FROM orders WHERE id % 4 = 0;";

   private static CatalogOverlay Relations(string sales, string crm) => new()
   {
      Relations =
      [
         new OverlayRelation($"{sales}.orders", ["customer_id"], $"{crm}.customers", ["id"]) { Name = "customer", InverseName = "orders" },
         new OverlayRelation($"{sales}.orders", ["code"], $"{crm}.products", ["code"]) { Name = "product", InverseName = "orders" },
         new OverlayRelation($"{sales}.orders", ["placed"], $"{crm}.calendar", ["day"]) { Name = "day", InverseName = "orders" },
         new OverlayRelation($"{sales}.lines", ["order_id"], $"{sales}.orders", ["id"]) { Name = "order", InverseName = "lines" },
      ],
   };

   public static readonly TheoryData<string> Irregular =
   [
      "sales.orders.select(id, who: customer.name, p: product.name, h: day.holiday).orderBy(id).skip(10).take(7)",
      "crm.customers.select(id, n: orders.count(), s: orders.sum(total), big: orders.any(total > 100)).orderBy(id)",
      "crm.customers.where(not orders.any(total > 140)).select(id).orderBy(id)",
      "crm.customers.where(city == 'city 1' and orders.any(o => o.product.name == 'product 2')).select(id).orderBy(id)",
      "sales.orders.where(customer.city == null).select(id).orderBy(id)",
      "sales.orders.where(customer.city == 'city 2' or product.name == 'product 1').select(id).orderBy(id)",
      "crm.products.select(code, n: orders.where(o => o.customer.city != null).count()).orderBy(code)",
      "sales.orders.join(crm.customers, outer.customer_id == inner.id and inner.city == 'city 3', o: outer, c: inner).select(o.id, c.name).orderBy(id)",
      "sales.orders.leftJoin(crm.customers, outer.customer_id == inner.id and inner.city == 'city 3', o: outer, c: inner).select(o.id, c.name).orderBy(id)",
      "crm.customers.selectMany(orders, who: outer.name, t: inner.total).orderBy(who, t).take(9)",
      "crm.customers.select(id, last: orders.orderBy(desc(placed), id).firstOrDefault().total).orderBy(id)",
      "crm.customers.select(id, f: orders.orderBy(total).firstOrDefault().product.name).orderBy(id)",
      "sales.orders.groupBy(customer.city).select(city, n: count()).orderBy(city)",
      "sales.orders.where(customer_id in crm.customers.where(city == 'city 1').select(id)).count()",
      "crm.customers.where(id in sales.orders.where(total > 100).select(customer_id)).count()",
      "sales.orders.where(customer.orders.count() > 4).count()",
      "sales.orders.where(total > customer.limit_).select(id).orderBy(id)",
      "crm.calendar.select(day, n: orders.count(), holiday).where(n > 12).orderBy(day)",
      "crm.customers.where(orders.all(total > 10)).count()",
      "sales.orders.select(c: customer.city ?? 'none').distinct().orderBy(c)",
      "sales.orders.select(customer_id).union(crm.customers.select(customer_id: id)).count()",
      "crm.customers.where(c => sales.orders.any(o => o.customer_id == c.id and o.code == 'P-1')).count()",
      "sales.orders.where(product.code == 'P-1').select(id, who: customer.name).orderBy(id).take(3)",
      "crm.customers.select(id, n: orders.count(o => o.product.name == 'product 1')).orderBy(id)",
      "crm.customers.where(c => sales.orders.where(o => o.customer_id == c.id).any(o => o.product.name == 'product 3')).count()",
      "sales.lines.select(order_id, line_no, c: order.customer.city, p: order.product.name).orderBy(order_id, line_no).skip(20).take(10)",
      "sales.lines.where(order.customer.city == 'city 4').select(order_id, line_no).orderBy(order_id, line_no)",
      "crm.customers.select(id, q: orders.sum(o => o.lines.sum(qty))).orderBy(id)",
      "crm.products.where(p => p.orders.any(o => o.customer.city == 'city 2' and o.lines.any(qty > 3))).select(code).orderBy(code)",
      "sales.orders.where(day.holiday and customer.city != null).select(id, placed).orderBy(id)",
      "sales.orders.where(not day.holiday).count()",
      "sales.orders.where(day == null).count()",
      "sales.orders.where(product == null or customer == null).count()",
      "sales.orders.orderBy(desc(total)).where(customer.city != null).take(4).select(id, who: customer.name)",
      "sales.orders.orderBy(customer.name, id).take(4).select(id, who: customer.name)",
      "crm.customers.orderBy(desc(limit_)).take(3).select(id, n: orders.count())",
      "crm.customers.where(orders.count() > 4).orderBy(id).take(3).select(id)",
      "sales.orders.where(total > crm.customers.max(limit_) / 2 and customer.city != null).count()",
      "sales.orders.where(customer.limit_ > sales.orders.avg(total)).count()",
      "crm.customers.where(c => c.orders.count() > sales.orders.count() / 40).select(id).orderBy(id)",
      "sales.orders.groupBy(product).select(product.name, n: count(), s: sum(total)).orderBy(name, n)",
      "sales.orders.groupBy(customer_id).where(count() > 5).select(customer_id, who: crm.customers.where(c => c.id == customer_id).firstOrDefault().name).orderBy(customer_id)",
      "crm.customers.select(id, x: orders.select(code).distinct().count()).orderBy(id)",
      "sales.orders.where(code in crm.products.where(name != 'product 0').select(code)).count()",
      "sales.orders.where(not (code in crm.products.where(name != 'product 0').select(code))).count()",
      "sales.orders.where(customer_id in crm.customers.select(id) or code == 'P-5').count()",
      "crm.customers.where(orders.any() and not orders.any(o => o.product == null)).select(id).orderBy(id)",
   ];

   [Theory]
   [MemberData(nameof(Irregular))]
   public async Task IrregularDataSplitMatchesOneDatabase(string query)
   {
      await using TestSources whole = new();
      await whole.AddDuckDbAsync("one", OrdersDuckDb + " " + CrmDuckDb);
      await using TestSources split = new();
      await split.AddSqliteAsync("sales", OrdersSqlite);
      await split.AddDuckDbAsync("crm", CrmDuckDb);
      string one = query.Replace("sales.", "one.", StringComparison.Ordinal).Replace("crm.", "one.", StringComparison.Ordinal);
      string expected = await RowsAsync(whole.Engine(Relations("one", "one")), one);
      CatalogOverlay overlay = Relations("sales", "crm");
      foreach (QueryEngineOptions options in new[] { Never, Always, new QueryEngineOptions(), new QueryEngineOptions { BindJoins = BindJoinMode.Always, MaxParallelFetches = 1, MaxBindBatch = 3 } })
      {
         string rows = await RowsAsync(split.Engine(overlay, options), query);
         rows.ShouldBe(expected, $"{options.BindJoins}/{options.MaxBindBatch}: {query}{Environment.NewLine}{Explain(split.Engine(overlay, options), query)}");
      }
      split.Merge.ActiveSessions.ShouldBe(0);
   }

   /// <summary>The same with default options: 3,000 keys go in two batches, and case variants land in both.</summary>
   [Fact]
   public async Task CaseInsensitiveKeysWithDefaultOptions()
   {
      await using TestSources sources = new();
      await sources.AddSqliteAsync("sales", "CREATE TABLE products (code TEXT COLLATE NOCASE PRIMARY KEY, name TEXT NOT NULL); " +
         "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 1500) INSERT INTO products SELECT 'p-' || i, 'n' || i FROM n;");
      await sources.AddDuckDbAsync("crm", "CREATE TABLE lines (id INTEGER PRIMARY KEY, code VARCHAR NOT NULL); " +
         "INSERT INTO lines SELECT i, 'p-' || i FROM range(1, 1501) t(i); INSERT INTO lines SELECT i + 1500, 'P-' || i FROM range(1, 1501) t(i);");
      CatalogOverlay overlay = new() { Relations = [new OverlayRelation("crm.lines", ["code"], "sales.products", ["code"]) { Name = "product" }] };
      await using QueryResult result = await sources.Engine(overlay).ExecuteAsync(new QueryRequest("crm.lines.select(id, p: product.name)"), Token);
      (await result.ToListAsync(Token)).Count.ShouldBe(3000, string.Join(", ", result.Stats.Fragments.Select(f => $"{f.Source} {f.Strategy} keys {f.Keys} batches {f.Batches} rows {f.Rows}")));
   }

   /// <summary>A page of a group join: the groups a source makes may be one key in the merge engine (dates held as different text).</summary>
   [Fact]
   public async Task APageOfAGroupJoinHasNoMoreRowsThanThePage()
   {
      await using TestSources sources = new();
      await sources.AddSqliteAsync("sales", "CREATE TABLE orders (id INTEGER PRIMARY KEY, placed DATE NOT NULL); " +
         "INSERT INTO orders VALUES (1, '2026-01-05 00:00:00'), (2, '2026-01-05'), (3, '2026-01-06');");
      await sources.AddDuckDbAsync("crm", "CREATE TABLE calendar (day DATE PRIMARY KEY); INSERT INTO calendar VALUES (DATE '2026-01-05'), (DATE '2026-01-06'), (DATE '2026-01-07');");
      CatalogOverlay overlay = new() { Relations = [new OverlayRelation("sales.orders", ["placed"], "crm.calendar", ["day"]) { Name = "day", InverseName = "orders" }] };
      QueryEngine engine = sources.Engine(overlay, Never);
      string rows = await RowsAsync(engine, "crm.calendar.select(day, n: orders.count()).orderBy(day).take(2)");
      rows.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length.ShouldBeLessThanOrEqualTo(2, rows + Explain(engine, "crm.calendar.select(day, n: orders.count()).orderBy(day).take(2)"));
   }

   /// <summary>A virtual entity whose rows are an entity's twice has no key, so no relation can lead to it.</summary>
   [Fact]
   public async Task AConcatenatedVirtualEntityHasNoKey()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      CatalogOverlay overlay = new()
      {
         VirtualEntities = [new OverlayVirtualEntity("reports.twice", "shop.customers.concat(shop.customers)")],
         Relations = [new OverlayRelation("shop.orders", ["customer_id"], "reports.twice", ["id"]) { Name = "twice" }],
      };
      QueryCatalog catalog = sources.Catalog(overlay);
      catalog.FindEntity("reports.twice")!.Key.ShouldBeNull();
      catalog.Diagnostics.ShouldContain(d => d.Code == Diagnostics.DiagnosticCodes.RelationNotUnique);
   }

   /// <summary>A key declared for rows that repeat it: a page pushed below the join to them still has no more rows than the page.</summary>
   [Fact]
   public async Task APageOfAJoinToAWronglyKeyedEntityIsStillAPage()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      CatalogOverlay overlay = new()
      {
         VirtualEntities = [new OverlayVirtualEntity("reports.twice", "shop.customers.concat(shop.customers)") { Key = ["id"] }],
         Relations = [new OverlayRelation("shop.orders", ["customer_id"], "reports.twice", ["id"]) { Name = "twice" }],
      };
      sources.Catalog(overlay).Diagnostics.ShouldBeEmpty();
      const string query = "shop.orders.select(id, who: twice.name).orderBy(id).take(2)";
      string expected = "1001 | 'Acme Ltd'" + Environment.NewLine + "1001 | 'Acme Ltd'" + Environment.NewLine;
      (await RowsAsync(sources.Engine(overlay, new QueryEngineOptions { Optimize = false }), query)).ShouldBe(expected);
      (await RowsAsync(sources.Engine(overlay), query)).ShouldBe(expected);
   }

   [Theory]
   [InlineData("x := shop.orders; x.join(x, outer.id == inner.id, a: outer, b: inner).select(a.id, b.total).orderBy(id).take(2)")]
   [InlineData("x := shop.orders.select(id, total, c: customer.name); x.join(x, outer.id == inner.id, a: outer, b: inner).select(a.id, b.c).orderBy(id).skip(1).take(2)")]
   [InlineData("shop.orders.select(id, a: total, b: total).orderBy(a, b).take(2)")]
   [InlineData("shop.orders.select(id, who: customer.name, again: customer.name).orderBy(id).take(2)")]
   [InlineData("shop.orders.select(id, c: customer, d: customer).orderBy(id).take(2)")]
   [InlineData("shop.customers.select(name, n: orders.count(), m: orders.count()).orderBy(n, name).take(2)")]
   public async Task OddShapesUnderAPage(string query)
   {
      await using TestSources sqlite = await TestSources.SqliteShopAsync();
      string expected = await RowsAsync(sqlite.Engine(Conformance.Overlay, new QueryEngineOptions { Optimize = false }), query);
      (await RowsAsync(sqlite.Engine(Conformance.Overlay), query)).ShouldBe(expected, query);
      await using TestSources split = await Conformance.SplitShopAsync();
      (await RowsAsync(split.Engine(Conformance.SplitOverlay, Always), Conformance.Split(query))).ShouldBe(expected, "split " + query);
   }

   // ---------------------------------------------------------------- options

   [Fact]
   public async Task NoLimitOnKeys()
   {
      await using TestSources sources = await Conformance.SplitShopAsync();
      QueryEngine engine = sources.Engine(Conformance.SplitOverlay, new QueryEngineOptions { MaxBindKeys = int.MaxValue });
      string rows = await RowsAsync(engine, "sales.orders.where(status == 'open').select(id, who: customer.name).orderBy(id)");
      rows.ShouldBe("1001 | 'Acme Ltd'" + Environment.NewLine + "1003 | 'Beta Corp'" + Environment.NewLine);
   }

   // ---------------------------------------------------------------- runtime values

   private static async Task<TestSources> ShopAsync()
   {
      TestSources sources = new();
      await sources.AddSqliteAsync("sales",
         "CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL, code TEXT NOT NULL, placed DATE NOT NULL, total DECIMAL(10,2) NOT NULL); " +
         "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 100) " +
         "INSERT INTO orders SELECT i, i % 30 + 1, 'P-' || (i % 4), date('2026-01-01', '+' || (i % 10) || ' days'), i * 1.5 FROM n;");
      await sources.AddDuckDbAsync("crm",
         "CREATE TABLE customers (id INTEGER PRIMARY KEY, name VARCHAR NOT NULL, city VARCHAR, limit_ DECIMAL(10,2)); " +
         "INSERT INTO customers SELECT i, 'customer ' || i, CASE WHEN i % 7 = 0 THEN NULL ELSE 'city ' || (i % 5) END, i * 2.25 FROM range(1, 31) t(i); " +
         "CREATE TABLE products (code VARCHAR PRIMARY KEY, name VARCHAR NOT NULL); " +
         "INSERT INTO products SELECT 'P-' || i, 'product ' || i FROM range(0, 4) t(i); " +
         "CREATE TABLE calendar (day DATE PRIMARY KEY, holiday BOOLEAN NOT NULL); " +
         "INSERT INTO calendar SELECT DATE '2026-01-01' + i::INTEGER, i % 3 = 0 FROM range(0, 10) t(i);");
      return sources;
   }

   private static readonly CatalogOverlay ShopOverlay = new()
   {
      Relations =
      [
         new OverlayRelation("sales.orders", ["customer_id"], "crm.customers", ["id"]) { Name = "customer", InverseName = "orders" },
         new OverlayRelation("sales.orders", ["code"], "crm.products", ["code"]) { Name = "product", InverseName = "orders" },
         new OverlayRelation("sales.orders", ["placed"], "crm.calendar", ["day"]) { Name = "day", InverseName = "orders" },
      ],
   };

   [Theory]
   [InlineData("crm.customers.select(id, n: sales.orders.count()).orderBy(id).take(2)", "1 | 100\n2 | 100\n")]
   [InlineData("sales.orders.select(id, n: crm.customers.count()).orderBy(id).take(2)", "1 | 30\n2 | 30\n")]
   [InlineData("crm.customers.where(limit_ > sales.orders.avg(total) / 2).count()", "14\n")]
   [InlineData("sales.orders.where(total > crm.customers.avg(limit_)).count()", "77\n")]
   [InlineData("sales.orders.where(placed == crm.calendar.max(day)).count()", "10\n")]
   [InlineData("crm.calendar.where(day == sales.orders.max(placed)).count()", "1\n")]
   [InlineData("sales.orders.where(total > crm.customers.where(id > 1000).max(id)).count()", "0\n")]
   [InlineData("sales.orders.where(not (total > crm.customers.where(id > 1000).max(id))).count()", "0\n")]
   [InlineData("sales.orders.where(total > crm.customers.count()).select(id, n: crm.customers.count(), who: customer.name).orderBy(id).take(1)", "21 | 30 | 'customer 22'\n")]
   [InlineData("crm.customers.where(c => c.orders.any(o => o.total > crm.calendar.count() * 14)).count()", "7\n")]
   [InlineData("sales.orders.count() + crm.customers.count()", "130\n")]
   [InlineData("sales.orders.avg(total) + crm.customers.avg(limit_)", "110.625\n")]
   [InlineData("crm.calendar.max(day) == sales.orders.max(placed)", "true\n")]
   [InlineData("iif(crm.customers.count() > sales.orders.count(), 'a', 'b')", "'b'\n")]
   [InlineData("addDays(crm.calendar.max(day), 1)", "2026-01-11\n")]
   [InlineData("crm.customers.where(id > 1000).max(id) + sales.orders.count()", "null\n")]
   [InlineData("coalesce(crm.customers.where(id > 1000).max(id), sales.orders.count())", "100\n")]
   [InlineData("crm.customers.where(id > 1000).max(name) ?? sales.orders.max(code)", "'P-3'\n")]
   [InlineData("crm.calendar.where(day > toDate('2030-01-01')).max(day) ?? sales.orders.max(placed)", "2026-01-10\n")]
   public async Task RuntimeValues(string query, string expected)
   {
      await using TestSources sources = await ShopAsync();
      foreach (QueryEngineOptions options in new[] { Never, Always, new QueryEngineOptions { MaxParallelFetches = 1 } })
      {
         string rows = await RowsAsync(sources.Engine(ShopOverlay, options), query);
         rows.Replace("\r\n", "\n").ShouldBe(expected, query + Environment.NewLine + Explain(sources.Engine(ShopOverlay, options), query));
      }
      sources.Merge.ActiveSessions.ShouldBe(0);
   }

   // ---------------------------------------------------------------- scheduling

   [Fact]
   public async Task ChainsOfDependenciesWithOneFetchAtATime()
   {
      await using TestSources sources = await ShopAsync();
      QueryEngine engine = sources.Engine(ShopOverlay, new QueryEngineOptions { MaxParallelFetches = 1, BindJoins = BindJoinMode.Always, MaxBindBatch = 1 });
      const string query = "sales.orders.where(total > crm.calendar.count()).select(id, who: customer.name, p: product.name, h: day.holiday, c: customer.orders.count()).orderBy(id).take(5)";
      using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
      timeout.CancelAfter(TimeSpan.FromSeconds(30));
      Task<string> run = RowsAsync(engine, query);
      (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30), Token))).ShouldBe(run, "hangs");
      string rows = await run;
      rows.ShouldBe(await RowsAsync(sources.Engine(ShopOverlay, Never), query));
      sources.Merge.ActiveSessions.ShouldBe(0);
   }

   [Fact]
   public async Task AValueThatFailsStopsTheFragmentsWaitingForIt()
   {
      await using TestSources sources = await ShopAsync();
      QueryEngine engine = sources.Engine(ShopOverlay, new QueryEngineOptions { MaxParallelFetches = 1 });
      PreparedQuery prepared = engine.Prepare("sales.orders.where(total > crm.calendar.count()).select(id, who: customer.name)");
      await sources.RunAsync("crm", "DROP TABLE calendar");
      QueryExecutionException error = await Should.ThrowAsync<QueryExecutionException>(() => prepared.ExecuteAsync(Token));
      error.Message.ShouldContain("crm");
      sources.Merge.ActiveSessions.ShouldBe(0);
   }

   [Fact]
   public async Task ADriverThatFailsStopsItsTarget()
   {
      await using TestSources sources = await ShopAsync();
      QueryEngine engine = sources.Engine(ShopOverlay, new QueryEngineOptions { MaxParallelFetches = 1, BindJoins = BindJoinMode.Always });
      PreparedQuery prepared = engine.Prepare("sales.orders.select(id, who: customer.name)");
      await sources.RunAsync("sales", "DROP TABLE orders");
      QueryExecutionException error = await Should.ThrowAsync<QueryExecutionException>(() => prepared.ExecuteAsync(Token));
      error.Message.ShouldStartWith("sales (SQLite) failed");
      sources.Merge.ActiveSessions.ShouldBe(0);
   }

   [Fact]
   public async Task CancelledWhileWaiting()
   {
      await using TestSources sources = await ShopAsync();
      QueryEngine engine = sources.Engine(ShopOverlay, new QueryEngineOptions { MaxParallelFetches = 1, BindJoins = BindJoinMode.Always });
      using CancellationTokenSource cancel = new();
      cancel.Cancel();
      await Should.ThrowAsync<OperationCanceledException>(() => engine.ExecuteAsync(new QueryRequest("sales.orders.select(id, who: customer.name)"), cancel.Token));
      sources.Merge.ActiveSessions.ShouldBe(0);
   }

   // ---------------------------------------------------------------- top-N and subquery pushdown, against one database

   [Theory]
   [InlineData("shop.orders.select(id, who: customer.name).orderBy(id).skip(1).take(2)")]
   [InlineData("shop.orders.where(customer.city == 'Cape Town').select(id, who: customer.name).orderBy(id).take(1)")]
   [InlineData("shop.orders.where(customer.city != null).orderBy(desc(total)).take(2).select(id, who: customer.name)")]
   [InlineData("shop.order_lines.select(order_id, line_no, c: order.customer.city).orderBy(order_id, line_no).skip(1).take(2)")]
   [InlineData("shop.customers.select(name, n: orders.count()).orderBy(name).skip(1).take(1)")]
   [InlineData("shop.orders.select(id, c: ship_address.customer.name).orderBy(id).take(3)")]
   [InlineData("shop.orders.where(ship_address.city != null).select(id, c: ship_address.customer.name).orderBy(id).take(3)")]
   [InlineData("shop.orders.where(customer.orders.count() > 1).select(id).orderBy(id)")]
   [InlineData("shop.orders.where(customer.city == 'Cape Town' and customer.orders.count() > 1).select(id).orderBy(id)")]
   [InlineData("shop.orders.where(ship_address.city == null and customer.orders.any(o => o.total > 200)).select(id).orderBy(id)")]
   [InlineData("shop.orders.where(bill_address.line1 == shop.addresses.orderBy(id).first().line1).select(id).orderBy(id)")]
   [InlineData("shop.orders.where(customer.name == shop.customers.orderBy(id).first().name).select(id, a: bill_address.line1).orderBy(id)")]
   [InlineData("shop.orders.orderBy(total).select(id, who: customer.name).take(2)")]
   [InlineData("shop.addresses.select(line1, who: customer.name).orderBy(line1).take(1)")]
   [InlineData("shop.orders.select(id, who: customer.name, s: order_lines.sum(qty)).orderBy(desc(id)).skip(1).take(2)")]
   public async Task TopNAndSubqueriesMatchAcrossDatabases(string query)
   {
      await using TestSources sqlite = await TestSources.SqliteShopAsync();
      await using TestSources duckdb = await TestSources.DuckDbShopAsync();
      await using TestSources split = await Conformance.SplitShopAsync();
      string expected = await RowsAsync(sqlite.Engine(Conformance.Overlay), query);
      (await RowsAsync(duckdb.Engine(Conformance.Overlay), query)).ShouldBe(expected, "duckdb " + query);
      (await RowsAsync(split.Engine(Conformance.SplitOverlay, Always), Conformance.Split(query))).ShouldBe(expected, "split " + query);
      (await RowsAsync(split.Engine(Conformance.SplitOverlay, Never), Conformance.Split(query))).ShouldBe(expected, "split never " + query);
      (await RowsAsync(sqlite.Engine(Conformance.Overlay, new QueryEngineOptions { PushDown = false }), query)).ShouldBe(expected, "merge only " + query);
   }
}
