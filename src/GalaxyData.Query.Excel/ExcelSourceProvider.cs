using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.DuckDb;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Providers;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Excel;

/// <summary>How a folder of workbooks reads as a source.</summary>
public sealed class ExcelFolderOptions
{
   /// <summary>The folder; its own .xlsx workbooks (not those of folders in it) are the source's schemas.</summary>
   public required string Path { get; init; }

   /// <summary>The first row of each sheet with a value names its columns; off, every row is data and columns are named by their letters.</summary>
   public bool HeaderRow { get; init; } = true;

   /// <summary>Read every column as text, as its values read without their number formats; off, a column's type is the one all its values share.</summary>
   public bool AllText { get; init; }

   /// <summary>Include the sheets Excel hides.</summary>
   public bool IncludeHiddenSheets { get; init; }
}

/// <summary>
/// Excel folder sources: a folder of .xlsx workbooks, each a schema named by its file (without <c>.xlsx</c>), each
/// of its worksheets a table: <c>xl["Budget 2024"]["Sheet 1"]</c>. Read-only. Workbooks are read without an Excel
/// library, and no DuckDB extension. The sheets are loaded into the merge engine's database (a catalog for each
/// folder, <c>excel_xl</c>; DuckDB's names ignore case, so aliases that differ only in case get <c>excel_xl_2</c>)
/// when a query first reads them, and again when their workbooks change; queries of them run there, in DuckDB's
/// dialect, and their rows go into a query's merge tables without leaving it.
/// </summary>
/// <remarks>
/// Register each folder with <see cref="AddFolder"/>, read its schema with <see cref="IntrospectAsync(string, IntrospectionOptions?, CancellationToken)"/>
/// and add it to the catalog as <see cref="Source"/>. The provider opens its own connections: the application's
/// connection factory is never asked for one to a folder.
/// </remarks>
public sealed class ExcelSourceProvider : SourceProvider, IDisposable
{
   public const string Kind = "excel";

   private readonly DuckDbMergeEngine merge;
   private readonly ConcurrentDictionary<string, ExcelFolder> folders = new(StringComparer.Ordinal);
   private readonly Lock gate = new();

   public ExcelSourceProvider(DuckDbMergeEngine merge)
   {
      ArgumentNullException.ThrowIfNull(merge);
      this.merge = merge;
      Introspector = new FolderIntrospector(this);
   }

   public override string ProviderKind => Kind;

   public override SqlDialect Dialect => SqlDialect.DuckDb;

   /// <summary>Reads the schema of the folder of a connection this provider opened.</summary>
   public override ISchemaIntrospector Introspector { get; }

   public override IMergeEngine? Host => merge;

   /// <summary>Registers the folder of a source, in place of the one it had.</summary>
   public void AddFolder(string alias, ExcelFolderOptions options)
   {
      ArgumentException.ThrowIfNullOrEmpty(alias);
      ArgumentNullException.ThrowIfNull(options);
      lock (gate)
      {
         string catalog;
         if (folders.TryRemove(alias, out ExcelFolder? old))
         {
            old.Detach();
            catalog = old.Catalog;
         }
         else
         {
            // DuckDB's names ignore case: another alias in another case has a catalog of its own.
            string stem = "excel_" + alias.ToLowerInvariant();
            catalog = stem;
            for (int n = 2; folders.Values.Any(f => string.Equals(f.Catalog, catalog, StringComparison.OrdinalIgnoreCase)); n++)
            {
               catalog = stem + "_" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
         }
         ExcelFolder folder = new(alias, catalog, options, merge);
         folder.Attach();
         folders[alias] = folder;
      }
   }

   /// <summary>Forgets a source's folder, and drops the sheets loaded from it.</summary>
   public bool RemoveFolder(string alias)
   {
      lock (gate)
      {
         if (!folders.TryRemove(alias, out ExcelFolder? folder)) { return false; }
         folder.Detach();
         return true;
      }
   }

   /// <summary>A registered folder's source, to add to a catalog with its schema: read-only, its tables named in the folder's catalog.</summary>
   public SourceInfo Source(string alias) =>
      new(alias, Kind, string.Empty) { IsReadOnly = true, SupportsDml = false, Catalog = Folder(alias).Catalog };

   /// <summary>
   /// A registered folder's schema: every row of each sheet is read for the types of its columns. Workbooks and
   /// sheets that can't be read are left out, and named in <see cref="SourceSchema.Warnings"/>. Sheets loaded before
   /// that aren't in the schema any more are dropped.
   /// </summary>
   public async Task<SourceSchema> IntrospectAsync(string alias, IntrospectionOptions? options = null, CancellationToken cancellationToken = default)
   {
      ExcelFolder folder = Folder(alias);
      SourceSchema schema = await folder.IntrospectAsync(options ?? IntrospectionOptions.Default, cancellationToken).ConfigureAwait(false);
      folder.Prune(schema.Tables);
      return schema;
   }

   private ExcelFolder Folder(string alias) =>
      folders.TryGetValue(alias, out ExcelFolder? folder) ? folder : throw new InvalidOperationException($"No Excel folder is registered as '{alias}'");

   /// <summary>The folder of a source a query reads: that it isn't registered (any more) fails the query.</summary>
   private ExcelFolder Queried(SourceInfo source) =>
      folders.TryGetValue(source.Alias, out ExcelFolder? folder) ? folder : throw new QueryExecutionException($"{source.Alias}: no Excel folder is registered for the source");

   /// <summary>A connection to the merge engine's database, in the folder's catalog.</summary>
   public override async ValueTask<DbConnection> OpenConnectionAsync(SourceInfo source, IConnectionFactory connections, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(source);
      ExcelFolder folder = Queried(source);
      DuckDBConnection connection = merge.Connect();
      try
      {
         await connection.ExecuteAsync("USE " + SqlDialect.DuckDb.QuoteIdentifier(folder.Catalog), cancellationToken).ConfigureAwait(false);
         return connection;
      }
      catch
      {
         await connection.DisposeAsync().ConfigureAwait(false);
         throw;
      }
   }

   /// <summary>
   /// Loads the sheets a statement reads that aren't loaded, or whose workbooks changed, or that are loaded as another
   /// catalog types them; the lease keeps them so until the statement has started.
   /// </summary>
   public override ValueTask<IDisposable?> PrepareReadAsync(SourceInfo source, IReadOnlyList<TableEntity> tables, QueryEngineOptions options,
                                                           CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(source);
      ArgumentNullException.ThrowIfNull(tables);
      ArgumentNullException.ThrowIfNull(options);
      ExcelFolder folder = Queried(source);
      if (!string.Equals(source.Catalog, folder.Catalog, StringComparison.Ordinal))
      {
         throw new InvalidOperationException($"The source '{source.Alias}' names its tables in '{source.Catalog}', and its folder's are in '{folder.Catalog}': make it with ExcelSourceProvider.Source");
      }
      return folder.PrepareAsync(tables, options.LenientConversion, cancellationToken);
   }

   public override ValueTask PrepareConnectionAsync(DbConnection connection, CancellationToken cancellationToken) =>
      DuckDbSourceProvider.Instance.PrepareConnectionAsync(connection, cancellationToken);

   public override void PrepareCommand(DbCommand command) => DuckDbSourceProvider.Instance.PrepareCommand(command);

   public override void BindParameter(DbParameter parameter, object? value, ScalarType type) =>
      DuckDbSourceProvider.Instance.BindParameter(parameter, value, type);

   public override object? ReadValue(DbDataReader reader, int ordinal) => DuckDbSourceProvider.Instance.ReadValue(reader, ordinal);

   /// <summary>Drops the sheets loaded from every folder.</summary>
   public void Dispose()
   {
      foreach (string alias in folders.Keys.ToList()) { RemoveFolder(alias); }
   }

   /// <summary>Finds the folder of a connection by the catalog it is in.</summary>
   private sealed class FolderIntrospector(ExcelSourceProvider provider) : ISchemaIntrospector
   {
      public async Task<SourceSchema> IntrospectAsync(DbConnection connection, IntrospectionOptions options, CancellationToken cancellationToken)
      {
         ArgumentNullException.ThrowIfNull(connection);
         string? catalog = await connection.ScalarTextAsync("SELECT current_database()", cancellationToken).ConfigureAwait(false);
         ExcelFolder folder = provider.folders.Values.FirstOrDefault(f => string.Equals(f.Catalog, catalog, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The connection isn't one to an Excel folder: open it with ExcelSourceProvider.OpenConnectionAsync");
         return await provider.IntrospectAsync(folder.Alias, options, cancellationToken).ConfigureAwait(false);
      }
   }
}
