using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Planning;
using GalaxyData.Query.Planning.Optimizer;
using GalaxyData.Query.Tests.Binding;
using Shouldly;

namespace GalaxyData.Query.Tests.Planning;

/// <summary>Queries that read one source, planned against the sales test catalog; shared by the plan and SQL reports.</summary>
internal static class PlanCases
{
   public static readonly QueryCatalog Catalog = TestCatalogs.Sales(TestCatalogs.SalesOverlay(
      new OverlayVirtualEntity("reports.big_orders", "sales.orders.where(total > 100)"),
      new OverlayVirtualEntity("reports.customer_cities", "sales.customers.where(city != null).select(id, name, city: upper(city))") { Key = ["id"] }));

   public static readonly QueryParameters Parameters = new QueryParameters()
      .Add("status", "open")
      .Add("pageSize", 25L)
      .Add("since", "2026-01-01")
      .Add("rate", 0.15m)
      .Add("suffix", "Ltd")
      .Add("n", 3L);

   public static readonly string[] Queries =
   [
      "sales.orders.where(status == 'open').select(id, total, who: customer.name, city: customer.city)",
      "sales.orders",
      "sales.main.orders.where(o => o.total > 100 and o.customer.city == 'Cape Town')",
      "sales::orders.orderBy(desc(total), order_date).thenByDescending(id).skip(10).take($pageSize)",
      "sales.orders.where(status == $status and order_date >= $since)",
      "sales.orders.where(order_date >= '2026-01-01' and shipped_at < '2026-02-01 12:30')",
      "sales.orders.where(shipped_at == null or ship_customer == null).select(id, ship_to: ship_customer)",
      "sales.orders.where(ship_customer.city == 'Durban').select(id, ship_customer.name)",
      "sales.orders.where(status in ['open', 'shipped'] and not (total < 10))",
      "sales.orders.where(id in [])",
      "sales.orders.select(order_id: id, customer.*)",
      "sales.orders.select(id, customer.name, total * 2, lower(status), 'Total Spend': total)",
      "sales.orders.extend(gross: total * (1 + $rate)).where(gross > 100).select(customer.name, gross)",
      "sales.orders.select(id, customer).where(customer.city != null).select(id, customer.name)",
      "sales.order_lines.select(line_no, region: order.customer.region.name)",
      "sales.customers.select(name, label: name + ' (' + (city ?? 'unknown') + ')', size: credit_limit > 1000 ? 'big' : 'small')",
      "sales.customers.select(n: name.upper().trim(), y: year(created), d: addDays(created, 30), c: coalesce(city, name, 'x'))",
      "sales.order_lines.select(order_id, line_no, amount: qty * price, share: qty / 3, r: round(price, 1), p: product.name)",
      "sales.customers.where(between(credit_limit, 100, 5000.5) and like(name, 'A%'))",
      "sales.customers.where(startsWith(name, 'A_c') and endsWith(name, $suffix) and contains(name, city) and icontains(name, 'ME'))",
      "sales.customers.select(flag: credit_limit > 100, n: not (credit_limit > 100), t: toString(credit_limit > 100))",
      "sales.customers.where((credit_limit > 100) == (city != null))",
      "sales.orders.select(id, d: daysBetween(order_date, shipped_at), m: addMonths(order_date, 1), h: hour(shipped_at), at: now(), on: today())",
      "sales.order_lines.select(a: toInt(price), b: toDecimal(qty, 10, 2), c: toString(price), d: toDouble(qty), e: substring(product_code, 2, 3), f: indexOf(product_code, '-'), g: left(product_code, 2), h: right(product_code, 3))",
      "sales.customers.select(region.name).distinct()",
      "sales.orders.orderBy(id).distinct()",
      "sales.orders.orderBy(total).take(10).where(status == 'open')",
      "sales.orders.take(5).select(id, who: customer.name)",
      "sales.orders.select(status).distinct().orderBy(status).take(3)",
      "sales.orders.skip(5)",
      "sales.orders.take(5).take(3).skip(1)",
      "sales.orders.take(3).skip(5)",
      "sales.orders.select(status).distinct().skip(1)",
      "sales.orders.select(id, x: 'k', f: false).orderBy(x, f, desc(id))",
      "sales.orders.select(q: total / customer.credit_limit, r: total % 7, s: id % 7, t: id / 2)",
      "sales.orders.where(order_date <= shipped_at).select(id)",
      "sales.customers.select(d: addDays(created, $n), l: left(name, $n), n: length(name))",
      "sales.orders.groupBy(customer_id).select(customer_id: key, spend: sum(total), orders: count())",
      "sales.orders.join(sales.customers, outer.customer_id == inner.id, orders: outer, cust: inner).groupBy(cust.name).select(name: cust.name, count: orders.count(), total: orders.sum(total))",
      "x := sales.orders.where(total > 3); sales.customers.where(cust => x.any(order => cust.id == order.customer_id))",
      "sales.orders.groupBy(y: year(order_date), status).where(count() > 1).orderBy(desc(sum(total))).select(y, status, n: count(), big: any(total > 100), mean: avg(total))",
      "sales.orders.groupBy(customer).select(customer.name, customer.city, n: count())",
      "sales.orders.groupBy(customer).select(customer.id, n: count())",
      "sales.orders.groupBy(customer_id).extend(n: count()).where(n > 1 and sum(total) > 10).select(customer_id, n)",
      "sales.order_lines.groupBy(order).select(order.status, n: count(), qty: qty.sum(), priced: price.countDistinct(), all: all(qty > 0))",
      "sales.orders.groupBy().select(n: count(), total: sum(total))",
      "sales.orders.groupBy(big: total > 100).select(big, n: count())",
      "sales.customers.select(name, n: orders_by_customer.count(), spend: orders_by_customer.sum(total), last: orders_by_customer.max(order_date))",
      "sales.customers.where(orders_by_customer.any(status == 'open')).select(name)",
      "sales.customers.where(not orders_by_customer.any()).select(name)",
      "sales.customers.where(c => sales.orders.all(o => o.customer_id != c.id or o.total > 0))",
      "sales.orders.where(customer_id in sales.customers.where(city == 'Cape Town').select(id))",
      "sales.orders.where(total > sales.orders.avg(total))",
      "sales.orders.count()",
      "sales.customers.select(name, big: orders_by_customer.any(total > 100))",
      "sales.orders.leftJoin(sales.regions, (o, r) => o.customer.region_code == r.code, id: outer.id, region: inner.name)",
      "sales.orders.join(sales.order_lines, outer.id == inner.order_id and inner.qty > 1, id: outer.id, line: inner.line_no, product: inner.product.name)",
      "sales.customers.selectMany(orders_by_customer).select(id, total)",
      "sales.customers.selectMany(orders_by_customer, who: outer.name, total: inner.total)",
      "sales.orders.select(id, status).union(sales.orders.where(total > 100).select(status, id))",
      "sales.orders.where(total > 100).concat(sales.orders.where(status == 'open')).select(id, customer.name)",
      "sales.orders.select(customer_id).intersect(sales.customers.select(customer_id: id)).orderBy(customer_id).take(5)",
      "sales.orders.select(customer_id).except(sales.customers.where(city == null).select(customer_id: id))",
      "sales.customers.groupBy(city).select(city, s: sum(orders_by_customer.count()), big: any(orders_by_customer.any(total > 100)))",
      "sales.orders.take(2).count()",
      "sales.customers.extend(k: $n).select(name, l: left(name, k), d: addDays(created, k))",
      "xl['Budget 2024']['Sheet 1'].where(Amount > 0).select(it['Line Item'], Amount)",
      "big := sales.orders.where(total > 100); big.select(id, total)",
      "limit := 1000; cutoff := $rate * 2; sales.customers.where(credit_limit > limit).select(name, cutoff)",
      "reports.big_orders.where(status == 'open').select(id, who: customer.name)",
      "reports.customer_cities.orderBy(city)",
      "1 + 2 * 3",
      "sales.orders.where(total > 3.5 and total < 1e3 and id > -1)",
      "sales.orders.orderBy(desc(total)).first()",
      "sales.orders.firstOrDefault(status == 'open').customer",
      "sales.customers.select(name, latest: orders_by_customer.orderBy(desc(order_date)).first())",
      "sales.customers.select(name, last_total: orders_by_customer.orderBy(desc(order_date)).firstOrDefault().total, last_city: orders_by_customer.orderBy(desc(order_date)).first().ship_customer.city)",
      "sales.customers.where(orders_by_customer.orderBy(desc(order_date)).firstOrDefault() == null).select(name)",
      "sales.customers.select(name, lines: orders_by_customer.orderBy(id).first().order_lines.count())",
      "sales.orders.select(id, c: customer).select(id, c)",
      "g := sales.orders.where(total > 3); g.groupBy(o => o.customer_id).select(k: key, n: count())",
      "sales.orders.groupBy(customer, big: total > 100).select(customer, big, n: count(), s: sum(total))",
      "sales.order_lines.select(order_id, qty, product)",
      "sales.orders.select(id, status).distinct()",
      "sales.customers.select(name, t: orders_by_customer.orderBy(id).first().total).groupBy(name).select(name, s: sum(t))",
   ];

   /// <summary>The plan as lowered, before the optimizer.</summary>
   public static LogicalPlan Lowered(string query)
   {
      BoundProgram program = Binder.Bind(query, Catalog, Parameters);
      program.Diagnostics.ShouldNotContain(d => d.IsError, query);
      return Lowerer.Lower(program);
   }

   /// <summary>The plan the engine writes SQL for.</summary>
   public static LogicalPlan Plan(string query) => PlanOptimizer.Optimize(Lowered(query));
}
