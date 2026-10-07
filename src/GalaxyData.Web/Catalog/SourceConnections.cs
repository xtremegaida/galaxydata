using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Connectors;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;

namespace GalaxyData.Web.Catalog;

/// <summary>
/// How a source of the catalog connects: its kind and connection string, or why it can't (<see cref="Problem"/>); or
/// the connector that opens it itself (<see cref="Attached"/>), with no connection string.
/// </summary>
public sealed record SourceRuntime(string Alias, ConnectionKind Kind, string? ConnectionString, string? Problem = null)
{
   /// <summary>The sources its connector opens itself, of which this is one (a folder of workbooks).</summary>
   public IAttachedSources? Attached { get; init; }

   /// <summary>Its alias and kind: never the connection string, which has its secrets.</summary>
   public override string ToString() => $"{Alias} ({Kind.Id})";
}

/// <summary>
/// How queries reach the sources: the engine's <see cref="IConnectionFactory"/>. Each source connects as the catalog
/// last built says, with a connection string written from its settings and secrets (read-only or not). Sources with
/// the same string share a connector (PostgreSQL's data source; the providers' pools), let go once no source uses the
/// string any more. Sources a connector opens itself (folders of workbooks) are attached to it under their aliases.
/// </summary>
public sealed class SourceConnections : IConnectionFactory, IAsyncDisposable
{
   private readonly Lock gate = new();
   private readonly Dictionary<string, SourceConnector> connectors = new(StringComparer.Ordinal);
   private readonly Dictionary<string, IAttachedSources> attached = new(StringComparer.Ordinal);
   private Dictionary<string, SourceRuntime> sources = new(StringComparer.OrdinalIgnoreCase);
   private bool disposed;

   /// <summary>
   /// Attaches a source to the connector that opens it, with its settings and options, unless it is attached so
   /// already (which would drop what was loaded for it); the source to add to a catalog.
   /// </summary>
   internal SourceInfo Attach(string alias, IAttachedSources owner, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> options)
   {
      lock (gate)
      {
         if (attached.TryGetValue(alias, out IAttachedSources? before) && !ReferenceEquals(before, owner)) { before.Detach(alias); }
         SourceInfo source = owner.Attach(alias, settings, options);
         attached[alias] = owner;
         return source;
      }
   }

   /// <summary>
   /// Makes the sources of a catalog just built the ones queries reach. Connectors whose strings no source has any
   /// more are let go, and attached sources no source is any more are detached.
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
         // Attached names are exact: a source whose alias came back in another case is another source.
         foreach ((string alias, IAttachedSources owner) in attached.Where(a => !(sources.TryGetValue(a.Key, out SourceRuntime? s) && ReferenceEquals(s.Attached, a.Value) &&
                                                                                    s.Alias == a.Key)).ToList())
         {
            owner.Detach(alias);
            attached.Remove(alias);
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
