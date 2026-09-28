using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Results;
using GalaxyData.Testing;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Execution;

public sealed class ExecutionTests
{
   private const string ExampleOne = "shop.orders.where(status == 'open').select(id, total, who: customer.name, city: customer.city)";
   private const string ExampleTwo = "shop.orders.groupBy(customer_id).select(customer_id: key, spend: sum(total), orders: count())";
   private const string ExampleThree =
      "shop.orders.join(shop.customers, outer.customer_id == inner.id, orders: outer, cust: inner).groupBy(cust.name).select(name: cust.name, count: orders.count(), total: orders.sum(total))";
   private const string ExampleFour = "x := shop.orders.where(total > 3); shop.customers.where(cust => x.any(order => cust.id == order.customer_id))";

   /// <summary>Queries over the columns both shop fixtures share; SQLite and DuckDB must return the same rows.</summary>
   private static readonly string[] Conformance =
   [
      ExampleOne + ".orderBy(id)",
      "shop.orders.orderBy(desc(total)).take(2).select(id, total)",
      "shop.orders.orderBy(id).skip(1).take(2).select(id, status)",
      "shop.orders.orderBy(total).take(3).where(status == 'open').select(id, total)",
      "shop.customers.orderBy(city).select(name, city)",
      "shop.customers.orderBy(desc(city)).select(name, city)",
      "shop.orders.where(ship_address == null).select(id)",
      "shop.orders.select(id, ship: ship_address.city, bill: bill_address.line1).orderBy(id)",
      "shop.orders.where(customer.city == 'Cape Town').select(id, customer.name).orderBy(id)",
      "shop.order_lines.select(order_id, line_no, city: order.customer.city).orderBy(order_id, line_no)",
      "shop.order_lines.select(order_id, line_no, amount: qty * price, share: qty / 2, r: round(price, 1)).orderBy(order_id, line_no)",
      "shop.customers.select(name, label: name + ' (' + (city ?? 'unknown') + ')', size: credit_limit > 1000 ? 'big' : 'small').orderBy(name)",
      "shop.customers.select(n: upper(name), l: length(name), f: left(name, 3), r: right(name, 3), s: substring(name, 2, 3), i: indexOf(name, ' ')).orderBy(n)",
      "shop.customers.where(startsWith(name, 'Ac') or endsWith(name, 'Inc') or contains(name, 'eta')).select(name).orderBy(name)",
      "shop.customers.where(startsWith(name, 'ac') or contains(name, 'ETA')).select(name)",
      "shop.customers.where(icontains(name, 'CORP')).select(name)",
      "shop.customers.where(like(name, '%Ltd')).select(name)",
      "shop.orders.select(id, y: year(order_date), m: month(order_date), d: day(order_date), plus: addDays(order_date, 30), next: addMonths(order_date, 1), days: daysBetween(order_date, toDate('2026-03-01'))).orderBy(id)",
      "shop.orders.select(id, t: toString(total), i: toInt(total), f: toDouble(total), b: toBool(total), big: total > 100).orderBy(id)",
      "shop.orders.select(status).distinct().orderBy(status)",
      "shop.orders.where(status in ['open', 'shipped'] and not (total < 50)).select(id).orderBy(id)",
      "shop.orders.where(total > $min and order_date >= $since).select(id).orderBy(id)",
      "shop.customers.where(credit_limit == null).select(name)",
      "shop.addresses.select(line1, who: customer.name).orderBy(line1)",
      "big := shop.orders.where(total > 50); big.select(id, total).orderBy(id)",
      "shop.open_orders.orderBy(id)",
      "shop.orders.select(id, tag: shipx.tag, known: shipx.known, label: shipx.label).orderBy(id)",
      "shop.orders.where(shipx == null).select(id)",
      "shop.orders.where(shipx.label == 'addr').select(id).orderBy(id)",
      "shop.orders.select(id, q: total / customer.credit_limit, r: total % 7).orderBy(id)",
      "shop.orders.select(id, x: 'k', f: false).orderBy(x, f, desc(id)).select(id)",
      "shop.orders.select(id, same: order_date == toDateTime('2026-01-05 00:00'), before: order_date < toDateTime('2026-01-09 12:00')).orderBy(id)",
      "shop.customers.select(name, l: left(name, $n), d: addDays(toDate('2026-01-01'), $n)).orderBy(name)",
      ExampleTwo + ".orderBy(customer_id)",
      ExampleThree + ".orderBy(name)",
      ExampleFour + ".select(name).orderBy(name)",
      "shop.customers.select(name, n: addresses.count(), orders: orders.count(), spend: orders.sum(total), last: orders.max(order_date)).orderBy(name)",
      "shop.customers.where(not orders.any(status == 'open')).select(name)",
      "shop.customers.where(c => shop.orders.all(o => o.customer_id != c.id or o.total > 50)).select(name).orderBy(name)",
      "shop.customers.select(name, big: orders.any(total > 100)).orderBy(name)",
      "shop.orders.where(customer_id in shop.customers.where(city == 'Cape Town').select(id)).select(id).orderBy(id)",
      "shop.orders.where(total > shop.orders.avg(total)).select(id).orderBy(id)",
      "shop.orders.count()",
      "shop.orders.groupBy(status).where(count() > 1).select(status, n: count(), mean: avg(total), big: any(total > 100), all: all(total > 10)).orderBy(status)",
      "shop.orders.groupBy(customer).select(customer.name, n: count(), total: sum(total)).orderBy(name)",
      "shop.orders.groupBy(y: year(order_date), m: month(order_date)).select(y, m, n: count()).orderBy(y, m)",
      "shop.order_lines.groupBy(order).select(id: order.id, order.status, lines: count(), qty: qty.sum()).orderBy(id)",
      "shop.orders.groupBy().select(n: count(), total: sum(total), low: min(total), high: max(total))",
      "shop.orders.leftJoin(shop.addresses, outer.bill_address_id == inner.id, o: outer, a: inner).select(o.id, line: a.line1).orderBy(id)",
      "shop.customers.selectMany(orders, who: outer.name, total: inner.total).orderBy(who, total)",
      "shop.orders.select(id, status).union(shop.orders.where(total > 50).select(status, id)).orderBy(id)",
      "shop.orders.where(total > 50).concat(shop.orders.where(status == 'open')).select(id).orderBy(id)",
      "shop.orders.select(customer_id).intersect(shop.addresses.select(customer_id)).orderBy(customer_id)",
      "shop.orders.select(customer_id).except(shop.addresses.select(customer_id))",
      "shop.customers.select(name, n: addresses.countDistinct(city)).orderBy(name)",
      "shop.customers.where(addresses.countDistinct(city) == 0).select(name)",
      "shop.orders.take(2).count()",
      "shop.orders.skip(1).count()",
      "shop.orders.select(id, total).concat(shop.orders.select(id, total)).count()",
      "shop.customers.where(shop.orders.concat(shop.orders).any()).select(name).orderBy(name)",
      "shop.orders.where(total > 100000).groupBy().select(n: count()).count()",
      "shop.customers.where(shop.orders.where(total > 100000).groupBy().select(n: count()).any()).select(name).orderBy(name)",
      "shop.orders.leftJoin(shop.customers, outer.customer_id == inner.id and inner.city == 'Cape Town', o: outer, c: inner).where(between(o.total, c.credit_limit, 10) == false).select(o.id).orderBy(id)",
      "shop.customers.select(name, s: orders.sum(total * credit_limit)).orderBy(name)",
      "shop.orders.groupBy(customer_id).extend(n: count()).select(customer_id, m: shop.customers.where(c => c.credit_limit > n).count()).orderBy(customer_id)",
      "shop.orders.count(ship_address)",
      "shop.customers.select(name, n: orders.count(ship_address)).orderBy(name)",
      "shop.customers.groupBy(city).select(city, s: sum(orders.count())).orderBy(city)",
      "shop.customers.extend(k: $n).select(name, l: left(name, k), d: addDays(toDate('2026-01-01'), k)).orderBy(name)",
      "1 + 2 * 3",
   ];

   private static readonly QueryParameters Parameters = new QueryParameters().Add("min", 50L).Add("since", "2026-01-06").Add("n", 3L);

   /// <summary>A virtual entity with computed columns, reached through an outer join.</summary>
   private static readonly CatalogOverlay Overlay = new()
   {
      VirtualEntities =
      [
         new OverlayVirtualEntity("reports.addr_x", "shop.addresses.select(id, line1, tag: coalesce(city, 'none'), known: city != null, label: 'addr')") { Key = ["id"] },
      ],
      Relations = [new OverlayRelation("shop.orders", ["ship_address_id"], "reports.addr_x", ["id"]) { Name = "shipx" }],
   };

   private static async Task<string> RunAllAsync(TestSources sources)
   {
      QueryEngine engine = sources.Engine(Overlay);
      StringBuilder report = new();
      foreach (string query in Conformance)
      {
         await using QueryResult result = await engine.ExecuteAsync(new QueryRequest(query) { Parameters = Parameters }, TestContext.Current.CancellationToken);
         IReadOnlyList<object?[]> rows = await result.ToListAsync(TestContext.Current.CancellationToken);
         report.Append("### ").AppendLine(query).AppendLine(TestSources.Header(result.Schema)).AppendLine(TestSources.Format(rows));
      }
      return report.ToString();
   }

   [Fact]
   public async Task SqliteAndDuckDbReturnTheSameRows()
   {
      await using TestSources sqlite = await TestSources.SqliteShopAsync();
      await using TestSources duckdb = await TestSources.DuckDbShopAsync();
      string fromSqlite = await RunAllAsync(sqlite);
      string fromDuckDb = await RunAllAsync(duckdb);
      fromDuckDb.ShouldBe(fromSqlite);
      Golden.Match(fromSqlite);
   }

   [Theory]
   [InlineData("sqlite")]
   [InlineData("duckdb")]
   public async Task ExampleOneRunsEndToEnd(string provider)
   {
      await using TestSources sources = provider == "sqlite" ? await TestSources.SqliteShopAsync() : await TestSources.DuckDbShopAsync();
      PreparedQuery prepared = sources.Engine().Prepare(ExampleOne + ".orderBy(id)");
      prepared.Success.ShouldBeTrue();
      prepared.Fragments.ShouldHaveSingleItem().Source.Alias.ShouldBe("shop");

      await using QueryResult result = await prepared.ExecuteAsync(TestContext.Current.CancellationToken);
      result.Schema.Columns.Select(c => c.Name).ShouldBe(["id", "total", "who", "city"]);
      ResultColumn who = result.Schema.Columns[2];
      who.Lineage.Kind.ShouldBe(LineageKind.Direct);
      who.Lineage.NavigationPath.ShouldBe("customer");
      who.Lineage.Sources.ShouldHaveSingleItem().Column.ToString().ShouldBe("shop.customers.name");

      IReadOnlyList<object?[]> rows = await result.ToListAsync(TestContext.Current.CancellationToken);
      TestSources.Format(rows).ShouldBe(
         "1001 | 250.00 | 'Acme Ltd' | 'Cape Town'" + Environment.NewLine +
         "1003 | 12.25 | 'Beta Corp' | 'Johannesburg'" + Environment.NewLine);
      result.Stats.Rows.ShouldBe(2);
      sources.Opened.ShouldBe(1);
   }

   [Fact]
   public async Task PagingAppliesAfterTheQuery()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      QueryRequest request = new("shop.orders.orderBy(id).select(id)") { Paging = new PageRequest(1, 2) };
      await using QueryResult result = await sources.Engine().ExecuteAsync(request, TestContext.Current.CancellationToken);
      TestSources.Format(await result.ToListAsync(TestContext.Current.CancellationToken)).ShouldBe($"1002{Environment.NewLine}1003{Environment.NewLine}");
   }

   [Fact]
   public async Task NowIsFixedWhenTheQueryStarts()
   {
      await using TestSources sources = await TestSources.DuckDbShopAsync();
      FixedClock clock = new(new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.Zero));
      QueryEngine engine = sources.Engine(options: new QueryEngineOptions { Clock = clock });
      await using QueryResult result = await engine.ExecuteAsync(
         new QueryRequest("shop.orders.where(order_date < today()).select(id, at: now(), age: daysBetween(order_date, today())).take(1)"),
         TestContext.Current.CancellationToken);
      TestSources.Format(await result.ToListAsync(TestContext.Current.CancellationToken)).ShouldBe($"1001 | 2026-03-01 09:30:00 | 55{Environment.NewLine}");
   }

   [Fact]
   public async Task ParametersThatDontConvertFailWhenTheQueryRuns()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      PreparedQuery prepared = sources.Engine().Prepare("shop.orders.where(order_date >= $since)", new QueryParameters().Add("since", "soon"));
      prepared.Success.ShouldBeTrue();
      QueryExecutionException error = await Should.ThrowAsync<QueryExecutionException>(() => prepared.ExecuteAsync(TestContext.Current.CancellationToken));
      error.Message.ShouldContain("$since can't be used as a date");
   }

   [Fact]
   public async Task ValuesThatDontConvertNameTheirRowAndColumn()
   {
      const string script = "CREATE TABLE events (id INTEGER PRIMARY KEY, happened DATE); INSERT INTO events VALUES (1, '2026-01-05'), (2, 'soon');";
      await using TestSources sources = await new TestSources().AddSqliteAsync("log", script);
      await using (QueryResult strict = await sources.Engine().ExecuteAsync(new QueryRequest("log.events.orderBy(id)"), TestContext.Current.CancellationToken))
      {
         QueryExecutionException error = await Should.ThrowAsync<QueryExecutionException>(() => strict.ToListAsync(TestContext.Current.CancellationToken));
         error.Message.ShouldStartWith("Row 2, column 'happened'");
      }
      QueryEngine lenient = sources.Engine(options: new QueryEngineOptions { LenientConversion = true });
      await using QueryResult result = await lenient.ExecuteAsync(new QueryRequest("log.events.orderBy(id)"), TestContext.Current.CancellationToken);
      TestSources.Format(await result.ToListAsync(TestContext.Current.CancellationToken)).ShouldBe($"1 | 2026-01-05{Environment.NewLine}2 | null{Environment.NewLine}");
   }

   [Fact]
   public async Task QueriesAcrossSourcesAreReportedNotRun()
   {
      await using TestSources sources = new();
      await sources.AddSqliteAsync("shop", Fixtures.Sql("shop.sqlite.sql"));
      await sources.AddDuckDbAsync("wh", Fixtures.Sql("shop.duckdb.sql"));
      CatalogOverlay overlay = new() { Relations = [new OverlayRelation("wh.orders", ["customer_id"], "shop.customers", ["id"]) { Name = "shop_customer" }] };
      PreparedQuery prepared = sources.Engine(overlay).Prepare("wh.orders.select(id, who: shop_customer.name)");
      prepared.Success.ShouldBeFalse();
      QueryDiagnostic error = prepared.Diagnostics.ShouldHaveSingleItem();
      error.Code.ShouldBe(DiagnosticCodes.CrossSourceQuery);
      error.Message.ShouldContain("wh and shop");
      await Should.ThrowAsync<QueryException>(() => prepared.ExecuteAsync(TestContext.Current.CancellationToken));
      sources.Opened.ShouldBe(0);
   }

   [Theory]
   [InlineData("sqlite", "CREATE TABLE blobs (id INTEGER PRIMARY KEY, data BLOB); INSERT INTO blobs VALUES (1, X'AABB'), (2, NULL);")]
   [InlineData("duckdb", "CREATE TABLE blobs (id INTEGER PRIMARY KEY, data BLOB); INSERT INTO blobs VALUES (1, '\\xAA\\xBB'::BLOB), (2, NULL);")]
   public async Task BinaryValuesAreBytes(string provider, string script)
   {
      await using TestSources sources = provider == "sqlite" ? await new TestSources().AddSqliteAsync("b", script) : await new TestSources().AddDuckDbAsync("b", script);
      await using QueryResult result = await sources.Engine().ExecuteAsync(new QueryRequest("b.blobs.orderBy(id)"), TestContext.Current.CancellationToken);
      IReadOnlyList<object?[]> rows = await result.ToListAsync(TestContext.Current.CancellationToken);
      rows[0][1].ShouldBe(new byte[] { 0xAA, 0xBB });
      rows[1][1].ShouldBeNull();
   }

   [Fact]
   public async Task OffsetDateTimesCompareWithNowInUtcWhateverTheMachineZone()
   {
      const string script = "CREATE TABLE events (id INTEGER PRIMARY KEY, happened TIMESTAMPTZ); " +
                            "INSERT INTO events VALUES (1, TIMESTAMPTZ '2026-03-01 09:00:00+00'), (2, TIMESTAMPTZ '2026-03-01 10:00:00+00');";
      await using TestSources sources = await new TestSources().AddDuckDbAsync("log", script);
      QueryEngine engine = sources.Engine(options: new QueryEngineOptions { Clock = new FixedClock(new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.Zero)) });
      await using QueryResult result = await engine.ExecuteAsync(
         new QueryRequest("log.events.select(id, past: happened < now(), h: hour(happened), n: now()).orderBy(id)"), TestContext.Current.CancellationToken);
      TestSources.Format(await result.ToListAsync(TestContext.Current.CancellationToken)).ShouldBe(
         "1 | true | 9 | 2026-03-01 09:30:00" + Environment.NewLine +
         "2 | false | 10 | 2026-03-01 09:30:00" + Environment.NewLine);
   }

   [Fact]
   public async Task CollectionsThatCantBeJoinedAreReported()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      PreparedQuery prepared = sources.Engine().Prepare("shop.customers.selectMany(c => shop.orders.where(o => o.customer_id == c.id).take(1)).select(id)");
      prepared.Success.ShouldBeFalse();
      prepared.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.NotTranslatable);
   }

   [Fact]
   public async Task CorrelatedSubqueriesRunWithoutTheOptimizerToo()
   {
      await using TestSources sources = await TestSources.DuckDbShopAsync();
      QueryEngine plain = sources.Engine(options: new QueryEngineOptions { Optimize = false });
      const string query = "shop.customers.select(name, n: orders.count(), spend: orders.sum(total), big: orders.any(total > 100)).orderBy(name)";
      await using QueryResult result = await plain.ExecuteAsync(new QueryRequest(query), TestContext.Current.CancellationToken);
      TestSources.Format(await result.ToListAsync(TestContext.Current.CancellationToken)).ShouldBe(
         "'Acme Ltd' | 2 | 349.50 | true" + Environment.NewLine +
         "'Beta Corp' | 1 | 12.25 | false" + Environment.NewLine +
         "'Gamma Inc' | 1 | 0.00 | false" + Environment.NewLine);
   }

   [Fact]
   public async Task BindErrorsComeBackAsDiagnostics()
   {
      await using TestSources sources = await TestSources.SqliteShopAsync();
      PreparedQuery prepared = sources.Engine().Prepare("shop.orders.where(statuss == 'open')");
      prepared.Success.ShouldBeFalse();
      prepared.Diagnostics.ShouldHaveSingleItem().Message.ShouldContain("Did you mean 'status'?");
      prepared.Fragments.ShouldBeEmpty();
   }

   private sealed class FixedClock(DateTimeOffset now) : TimeProvider
   {
      public override DateTimeOffset GetUtcNow() => now;
   }
}
