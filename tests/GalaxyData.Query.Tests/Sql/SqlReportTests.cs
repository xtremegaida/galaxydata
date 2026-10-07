using System;
using System.Linq;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Tests.Planning;
using GalaxyData.Testing;
using Xunit;

namespace GalaxyData.Query.Tests.Sql;

/// <summary>Golden reports: the SQL each dialect gets for the shared plan cases, with its parameters.</summary>
public sealed class SqlReportTests
{
   public static TheoryData<string> Dialects => [.. TestDialects.All.Select(d => d.ProviderKind)];

   [Theory]
   [MemberData(nameof(Dialects))]
   public void Statements(string providerKind)
   {
      SqlDialect dialect = TestDialects.All.Single(d => d.ProviderKind == providerKind);
      QueryReport report = new();
      foreach (string query in PlanCases.Queries)
      {
         string sql;
         try
         {
            sql = SqlBuilder.Build(PlanCases.Plan(query), dialect).ToString();
         }
         catch (Exception e) when (e is NotSupportedException or SqlTranslationException)
         {
            // What a source can't write runs in the merge engine.
            sql = $"-- not written for {dialect.Name}: {e.Message}";
         }
         report.Case(query, sql);
      }
      Golden.Match(report.ToString(), suffix: providerKind);
   }
}
