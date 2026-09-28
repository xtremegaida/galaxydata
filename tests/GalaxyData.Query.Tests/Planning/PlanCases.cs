using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Planning;
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
      "xl['Budget 2024']['Sheet 1'].where(Amount > 0).select(it['Line Item'], Amount)",
      "big := sales.orders.where(total > 100); big.select(id, total)",
      "limit := 1000; cutoff := $rate * 2; sales.customers.where(credit_limit > limit).select(name, cutoff)",
      "reports.big_orders.where(status == 'open').select(id, who: customer.name)",
      "reports.customer_cities.orderBy(city)",
      "1 + 2 * 3",
      "sales.orders.where(total > 3.5 and total < 1e3 and id > -1)",
   ];

   public static LogicalPlan Plan(string query)
   {
      BoundProgram program = Binder.Bind(query, Catalog, Parameters);
      program.Diagnostics.ShouldNotContain(d => d.IsError, query);
      return Lowerer.Lower(program);
   }
}
