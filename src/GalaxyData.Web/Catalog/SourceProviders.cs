using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.DuckDb;
using GalaxyData.Query.Excel;
using GalaxyData.Query.Execution;
using GalaxyData.Query.PostgreSql;
using GalaxyData.Query.Sqlite;
using GalaxyData.Query.SqlServer;

namespace GalaxyData.Web.Catalog;

/// <summary>The engine's providers, one for each kind of connection; the Excel folders' keeps their sheets in the application's merge engine.</summary>
public sealed class SourceProviders
{
   public SourceProviders(ExcelSourceProvider excel)
   {
      ArgumentNullException.ThrowIfNull(excel);
      Excel = excel;
      All = [PostgreSqlSourceProvider.Instance, SqlServerSourceProvider.Instance, SqliteSourceProvider.Instance, DuckDbSourceProvider.Instance, excel];
   }

   public IReadOnlyList<SourceProvider> All { get; }

   public ExcelSourceProvider Excel { get; }

   /// <summary>The provider of a kind of connection (its id is the provider's kind).</summary>
   public SourceProvider For(string kind) =>
      All.FirstOrDefault(p => string.Equals(p.ProviderKind, kind, StringComparison.Ordinal))
         ?? throw new InvalidOperationException($"No provider serves {kind} sources");
}
