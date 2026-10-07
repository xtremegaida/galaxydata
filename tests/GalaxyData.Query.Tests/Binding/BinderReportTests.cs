using System;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Testing;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.Tests.Binding;

/// <summary>Golden reports: what queries bind to, and what the binder says about queries that don't bind.</summary>
public sealed class BinderReportTests
{
   private static readonly QueryCatalog Catalog = TestCatalogs.Sales();

   private static readonly QueryParameters Parameters = new QueryParameters()
      .Add("status", "open")
      .Add("pageSize", 25L)
      .Add("since", "2026-01-01")
      .Add("rate", 0.15m)
      .Add("nothing", null);

   private static string Bind(string query)
   {
      BoundProgram program = Binder.Bind(query, Catalog, Parameters);
      string diagnostics = QueryReport.Diagnostics(query, program.Diagnostics);
      return program.Success ? BoundTreePrinter.Print(program) + diagnostics : "FAILED" + Environment.NewLine + diagnostics;
   }

   [Fact]
   public void BoundTrees()
   {
      string[] queries =
      [
         "sales.orders.where(status == 'open').select(id, total, who: customer.name, city: customer.city)",
         "sales.main.orders.where(o => o.total > 100 and o.customer.city == 'Cape Town')",
         "sales::orders.orderBy(desc(total), order_date).thenByDescending(id).skip(10).take($pageSize)",
         "sales.orders.orderByDescending(total, asc(id))",
         "sales.orders.where(status == $status and order_date >= $since)",
         "sales.orders.where(order_date >= '2026-01-01' and shipped_at < '2026-02-01 12:30')",
         "sales.orders.where(shipped_at == null or ship_customer == null).select(id, ship_to: ship_customer)",
         "sales.orders.where(status in ['open', 'shipped'] and not (total < 10))",
         "sales.orders.where(id in [])",
         "sales.orders.select(order_id: id, customer.*)",
         "sales.orders.select(id, customer.name, total * 2, lower(status), 'Total Spend': total)",
         "sales.orders.extend(gross: total * (1 + $rate)).where(gross > 100).select(customer.name, gross)",
         "sales.orders.select(id, customer).where(customer.city != null).select(id, customer.name)",
         "sales.customers.select(name, label: name + ' (' + (city ?? 'unknown') + ')', size: credit_limit > 1000 ? 'big' : 'small')",
         "sales.customers.select(n: name.upper().trim(), y: year(created), d: addDays(created, 30), c: coalesce(city, name, 'x'))",
         "sales.order_lines.select(order_id, line_no, amount: qty * price, share: qty / 3, r: round(price, 1), p: product.name)",
         "sales.customers.where(between(credit_limit, 100, 5000.5) and like(name, 'A%'))",
         "sales.customers.select(region.name).distinct()",
         "crm.contacts.select(email, it['Display Name'], customer.name, region: customer.region.name)",
         "xl['Budget 2024']['Sheet 1'].where(Amount > 0).select(it['Line Item'], Amount)",
         "big := sales.orders.where(total > 100); big.select(id, total)",
         "limit := 1000; cutoff := $rate * 2; sales.customers.where(credit_limit > limit).select(name, cutoff)",
         "status := 5; sales.orders.where(status == 'open')",
         "1 + 2 * 3",
         "sales.orders.where(it.total > 0 and it['status'] != '')",
         "sales.orders.where(total > 3.5 and total < 1e3 and id > -1)",
         "sales.orders.groupBy(customer_id).select(customer_id: key, spend: sum(total), orders: count())",
         "sales.orders.join(sales.customers, outer.customer_id == inner.id, orders: outer, cust: inner).groupBy(cust.name).select(name: cust.name, count: orders.count(), total: orders.sum(total))",
         "x := sales.orders.where(total > 3); sales.customers.where(cust => x.any(order => cust.id == order.customer_id))",
         "sales.orders.groupBy(y: year(order_date), status).where(count() > 1).orderBy(desc(sum(total))).select(y, status, n: count(), big: any(total > 100), mean: avg(total))",
         "sales.orders.groupBy(customer).select(customer.name, customer.city, n: count())",
         "sales.orders.groupBy(year(order_date)).select(year(order_date), n: count(total > 10), total.max())",
         "sales.orders.groupBy(customer_id).extend(n: count()).where(n > 1).select(customer_id, n)",
         "sales.order_lines.groupBy(order).select(order.status, n: count(), qty: qty.sum(), priced: price.countDistinct())",
         "sales.orders.groupBy(g => g.status).where(g => g.count() > 1).select(status)",
         "sales.customers.select(name, n: orders_by_customer.count(), spend: orders_by_customer.sum(total), last: orders_by_customer.max(order_date))",
         "sales.customers.where(orders_by_customer.any(status == 'open')).select(name)",
         "sales.customers.where(c => sales.orders.all(o => o.customer_id != c.id or o.total > 0))",
         "sales.orders.where(customer_id in sales.customers.where(city == 'Cape Town').select(id))",
         "sales.orders.where(total > sales.orders.avg(total))",
         "sales.orders.count()",
         "sales.orders.leftJoin(sales.regions, (o, r) => o.customer.region_code == r.code, id: outer.id, region: inner.name)",
         "sales.customers.selectMany(orders_by_customer).select(id, total)",
         "sales.customers.selectMany(orders_by_customer, who: outer.name, total: inner.total)",
         "sales.orders.select(id, status).union(sales.orders.where(total > 100).select(status, id))",
         "sales.orders.where(total > 100).concat(sales.orders.where(status == 'open'))",
         "sales.orders.select(customer_id).intersect(sales.customers.select(customer_id: id))",
         "sales.orders.orderBy(desc(total)).first()",
         "sales.orders.firstOrDefault(status == 'open').customer",
         "sales.customers.select(name, latest: orders_by_customer.orderBy(desc(order_date)).first())",
         "sales.customers.where(c => c.orders_by_customer.orderBy(desc(order_date)).firstOrDefault(o => o.status == 'open').total > 100)",
         "sales.orders.groupBy(status).orderBy(desc(count())).first().status",
      ];
      QueryReport report = new();
      foreach (string query in queries) { report.Case(query, Bind(query)); }
      Golden.Match(report.ToString());
   }

   [Fact]
   public void Errors()
   {
      string[] queries =
      [
         "sales.ordrs",
         "sales",
         "sales.orders.where(statuss == 'open')",
         "sales.orders.where(o => total > 100)",
         "sales.orders.where(total)",
         "sales.orders.where(status = 'open')",
         "sales.orders.where(status == 3)",
         "sales.orders.where(order_date > '5 Jan 2026')",
         "sales.customers.where(uid < uid)",
         "sales.customers.where(notes == 'x')",
         "sales.customers.orderBy(uid)",
         "sales.orders.select(id, id)",
         "sales.orders.select(id, customer.*)",
         "sales.orders.extend(total: 1)",
         "sales.orders.where(customer == 3)",
         "sales.customers.select(orders)",
         "sales.customers.select(orders_by_customer)",
         "sales.customers.select(n: orders.count())",
         "sales.orders.wher(total > 1)",
         "sales.orders.count",
         "sales.orders.total",
         "sales.orders.take(-1)",
         "sales.orders.take(total)",
         "sales.orders.thenBy(id)",
         "sales.orders.orderBy(customer)",
         "sales.customers.select(n: name + 1)",
         "sales.orders.select(d: order_date + 1)",
         "sales.orders.select(x: lowr(status))",
         "sales.orders.select(x: substring(status))",
         "sales.orders.select(x: year(status))",
         "sales.orders.select(x: startOfMonth(status))",
         "sales.orders.select(x: status.foo)",
         "sales.orders.where(status == $missing)",
         "x := sales.orders",
         "sales.orders; sales.customers",
         "x := 1; x := 2; x",
         "if (true) 1 else 2",
         "sales.orders.select(x: {a: 1})",
         "sales.orders.where(total & 1 == 1)",
         "where(total > 3)",
         "sales.orders.select(o => o.id)",
         "sales.orders.where(total > 1).distinct(id)",
         "sales.orders.where(status in ['a', null])",
         "sales.orders.where(x := 1)",
         "sales.orders.groupBy(customer_id).select(total)",
         "sales.orders.groupBy(customer_id).select(customer.name)",
         "sales.orders.select(n: count())",
         "sales.customers.groupBy(notes)",
         "sales.orders.groupBy(customer_id).groupBy(key)",
         "sales.orders.groupBy(customer_id).select(m: max(attachment))",
         "sales.orders.groupBy(customer_id).select(m: sum(status))",
         "sales.orders.join(sales.customers, customer_id == id)",
         "sales.orders.join(sales.customers, (o) => o.customer_id == 1)",
         "sales.orders.join(outer.customer_id == 1)",
         "sales.orders.union(sales.customers)",
         "sales.orders.select(id).union(sales.orders.select(id, status))",
         "sales.orders.select(id, customer).union(sales.orders.select(id, customer))",
         "sales.orders.where(total in sales.customers)",
         "sales.customers.select(n: orders_by_customer.sum())",
         "sales.orders.first(status == 'open', total > 1)",
         "sales.orders.first().where(total > 1)",
         "sales.orders.first().totl",
         "sales.orders.groupBy(status).first().count()",
         "sales.orders.groupBy(status).select(status, f: total.first())",
         "sales.orders.groupBy(x: sales.customers.first())",
         "sales.orders.groupBy(customer.orders_by_customer.first().customer)",
         "sales.customers.selectMany(name)",
         "sales.orders.groupBy(status).select(status, x: sales.customers.where(c => c.credit_limit > count()).count())",
         "sales.orders.groupBy(k: customer_id).select(k, s: sum(k))",
         "sales.orders.groupBy(customer_id).extend(n: count()).select(m: max(n))",
         "sales.orders.groupBy(customer_id).select(m: max(count()))",
         "sales.orders.groupBy(status).select(status, x: total == null)",
      ];
      QueryReport report = new();
      foreach (string query in queries)
      {
         BoundProgram program = Binder.Bind(query, Catalog, Parameters);
         program.Success.ShouldBeFalse(query);
         report.Case(query, QueryReport.Diagnostics(query, program.Diagnostics));
      }
      Golden.Match(report.ToString());
   }
}
