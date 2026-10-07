using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Execution;

/// <summary>
/// The conformance set: queries over the shop fixture that every source must answer alike, in one database, split
/// across sources, and however the engine runs them. The container tests run it on PostgreSQL and SQL Server too.
/// </summary>
internal static partial class Conformance
{
   internal const string ExampleOne = "shop.orders.where(status == 'open').select(id, total, who: customer.name, city: customer.city)";
   internal const string ExampleTwo = "shop.orders.groupBy(customer_id).select(customer_id: key, spend: sum(total), orders: count())";
   internal const string ExampleThree =
      "shop.orders.join(shop.customers, outer.customer_id == inner.id, orders: outer, cust: inner).groupBy(cust.name).select(name: cust.name, count: orders.count(), total: orders.sum(total))";
   internal const string ExampleFour = "x := shop.orders.where(total > 3); shop.customers.where(cust => x.any(order => cust.id == order.customer_id))";

   /// <summary>Queries over the columns both shop fixtures share; SQLite and DuckDB must return the same rows.</summary>
   internal static readonly string[] Queries =
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
      "shop.customers.select(name, c: concat(name, ': ', credit_limit > 1000, '/', city == null)).orderBy(name)",
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
      "shop.orders.where(placed_at != null).select(id, placed_at, h: hour(placed_at), d: date(placed_at), day: day(placed_at), at: toDateTime(placed_at), days: daysBetween(placed_at, toDate('2026-03-01'))).orderBy(id)",
      "shop.orders.where(placed_at > toDateTime('2026-01-10 00:00') or placed_at < toDateTime('2026-01-05 07:00')).select(id).orderBy(id)",
      "shop.orders.select(id, w: startOfWeek(order_date), m: startOfMonth(order_date), q: startOfQuarter(order_date), y: startOfYear(order_date), qn: quarter(order_date), wd: dayOfWeek(order_date)).orderBy(id)",
      "shop.orders.where(placed_at != null).select(id, placed_at, w: startOfWeek(placed_at), m: startOfMonth(placed_at), q: startOfQuarter(placed_at), y: startOfYear(placed_at), qn: quarter(placed_at), wd: dayOfWeek(placed_at)).orderBy(id)",
      "shop.orders.groupBy(m: startOfMonth(order_date)).select(m, n: count(), total: sum(total)).orderBy(m)",
      "shop.orders.where(placed_at != null).groupBy(w: startOfWeek(placed_at), d: dayOfWeek(placed_at)).select(w, d, n: count()).orderBy(w, d)",
      "shop.orders.orderBy(desc(placed_at)).select(id, placed_at)",
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
      "shop.orders.groupBy(status).select(status, some: customer_id.any(), big: total.any(it > 100), paid: total.all(it > 1), n: total.count(it > 10)).orderBy(status)",
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
      "shop.customers.select(name, s: orders.sum(credit_limit), n: orders.count(credit_limit), m: orders.max(credit_limit + total)).orderBy(name)",
      "shop.orders.groupBy(customer_id).extend(n: count()).select(customer_id, m: shop.customers.where(c => c.credit_limit > n).count()).orderBy(customer_id)",
      "shop.orders.count(ship_address)",
      "shop.customers.select(name, n: orders.count(ship_address)).orderBy(name)",
      "shop.customers.groupBy(city).select(city, s: sum(orders.count())).orderBy(city)",
      "shop.customers.extend(k: $n).select(name, l: left(name, k), d: addDays(toDate('2026-01-01'), k)).orderBy(name)",
      "1 + 2 * 3",
      "shop.orders.select(id, total, status).orderBy(desc(total)).first()",
      "shop.orders.where(status == 'open').orderBy(order_date).firstOrDefault().customer.name",
      "shop.customers.select(name, latest: orders.orderBy(desc(order_date)).first(), last_total: orders.orderBy(desc(order_date)).firstOrDefault().total).orderBy(name)",
      "shop.customers.where(orders.orderBy(order_date).firstOrDefault(status == 'open') == null).select(name).orderBy(name)",
      "shop.customers.select(name, lines: orders.orderBy(id).first().order_lines.count(), city: orders.orderBy(id).first().ship_address.city).orderBy(name)",
      "shop.orders.select(id, c: customer, a: ship_address).orderBy(id)",
      "shop.order_lines.select(qty, price).orderBy(qty, price)",
      "shop.orders.groupBy(customer).select(customer, n: count()).orderBy(customer.name)",
      "shop.orders.selectMany(o => shop.customers).count()",
   ];

   internal static readonly QueryParameters Parameters = new QueryParameters().Add("min", 50L).Add("since", "2026-01-06").Add("n", 3L);

   /// <summary>A virtual entity with computed columns, reached through an outer join.</summary>
   internal static readonly CatalogOverlay Overlay = new()
   {
      VirtualEntities =
      [
         new OverlayVirtualEntity("reports.addr_x", "shop.addresses.select(id, line1, tag: coalesce(city, 'none'), known: city != null, label: 'addr')") { Key = ["id"] },
      ],
      Relations = [new OverlayRelation("shop.orders", ["ship_address_id"], "reports.addr_x", ["id"]) { Name = "shipx" }],
   };

   /// <summary>
   /// The conformance queries' rows as a report; <paramref name="rewrite"/> changes each query's text for the sources
   /// (the report shows the query as written).
   /// </summary>
   internal static async Task<string> RunAllAsync(TestSources sources, CatalogOverlay? overlay = null, QueryEngineOptions? options = null, Func<string, string>? rewrite = null,
                                                  IMergeEngine? merge = null)
   {
      QueryEngine engine = sources.Engine(overlay ?? Overlay, options, merge);
      StringBuilder report = new();
      foreach (string query in Queries)
      {
         await using QueryResult result = await engine.ExecuteAsync(new QueryRequest(rewrite?.Invoke(query) ?? query) { Parameters = Parameters }, TestContext.Current.CancellationToken);
         IReadOnlyList<object?[]> rows = await result.ToListAsync(TestContext.Current.CancellationToken);
         report.Append("### ").AppendLine(query).AppendLine(TestSources.Header(result.Schema)).AppendLine(TestSources.Format(rows, result.Schema, hidden: true));
      }
      return report.ToString();
   }

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
}
