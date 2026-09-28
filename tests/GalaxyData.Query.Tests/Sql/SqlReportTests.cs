using System.Linq;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Tests.Planning;
using GalaxyData.Testing;
using Xunit;

namespace GalaxyData.Query.Tests.Sql;

/// <summary>Golden reports: the SQL each dialect gets for the shared plan cases, with its parameters.</summary>
public sealed class SqlReportTests
{
   public static TheoryData<string> Dialects => [.. SqlDialect.All.Select(d => d.ProviderKind)];

   [Theory]
   [MemberData(nameof(Dialects))]
   public void Statements(string providerKind)
   {
      SqlDialect dialect = SqlDialect.All.Single(d => d.ProviderKind == providerKind);
      QueryReport report = new();
      foreach (string query in PlanCases.Queries)
      {
         report.Case(query, SqlBuilder.Build(PlanCases.Plan(query), dialect).ToString());
      }
      Golden.Match(report.ToString(), suffix: providerKind);
   }
}
