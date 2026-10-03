using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Excel;
using GalaxyData.Query.Execution;
using GalaxyData.Web.Connections;

namespace GalaxyData.Web.Catalog;

/// <summary>How a source of the catalog connects: its kind and connection string, or why it can't (<see cref="Problem"/>).</summary>
public sealed record SourceRuntime(string Alias, ConnectionKind Kind, string? ConnectionString, string? Problem = null)
{
   /// <summary>Its alias and kind: never the connection string, which has its secrets.</summary>
   public override string ToString() => $"{Alias} ({Kind.Id})";
}

/// <summary>
/// How queries reach the sources: the engine's <see cref="IConnectionFactory"/>. Each source connects as the catalog
/// last built says, with a connection string written from its settings and secrets (read-only or not). Sources with
/// the same string share a connector (PostgreSQL's data source; the providers' pools), let go once no source uses the
/// string any more. Folders of workbooks are registered with the Excel provider, which opens its own connections.
/// </summary>
public sealed class SourceConnections(SourceProviders providers) : IConnectionFactory, IAsyncDisposable
{
   private readonly Lock gate = new();
   private readonly Dictionary<string, SourceConnector> connectors = new(StringComparer.Ordinal);
   private readonly Dictionary<string, ExcelFolderOptions> folders = new(StringComparer.Ordinal);
   private Dictionary<string, SourceRuntime> sources = new(StringComparer.OrdinalIgnoreCase);
   private bool disposed;

   /// <summary>
   /// Registers a source's folder of workbooks with the Excel provider, unless it is registered so already (which
   /// would drop the sheets loaded from it); the source to add to a catalog.
   /// </summary>
   internal SourceInfo Folder(string alias, ExcelFolderOptions options)
   {
      lock (gate)
      {
         if (!folders.TryGetValue(alias, out ExcelFolderOptions? registered) || !Same(registered, options))
         {
            providers.Excel.AddFolder(alias, options);
            folders[alias] = options;
         }
      }
      return providers.Excel.Source(alias);
   }

   /// <summary>
   /// Makes the sources of a catalog just built the ones queries reach. Connectors whose strings no source has any
   /// more are let go, and folders no source has are forgotten.
   /// </summary>
   internal async Task PublishAsync(IReadOnlyCollection<SourceRuntime> next)
   {
      List<SourceConnector> retired = [];
      lock (gate)
      {
         sources = next.ToDictionary(s => s.Alias, StringComparer.OrdinalIgnoreCase);
         HashSet<string> used = next.Where(s => s.ConnectionString != null).Select(s => s.ConnectionString!).ToHashSet(StringComparer.Ordinal);
         foreach (string connectionString in connectors.Keys.Where(c => !used.Contains(c)).ToList())
         {
            retired.Add(connectors[connectionString]);
            connectors.Remove(connectionString);
         }
         // The provider's names are exact: a folder whose alias came back in another case is another folder.
         foreach (string alias in folders.Keys.Where(a => !(sources.TryGetValue(a, out SourceRuntime? s) && s.Kind is ExcelKind && s.Alias == a)).ToList())
         {
            providers.Excel.RemoveFolder(alias);
            folders.Remove(alias);
         }
      }
      // Connections already open go on; a query opening another as its source changed opens it as it is now.
      foreach (SourceConnector connector in retired) { await connector.DisposeAsync(); }
   }

   public async ValueTask<DbConnection> OpenAsync(SourceInfo source, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(source);
      for (int attempt = 0; ; attempt++)
      {
         SourceConnector connector = Connector(source);
         try
         {
            return await connector.OpenAsync(cancellationToken);
         }
         catch (ObjectDisposedException) when (attempt == 0)
         {
            // Its settings changed as it was opened: open it as they are now.
         }
      }
   }

   private SourceConnector Connector(SourceInfo source)
   {
      lock (gate)
      {
         ObjectDisposedException.ThrowIf(disposed, this);
         if (!sources.TryGetValue(source.Alias, out SourceRuntime? runtime)) { throw new SourceUnavailableException(source, "There is no such connection any more"); }
         if (runtime.Problem != null) { throw new SourceUnavailableException(source, runtime.Problem); }
         if (runtime.ConnectionString == null) { throw new InvalidOperationException($"{source.Alias} is opened by its provider"); }
         if (!connectors.TryGetValue(runtime.ConnectionString, out SourceConnector? connector))
         {
            connector = runtime.Kind.Connector(runtime.ConnectionString);
            connectors.Add(runtime.ConnectionString, connector);
         }
         return connector;
      }
   }

   private static bool Same(ExcelFolderOptions a, ExcelFolderOptions b) =>
      string.Equals(a.Path, b.Path, StringComparison.Ordinal) && a.HeaderRow == b.HeaderRow && a.AllText == b.AllText && a.IncludeHiddenSheets == b.IncludeHiddenSheets;

   public async ValueTask DisposeAsync()
   {
      List<SourceConnector> all;
      lock (gate)
      {
         disposed = true;
         all = [.. connectors.Values];
         connectors.Clear();
      }
      foreach (SourceConnector connector in all) { await connector.DisposeAsync(); }
   }
}
