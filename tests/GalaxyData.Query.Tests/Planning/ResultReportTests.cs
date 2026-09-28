using System.Linq;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Explain;
using GalaxyData.Query.Language;
using GalaxyData.Query.Planning;
using GalaxyData.Query.Planning.Optimizer;
using GalaxyData.Query.Results;
using GalaxyData.Testing;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.Tests.Planning;

/// <summary>
/// Golden reports on what a result carries besides its values: each column's lineage, edit target and link, the
/// hidden key columns those need, and the row identity; and the plans counting a query's rows.
/// </summary>
public sealed class ResultReportTests
{
   private static readonly string[] Queries =
   [
      // the four examples
      "sales.orders.where(status == 'open').select(id, total, who: customer.name, city: customer.city)",
      "sales.orders.groupBy(customer_id).select(customer_id: key, spend: sum(total), orders: count())",
      "sales.orders.join(sales.customers, outer.customer_id == inner.id, orders: outer, cust: inner).groupBy(cust.name).select(name: cust.name, count: orders.count(), total: orders.sum(total))",
      "x := sales.orders.where(total > 3); sales.customers.where(cust => x.any(order => cust.id == order.customer_id))",
      // entity rows: identity and related rows; foreign keys link to their rows
      "sales.orders",
      "sales.order_lines.where(qty > 1)",
      "sales.customers.extend(big: credit_limit > 1000)",
      // records shown by their display column link to their row
      "sales.orders.select(id, c: customer, ship_to: ship_customer)",
      "sales.orders.select(id, c: customer).select(id, c)",
      "sales.order_lines.select(line_no, region: order.customer.region.name, product)",
      // renamed columns still edit their row; keys come along hidden
      "sales.orders.select(order_id: id, amount: total, cid: customer_id)",
      "sales.order_lines.select(qty, price)",
      // aggregates of collections link to the collection
      "sales.customers.select(name, n: orders_by_customer.count(), any: orders_by_customer.any(), open: orders_by_customer.any(status == 'open'))",
      // grouped aggregates drill down to the group's rows
      "sales.orders.groupBy(customer).select(customer, n: count())",
      "g := sales.orders.where(total > 3); g.groupBy(o => o.customer_id, big: o => o.total > 100).select(k: key, n: count(), s: sum(total))",
      "sales.orders.groupBy(y: year(order_date)).where(count() > 1).select(y, n: count())",
      // what doesn't keep rows apart has no edit targets
      "sales.orders.select(status).distinct()",
      "sales.orders.select(id, status).union(sales.orders.select(id, status))",
      "sales.orders.groupBy(status).select(status, n: count())",
      "sales.orders.select(t: total * 2, s: upper(status))",
      // virtual entities: edits go to the table underneath
      "reports.customer_cities",
      "reports.big_orders.select(id, who: customer.name)",
      // first rows
      "sales.orders.orderBy(desc(total)).first()",
      "sales.orders.first().customer",
      "sales.customers.select(name, latest: orders_by_customer.orderBy(desc(order_date)).first(), last: orders_by_customer.orderBy(desc(order_date)).first().total)",
      // explicit joins and flattening keep both rows
      "sales.orders.join(sales.customers, outer.customer_id == inner.id, o: outer.total, c: inner.name)",
      "sales.customers.selectMany(orders_by_customer, who: outer.name, total: inner.total)",
   ];

   [Fact]
   public void Schemas()
   {
      QueryReport report = new();
      foreach (string query in Queries)
      {
         LogicalPlan plan = PlanCases.Lowered(query);
         report.Case(query, ExplainTextRenderer.RenderColumns(plan.Schema));
      }
      Golden.Match(report.ToString());
   }

   private static readonly string[] Counted =
   [
      "sales.orders.where(status == 'open').select(id, total, who: customer.name, city: customer.city).orderBy(who)",
      "sales.orders.select(id, ship_to: ship_customer.name).orderBy(ship_to)",
      "sales.orders.orderBy(total).take(3)",
      "sales.orders.groupBy(customer_id).select(customer_id: key, n: count())",
      "sales.customers.select(name, n: orders_by_customer.count())",
      "sales.orders.select(status).distinct()",
      "sales.orders.orderBy(desc(total)).first()",
      "sales.orders.count()",
   ];

   [Fact]
   public void Counts()
   {
      QueryReport report = new();
      foreach (string query in Counted)
      {
         BoundProgram program = Binder.Bind(query, PlanCases.Catalog, PlanCases.Parameters);
         LogicalPlan plan = PlanOptimizer.Optimize(Lowerer.LowerCount(program));
         report.Case(query, PlanPrinter.Print(plan.Root));
      }
      Golden.Match(report.ToString());
   }

   private static PlanNode Paged(string query)
   {
      BoundProgram program = Binder.Bind(query, PlanCases.Catalog, PlanCases.Parameters);
      return Lowerer.Lower(program, 20, 10).Root;
   }

   [Fact]
   public void PagesOfEntityRowsAreSortedByTheKeyAfterTheQuerysOwnSort()
   {
      string SortOf(PlanNode root) => string.Join(", ", ((SortNode)((LimitNode)root).Input).Keys.Select(k => PlanPrinter.Expr(k.Expr) + (k.Descending ? " desc" : "")));

      SortOf(Paged("sales.orders")).ShouldBe("id#1");
      SortOf(Paged("sales.orders.orderBy(desc(total))")).ShouldBe("total#4 desc, id#1");
      SortOf(Paged("sales.orders.orderBy(id, total)")).ShouldBe("id#1, total#4");
      SortOf(Paged("sales.order_lines.orderBy(qty)")).ShouldBe("qty#4, order_id#1, line_no#2");
      // The key goes into the sort under the filter (and the take), so the page is of the same rows in the same order.
      PlanPrinter.Print(Paged("sales.orders.orderBy(total).take(50).where(status == 'open')")).ShouldBe(
         """
         Limit skip 20 take 10
           Filter status#3 == 'open'
             Limit take 50
               Sort total#4, id#1
                 Scan sales.orders (id#1, customer_id#2, status#3, total#4, order_date#5, shipped_at#6, ship_customer_id#7, attachment#8)

         """.ReplaceLineEndings());
      // Where the key isn't known at the sort (it comes from the joined side), the rows are sorted again above the join.
      PlanPrinter.Print(Paged("sales.customers.orderBy(desc(name)).selectMany(orders_by_customer)")).ShouldContain("Sort name#2 desc, id#9");
      // Rows that aren't an entity's aren't sorted for them.
      Paged("sales.orders.select(id, total)").ShouldBeOfType<LimitNode>().Input.ShouldBeOfType<ProjectNode>();
   }

   [Fact]
   public void DrillDownParametersAreNewToTheKeysToo()
   {
      QueryParameters parameters = new QueryParameters().Add("key1", 100m);
      BoundProgram program = Binder.Bind("sales.orders.groupBy(big: total > $key1).select(big, n: count())", PlanCases.Catalog, parameters);
      DrillDownLink link = Lowerer.Lower(program).Schema.Find("n")!.Link.ShouldBeOfType<DrillDownLink>();
      Query.Execution.QueryRequest drill = link.Query([true, 3L]);
      drill.Text.ShouldBe("(sales.orders).where((total > $key1) == $key1_)");
      drill.Parameters!.TryGet("key1_", out QueryParameter? value).ShouldBeTrue();
      value!.Value.ShouldBe(true);
   }

   [Fact]
   public void ComposedFiltersReachTheScans()
   {
      string composed = QueryText.Compose("sales.orders.select(id, total, who: customer.name)", ["total > 100", "who == 'Acme'"], [new QuerySortKey("who")]);
      composed.ShouldBe("(sales.orders.select(id, total, who: customer.name)).where(total > 100).where(who == 'Acme').orderBy(who)");
      LogicalPlan plan = PlanCases.Plan(composed);
      // Each filter sits right on the scan of the table it tests.
      FilterOver(plan.Root, "orders").Predicate.ShouldBeOfType<Query.Planning.PlanBinary>().Right.ShouldBeOfType<PlanLiteral>().Value.ShouldBe(100m);
      FilterOver(plan.Root, "customers").Predicate.ShouldBeOfType<Query.Planning.PlanBinary>().Right.ShouldBeOfType<PlanLiteral>().Value.ShouldBe("Acme");
   }

   private static FilterNode FilterOver(PlanNode node, string table)
   {
      if (node is FilterNode { Input: ScanNode scan } filter && scan.Entity.Name == table) { return filter; }
      foreach (PlanNode input in node.Inputs)
      {
         try { return FilterOver(input, table); }
         catch (ShouldAssertException) { }
      }
      throw new ShouldAssertException($"No filter right on a scan of {table} in{System.Environment.NewLine}{PlanPrinter.Print(node)}");
   }
}
