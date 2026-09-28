using System.Text;
using GalaxyData.Query.Planning;
using GalaxyData.Query.Planning.Optimizer;
using GalaxyData.Testing;
using Xunit;

namespace GalaxyData.Query.Tests.Planning;

/// <summary>Golden report: for queries each rule is meant for, the plan before the optimizer and after each phase, with the rules that fired.</summary>
public sealed class OptimizerReportTests
{
   private static readonly string[] Queries =
   [
      // exists to semi join, not exists to anti join
      "x := sales.orders.where(total > 3); sales.customers.where(cust => x.any(order => cust.id == order.customer_id))",
      "sales.customers.where(not orders_by_customer.any() and city != null).select(name)",
      "sales.customers.where(c => sales.orders.all(o => o.customer_id != c.id or o.total > 0))",
      // in to semi join
      "sales.orders.where(customer_id in sales.customers.where(city == 'Cape Town').select(id))",
      // correlated aggregates to (merged) group joins
      "sales.customers.select(name, n: orders_by_customer.count(), spend: orders_by_customer.sum(total), last: orders_by_customer.max(order_date))",
      "sales.customers.where(orders_by_customer.sum(total) > 100).select(name)",
      // correlated joins
      "sales.customers.selectMany(orders_by_customer, who: outer.name, total: inner.total)",
      // pushdown through projections, joins (with transitive constants), aggregates, set operations and sorts
      "sales.orders.select(id, who: customer.name, cid: customer_id).where(cid == 7 and who != 'x')",
      "sales.orders.orderBy(total).where(status == 'open')",
      "sales.orders.groupBy(customer_id).select(customer_id, n: count()).where(customer_id > 10 and n > 1)",
      "sales.orders.select(id, status).union(sales.orders.select(status, id)).where(status == 'open')",
      // outer to inner joins
      "sales.orders.where(ship_customer.city == 'Durban').select(id, ship_customer.name)",
      "sales.orders.leftJoin(sales.customers, outer.ship_customer_id == inner.id, o: outer, c: inner).where(c.city != null).select(o.id, c.name)",
      // unused navigation joins and columns
      "sales.orders.select(id, who: customer.name).select(id)",
      "sales.orders.groupBy(customer).select(customer.id, n: count())",
      // useless sorts
      "sales.orders.orderBy(total).groupBy(status).select(status, n: count())",
      "sales.customers.select(n: orders_by_customer.orderBy(total).count())",
   ];

   [Fact]
   public void Phases()
   {
      QueryReport report = new();
      foreach (string query in Queries)
      {
         LogicalPlan lowered = PlanCases.Lowered(query);
         OptimizerTrace trace = new();
         PlanOptimizer.Optimize(lowered, trace);
         StringBuilder text = new();
         text.AppendLine("-- lowered").Append(PlanPrinter.Print(lowered.Root));
         foreach ((string phase, var rules, string plan) in trace.Phases)
         {
            text.Append("-- ").Append(phase);
            if (rules.Count > 0) { text.Append(": ").AppendJoin(", ", rules); }
            text.AppendLine().Append(plan);
         }
         report.Case(query, text.ToString());
      }
      Golden.Match(report.ToString());
   }
}
