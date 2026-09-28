using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Functions;
using GalaxyData.Query.Planning;
using GalaxyData.Query.Planning.Federation;
using GalaxyData.Query.Planning.Optimizer;
using GalaxyData.Query.Sql;
using GalaxyData.Testing;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.Tests.Planning;

/// <summary>Golden reports: how plans that read more than one source are split into fragments and the merge engine's SQL.</summary>
public sealed class FederationReportTests
{
   /// <summary>Queries over sales (PostgreSQL) and crm (SQL Server), linked by the contacts' customer.</summary>
   private static readonly string[] Queries =
   [
      "crm.contacts.select(email, who: customer.name, city: customer.city)",
      "crm.contacts.where(customer.city == 'Durban' and icontains(email, 'acme')).select(email, customer.region.name)",
      "sales.customers.select(name, n: contacts.count())",
      "sales.customers.where(contacts.any(endsWith(email, '.za'))).select(name)",
      "sales.customers.where(not contacts.any()).select(name)",
      "crm.contacts.orderBy(email).take(5).select(email, customer.name)",
      "crm.contacts.orderBy(desc(email)).select(email, who: customer.name)",
      "sales.customers.select(name).union(crm.contacts.select(name: email))",
      "crm.contacts.groupBy(customer.city).select(city, n: count())",
      "sales.orders.where(total > sales.orders.avg(total) and customer_id in crm.contacts.select(customer_ref)).select(id)",
      "sales.customers.select(name, first: crm.contacts.where(c => c.customer_ref == id).orderBy(email).firstOrDefault().email)",
      "sales.customers.where(c => crm.contacts.count(k => k.customer_ref > c.id) > 1).select(name)",
      "crm.contacts.where(customer.credit_limit > 100).count()",
      "x := crm.contacts.where(email != null); sales.customers.where(c => x.any(k => k.customer_ref == c.id)).count()",
      "sales.orders.count() + crm.contacts.count()",
      "sales.orders.selectMany(o => crm.contacts).count()",
      "crm.contacts.orderBy(email).where(customer_ref in crm.contacts.select(customer_ref)).select(email, customer.name).take(1)",
      "sales.orders.orderBy(desc(total)).where(not order_lines.any()).select(id, who: crm.contacts.where(c => c.customer_ref == customer_id).count()).take(1)",
   ];

   private static SqlDialect? Dialect(SourceInfo source) => source.Alias switch
   {
      "sales" => SqlDialect.PostgreSql,
      "crm" => SqlDialect.SqlServer,
      _ => SqlDialect.Sqlite,
   };

   [Fact]
   public void Plans()
   {
      QueryReport report = new();
      foreach (string query in Queries) { report.Case(query, Report(PlanCases.Plan(query), pushDown: true)); }
      Golden.Match(report.ToString());
   }

   [Fact]
   public void WithoutPushDownSourcesOnlyReadTables()
   {
      string query = "sales.orders.where(status == 'open').groupBy(customer_id).select(customer_id, n: count())";
      Golden.Match(Report(PlanCases.Plan(query), pushDown: false));
   }

   /// <summary>A source that can't run upper(): a filter using it leaves the rest of its conditions in the fragment.</summary>
   [Fact]
   public void ConditionsTheSourceCanCheckStayInTheFragment()
   {
      string query = "sales.customers.where(city == 'Durban' and upper(name) == 'ACME' and credit_limit > 10).select(name, credit_limit)";
      string report = Report(PlanCases.Plan(query), pushDown: true, runs: (node, _) => !Calls(node, FunctionId.Upper));
      Golden.Match(report);
      report.ShouldContain("WHERE c.city = @p0 AND c.credit_limit > @p1");
   }

   private static bool Calls(PlanNode node, FunctionId function) =>
      PlanAnalysis.Expressions(node).Any(e => Contains(e, function)) || node.Inputs.Any(i => Calls(i, function));

   private static bool Contains(PlanExpr expr, FunctionId function)
   {
      bool found = false;
      PlanRewriter.Map(expr, e =>
      {
         found |= e is PlanFunction call && call.Function.Id == function;
         return null;
      });
      return found;
   }

   private static string Report(LogicalPlan plan, bool pushDown, Func<PlanNode, SourceInfo, bool>? runs = null)
   {
      FederatedPlan federated = FederationPlanner.Plan(plan, Dialect, SqlDialect.DuckDb, pushDown, runs);
      StringBuilder text = new();
      text.Append(PlanPrinter.Print(plan.Root, n => federated.Sites.GetValueOrDefault(n)));
      foreach (PlannedFragment fragment in federated.Fragments)
      {
         text.AppendLine().Append("-- ").Append(fragment.Table.Table).Append(": ").Append(fragment.Source.Alias).Append(" (").Append(fragment.Dialect.Name).Append(") ")
            .AppendJoin(", ", fragment.Columns.Select(c => $"{c.Name} {c.Type}")).AppendLine();
         text.AppendLine(fragment.Statement.ToString());
      }
      text.AppendLine().AppendLine("-- merge (DuckDB)").AppendLine(federated.Merge.ToString());
      return text.ToString();
   }
}
