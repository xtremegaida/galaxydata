using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.DuckDb;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Language;
using GalaxyData.Query.Providers;
using GalaxyData.Query.Sqlite;
using Microsoft.Data.Sqlite;

namespace GalaxyData.Query.Cli;

/// <summary>
/// A source given on the command line as <c>alias=kind:target</c>. The target is a database file (opened read-only),
/// a <c>.sql</c> script (run into a fresh in-memory database), <c>:memory:</c>, or a connection string (anything with
/// an <c>=</c> in it).
/// </summary>
internal sealed record SourceSpec(string Alias, string Kind, string Target)
{
   public static IReadOnlyList<string> Kinds { get; } = ["sqlite", "duckdb"];

   public static SourceSpec Parse(string text)
   {
      int equals = text.IndexOf('=', StringComparison.Ordinal);
      int colon = equals < 0 ? -1 : text.IndexOf(':', equals + 1);
      if (equals <= 0 || colon < 0)
      {
         throw new FormatException($"'{text}' is not a source; write alias=kind:target, e.g. shop=sqlite:shop.db");
      }
      string alias = text[..equals].Trim();
      string kind = text[(equals + 1)..colon].Trim().ToLowerInvariant();
      string target = text[(colon + 1)..].Trim();
      if (!QueryText.IsBareIdentifier(alias)) { throw new FormatException($"'{alias}' can't be a source alias; use letters, digits and underscores"); }
      if (!((IList<string>)Kinds).Contains(kind)) { throw new FormatException($"'{kind}' is not a source kind; use {string.Join(" or ", Kinds)}"); }
      if (target.Length == 0) { throw new FormatException($"The source '{alias}' needs a target after '{kind}:'"); }
      return new SourceSpec(alias, kind, target);
   }

   public bool IsScript => Target.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) && !Target.Contains('=', StringComparison.Ordinal);

   public bool IsMemory => Target == ":memory:";
}

/// <summary>The command line's sources, opened: each keeps a first connection open, which in-memory databases need.</summary>
internal sealed class CliSources : IConnectionFactory, IAsyncDisposable
{
   private readonly Dictionary<string, (DbConnection Keeper, Func<DbConnection> Open)> opened = new(StringComparer.Ordinal);
   private readonly CatalogBuilder builder = new();

   public static async Task<CliSources> OpenAsync(IEnumerable<SourceSpec> specs, CancellationToken cancellationToken)
   {
      CliSources sources = new();
      try
      {
         foreach (SourceSpec spec in specs) { await sources.AddAsync(spec, cancellationToken); }
         return sources;
      }
      catch
      {
         await sources.DisposeAsync();
         throw;
      }
   }

   public QueryCatalog BuildCatalog(CatalogOverlay overlay) => builder.WithOverlay(overlay).Build();

   private async Task AddAsync(SourceSpec spec, CancellationToken cancellationToken)
   {
      if (opened.ContainsKey(spec.Alias)) { throw new FormatException($"The source alias '{spec.Alias}' is given twice"); }
      (DbConnection keeper, Func<DbConnection> open, SourceProvider provider) = spec.Kind switch
      {
         "sqlite" => Sqlite(spec),
         _ => DuckDb(spec),
      };
      try
      {
         if (keeper.State != System.Data.ConnectionState.Open) { await keeper.OpenAsync(cancellationToken); }
         if (spec.IsScript) { await keeper.ExecuteAsync(await File.ReadAllTextAsync(spec.Target, cancellationToken), cancellationToken); }
         SourceSchema schema = await provider.Introspector.IntrospectAsync(keeper, IntrospectionOptions.Default, cancellationToken);
         builder.AddSource(new SourceInfo(spec.Alias, provider.ProviderKind, schema.DefaultSchema) { IsReadOnly = true }, schema);
         opened.Add(spec.Alias, (keeper, open));
      }
      catch
      {
         await keeper.DisposeAsync();
         throw;
      }
   }

   private static (DbConnection, Func<DbConnection>, SourceProvider) Sqlite(SourceSpec spec)
   {
      string connectionString = spec switch
      {
         { IsScript: true } or { IsMemory: true } => $"Data Source=gdq_{spec.Alias}_{Guid.NewGuid():N};Mode=Memory;Cache=Shared",
         _ when spec.Target.Contains('=', StringComparison.Ordinal) => spec.Target,
         _ => new SqliteConnectionStringBuilder { DataSource = ExistingFile(spec), Mode = SqliteOpenMode.ReadOnly }.ToString(),
      };
      return (new SqliteConnection(connectionString), () => new SqliteConnection(connectionString), SqliteSourceProvider.Instance);
   }

   private static (DbConnection, Func<DbConnection>, SourceProvider) DuckDb(SourceSpec spec)
   {
      string connectionString = spec switch
      {
         { IsScript: true } or { IsMemory: true } => "Data Source=:memory:",
         _ when spec.Target.Contains('=', StringComparison.Ordinal) => spec.Target,
         _ => $"Data Source={ExistingFile(spec)};ACCESS_MODE=READ_ONLY",
      };
      DuckDBConnection keeper = new(connectionString);
      // Only in-memory databases can be duplicated; files are opened again, and DuckDB shares them within the process.
      Func<DbConnection> open = spec.IsScript || spec.IsMemory ? keeper.Duplicate : () => new DuckDBConnection(connectionString);
      return (keeper, open, DuckDbSourceProvider.Instance);
   }

   private static string ExistingFile(SourceSpec spec) =>
      File.Exists(spec.Target) ? Path.GetFullPath(spec.Target) : throw new FileNotFoundException($"The {spec.Kind} database for '{spec.Alias}' doesn't exist: {spec.Target}");

   public async ValueTask<DbConnection> OpenAsync(SourceInfo source, CancellationToken cancellationToken)
   {
      DbConnection connection = opened[source.Alias].Open();
      if (connection.State != System.Data.ConnectionState.Open) { await connection.OpenAsync(cancellationToken); }
      return connection;
   }

   public async ValueTask DisposeAsync()
   {
      foreach ((DbConnection keeper, _) in opened.Values) { await keeper.DisposeAsync(); }
      opened.Clear();
   }
}
