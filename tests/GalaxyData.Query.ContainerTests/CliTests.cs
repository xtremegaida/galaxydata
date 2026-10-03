using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Cli;
using GalaxyData.Query.IntegrationTests.Execution;
using GalaxyData.Testing;
using Xunit;

namespace GalaxyData.Query.ContainerTests;

/// <summary>gdq with server sources: <c>-s shop=postgres:&lt;connection string&gt;</c> and <c>sqlserver:</c>.</summary>
public sealed partial class CliTests(Servers servers)
{
   [GeneratedRegex(@"; \d+ ms\)")]
   private static partial Regex Timing();

   private static async Task<string> RunAsync(string title, params string[] args)
   {
      using StringWriter output = new();
      using StringWriter error = new();
      int exit = await GdqApp.RunAsync(args, new StringReader(string.Empty), output, error, TestContext.Current.CancellationToken);
      StringBuilder text = new();
      text.Append("### ").AppendLine(title).Append("exit ").Append(exit).AppendLine();
      if (output.ToString().Length > 0) { text.AppendLine("--- out").Append(output.ToString().TrimEnd()).AppendLine(); }
      if (error.ToString().Length > 0) { text.AppendLine("--- err").Append(error.ToString().TrimEnd()).AppendLine(); }
      return Timing().Replace(text.ToString(), "; (time))").ReplaceLineEndings(Environment.NewLine) + Environment.NewLine;
   }

   [Fact]
   public async Task Transcript()
   {
      string postgres = "shop=postgres:" + (await servers.ShopAsync(ServerKind.Postgres)).ConnectionString;
      string sqlServer = "shop=sqlserver:" + (await servers.ShopAsync(ServerKind.SqlServer)).ConnectionString;
      string sales = "sales=postgres:" + (await servers.SalesAsync(ServerKind.Postgres)).ConnectionString;
      string crm = "crm=sqlserver:" + (await servers.CrmAsync(ServerKind.SqlServer)).ConnectionString;
      const string example = "shop.orders.where(status == 'open').select(id, total, who: customer.name, city: customer.city).orderBy(id)";

      StringBuilder transcript = new();
      transcript.Append(await RunAsync("run example 1 on PostgreSQL, with its SQL", "run", "-s", postgres, "--sql", example));
      transcript.Append(await RunAsync("run example 1 on SQL Server, with its SQL", "run", "-s", sqlServer, "--sql", example));
      transcript.Append(await RunAsync("the SQL of a page of groups, on SQL Server", "sql", "-s", sqlServer,
         "shop.orders.groupBy(customer).select(customer.name, n: count(), total: sum(total)).orderBy(desc(total)).skip(1).take(1)"));
      string overlay = Path.Combine(Path.GetTempPath(), $"gdq-overlay-{Guid.NewGuid():N}.json");
      await File.WriteAllTextAsync(overlay, Conformance.SplitOverlay.ToJson(), TestContext.Current.CancellationToken);
      try
      {
         const string across = "sales.orders.where(status == 'open').select(id, total, who: customer.name, city: customer.city).orderBy(id)";
         transcript.Append(await RunAsync("run across PostgreSQL and SQL Server", "run", "-s", sales, "-s", crm, "--overlay", overlay, across));
         transcript.Append(await RunAsync("explain across PostgreSQL and SQL Server", "explain", "-s", sales, "-s", crm, "--overlay", overlay, across));
      }
      finally
      {
         File.Delete(overlay);
      }
      transcript.Append(await RunAsync("a server source needs a connection string", "run", "-s", "shop=sqlserver:shop.mdf", "shop.orders.count()"));
      Golden.Match(transcript.ToString());
   }
}
