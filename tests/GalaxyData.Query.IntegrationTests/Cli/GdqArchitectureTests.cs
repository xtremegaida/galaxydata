using System;
using System.Linq;
using GalaxyData.Query.Cli;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Cli;

/// <summary>The command line names no data source's database: it opens sources through the built-in connectors, and only the merge engine (DuckDB's) is its own.</summary>
public sealed class GdqArchitectureTests
{
   private static readonly string[] Drivers = ["Npgsql", "Microsoft.Data.SqlClient", "Microsoft.Data.Sqlite", "DuckDB.NET.Data", "ClickHouse.Driver"];

   [Fact]
   public void TheCommandLineReferencesNoConnectorButTheMergeEngine() =>
      typeof(SourceSpec).Assembly.GetReferencedAssemblies().Select(a => a.Name!)
         .Where(name => name.StartsWith("GalaxyData.Query.", StringComparison.Ordinal) && name != "GalaxyData.Query.DuckDb" || Drivers.Contains(name))
         .ShouldBeEmpty();
}
