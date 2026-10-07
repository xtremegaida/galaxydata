using System;
using System.Linq;
using GalaxyData.Web.Catalog;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests;

/// <summary>
/// The application names no data source's database: it takes its connectors from the built-in list, and only the
/// merge engine (DuckDB's) and its own metadata database (SQLite, through EF Core) are its own.
/// </summary>
public sealed class ArchitectureTests
{
   private static readonly string[] Drivers = ["Npgsql", "Microsoft.Data.SqlClient", "DuckDB.NET.Data", "ClickHouse.Driver"];

   [Fact]
   public void TheApplicationReferencesNoConnectorButTheMergeEngine() =>
      typeof(SourceProviders).Assembly.GetReferencedAssemblies().Select(a => a.Name!)
         .Where(name => name.StartsWith("GalaxyData.Query.", StringComparison.Ordinal) && name != "GalaxyData.Query.DuckDb" || Drivers.Contains(name))
         .ShouldBeEmpty();
}
