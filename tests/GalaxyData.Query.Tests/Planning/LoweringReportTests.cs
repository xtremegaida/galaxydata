using System.Text;
using GalaxyData.Query.Planning;
using GalaxyData.Query.Results;
using GalaxyData.Testing;
using Xunit;

namespace GalaxyData.Query.Tests.Planning;

/// <summary>Golden report: the logical plan of each query, and where each result column comes from.</summary>
public sealed class LoweringReportTests
{
   [Fact]
   public void Plans()
   {
      QueryReport report = new();
      foreach (string query in PlanCases.Queries)
      {
         LogicalPlan plan = PlanCases.Plan(query);
         StringBuilder text = new(PlanPrinter.Print(plan.Root));
         foreach (ResultColumn column in plan.Schema.Columns)
         {
            text.Append("=> ").Append(column.Name).Append(' ').Append(column.Type).Append(": ").Append(column.Lineage).AppendLine();
         }
         report.Case(query, text.ToString());
      }
      Golden.Match(report.ToString());
   }
}
