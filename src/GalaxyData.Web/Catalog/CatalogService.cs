using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Introspection;
using GalaxyData.Web.Connections;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Overlay;
using GalaxyData.Web.Schemas;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GalaxyData.Web.Catalog;

/// <summary>A catalog as built: the engine's, every source as it stands, the overlay and what is wrong with it, and the tree over it.</summary>
public sealed class CatalogState(long generation, string version, DateTime builtAt, QueryCatalog catalog, IReadOnlyList<CatalogSource> sources, CatalogTree tree,
                                 StoredOverlay overlay, OverlayIssues issues, IReadOnlyList<(SourceInfo Source, SourceSchema Schema)> schemas)
{
   internal long Generation { get; } = generation;

   /// <summary>A hash of what the catalog was built from: the same after a restart, different after any change.</summary>
   public string Version { get; } = version;

   public DateTime BuiltAt { get; } = builtAt;

   public QueryCatalog Catalog { get; } = catalog;

   /// <summary>Every connection, read or not, by alias.</summary>
   public IReadOnlyList<CatalogSource> Sources { get; } = sources;

   public CatalogTree Tree { get; } = tree;

   /// <summary>The overlay it was built with.</summary>
   public StoredOverlay Overlay { get; } = overlay;

   /// <summary>What building it found wrong with each item of the overlay.</summary>
   public OverlayIssues Issues { get; } = issues;

   /// <summary>The aliases of sources without a schema in it (not read yet, or not readable), ignoring case.</summary>
   public IReadOnlySet<string> Unread { get; } = sources.Where(s => !s.HasSchema).Select(s => s.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);

   /// <summary>
   /// The catalog as it would be with another overlay, over the same schemas: to try an item before it is saved.
   /// It is the engine's alone (no tree), and no source is registered for it.
   /// </summary>
   public QueryCatalog With(CatalogOverlay other)
   {
      CatalogBuilder builder = new();
      foreach ((SourceInfo source, SourceSchema schema) in schemas) { builder.AddSource(source, schema); }
      return builder.WithOverlay(other).Build();
   }
}

/// <summary>
/// The catalog queries run against: built from each connection's newest schema snapshot, with the tree and its
/// search over it. It is built when first asked for, and again after anything it is built from changes
/// (<see cref="Invalidate"/>): the next to ask waits for it, so no one reads the old one after a change. Snapshots
/// are kept read between builds. Building it also makes its sources the ones queries connect to
/// (<see cref="SourceConnections"/>).
/// </summary>
public sealed partial class CatalogService(IServiceScopeFactory scopes, ConnectionKinds kinds, ConnectionSecrets secrets, SourceConnections connections,
                                           TimeProvider clock, ILogger<CatalogService> logger)
{
   /// <summary>The header every API answer carries, with the version of the catalog it was given with (or the latest built, when that is up to date).</summary>
   public const string VersionHeader = "X-Catalog-Version";

   private static readonly string EngineVersion =
      typeof(QueryEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;

   private readonly SemaphoreSlim building = new(1, 1);

   /// <summary>The snapshots read for the last build, by id; damaged ones (no schema) too, so they aren't read and told of again.</summary>
   private Dictionary<long, Snapshot> schemas = [];
   private CatalogState? current;
   private long wanted = 1;

   /// <summary>The catalog last built, when nothing changed since; null otherwise.</summary>
   public CatalogState? Fresh
   {
      get
      {
         CatalogState? state = Volatile.Read(ref current);
         return state != null && state.Generation >= Interlocked.Read(ref wanted) ? state : null;
      }
   }

   /// <summary>Something the catalog is built from changed: the next to ask for it waits for it to be built again.</summary>
   public void Invalidate() => Interlocked.Increment(ref wanted);

   /// <summary>The catalog, as <see cref="GetAsync(CancellationToken)"/> gives it, its version put on the answer (<see cref="VersionHeader"/>).</summary>
   public async Task<CatalogState> GetAsync(HttpResponse response, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(response);
      CatalogState state = await GetAsync(cancellationToken);
      response.Headers[VersionHeader] = state.Version;
      return state;
   }

   public async Task<CatalogState> GetAsync(CancellationToken cancellationToken)
   {
      if (Fresh is { } fresh) { return fresh; }
      await building.WaitAsync(cancellationToken);
      try
      {
         if (Fresh is { } built) { return built; }
         // Read before what it is built from, so a change made while it is built makes it stale.
         long generation = Interlocked.Read(ref wanted);
         // Others may be waiting for it: one who stops waiting doesn't stop it.
         CatalogState state = await BuildAsync(generation, CancellationToken.None);
         Volatile.Write(ref current, state);
         return state;
      }
      finally
      {
         building.Release();
      }
   }

   private async Task<CatalogState> BuildAsync(long generation, CancellationToken cancellationToken)
   {
      long started = clock.GetTimestamp();
      await using AsyncServiceScope scope = scopes.CreateAsyncScope();
      MetadataDb db = scope.ServiceProvider.GetRequiredService<MetadataDb>();
      List<SourceConnection> rows = await db.Connections.AsNoTracking().OrderBy(c => c.Id).ToListAsync(cancellationToken);
      Dictionary<int, (long Id, DateTime CheckedAt)> heads = (await db.SchemaSnapshots.AsNoTracking()
            .Select(s => new { s.Id, s.ConnectionId, s.CheckedAt })
            .ToListAsync(cancellationToken))
         .GroupBy(s => s.ConnectionId)
         .ToDictionary(g => g.Key, g => g.Select(s => (s.Id, s.CheckedAt)).MaxBy(s => s.Id));
      List<long> unread = heads.Values.Where(h => !Cached(h.Id, h.CheckedAt)).Select(h => h.Id).ToList();
      Dictionary<long, Snapshot> loaded = [];
      if (unread.Count > 0)
      {
         Dictionary<long, string> owners = rows.Where(r => heads.ContainsKey(r.Id)).ToDictionary(r => heads[r.Id].Id, r => r.Alias);
         foreach (var read in await db.SchemaSnapshots.AsNoTracking().Where(s => unread.Contains(s.Id))
                     .Select(s => new { s.Id, s.CheckedAt, s.Data, s.ReadWith }).ToListAsync(cancellationToken))
         {
            loaded[read.Id] = Read(read.Id, read.CheckedAt, read.Data, read.ReadWith, owners.GetValueOrDefault(read.Id, "?"));
         }
      }

      StoredOverlay overlay = await StoredOverlay.LoadAsync(db, cancellationToken);

      CatalogBuilder builder = new();
      List<(SourceInfo, SourceSchema)> inputs = [];
      List<CatalogSource> sources = [];
      List<SourceRuntime> runtimes = [];
      Dictionary<long, Snapshot> used = [];
      using IncrementalHash version = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      Append(version, EngineVersion);
      foreach (SourceConnection row in rows)
      {
         string? problem = null;
         bool hasHead = heads.TryGetValue(row.Id, out (long Id, DateTime CheckedAt) head);
         Snapshot? snapshot = !hasHead ? null : Cached(head.Id, head.CheckedAt) ? schemas[head.Id] : loaded.GetValueOrDefault(head.Id);
         if (snapshot != null) { used[head.Id] = snapshot; }
         SourceSchema? schema = snapshot?.Schema;
         if (snapshot is { Schema: null }) { problem = "Its schema snapshot can't be read; read the schema again"; }
         ConnectionKind? kind = kinds.Find(row.Kind);
         SourceInfo? info = null;
         if (kind == null)
         {
            problem = $"The application has no kind of connection '{row.Kind}'";
         }
         else if (kind is ExcelKind)
         {
            runtimes.Add(new SourceRuntime(row.Alias, kind, null));
            // Its sheets load as its schema was read, until it is read again with the settings it has now.
            if (schema != null)
            {
               info = connections.Folder(row.Alias, snapshot!.ReadWith is { } readWith ? StoredConnections.Folder(readWith.Settings, readWith.Options) : row.Folder());
            }
         }
         else
         {
            runtimes.Add(Runtime(row, kind));
            if (schema != null) { info = row.Source(schema.DefaultSchema); }
         }
         if (info != null)
         {
            builder.AddSource(info, schema!);
            inputs.Add((info, schema!));
         }
         sources.Add(new CatalogSource(row.Id, row.Alias, row.Kind, row.DisplayName, row.SchemaStatus, row.SchemaRefreshedAt, kind?.AlwaysReadOnly == true || row.IsReadOnly)
         {
            HasSchema = info != null,
            Problem = problem,
         });
         Append(version, row.Id, row.Alias, row.Kind, row.Version, row.SchemaStatus, row.SchemaRefreshedAt?.Ticks, hasHead ? head.Id : null,
            hasHead ? head.CheckedAt.Ticks : null);
      }
      schemas = used;
      foreach ((OverlayItemKind kind, IOverlayItem item) in overlay.All()) { Append(version, kind, item.Id, item.Version); }

      QueryCatalog catalog = builder.WithOverlay(overlay.Overlay).Build();
      CatalogTree tree = CatalogTree.Build(catalog, sources);
      string hash = Convert.ToHexStringLower(version.GetHashAndReset())[..16];
      await connections.PublishAsync(runtimes);
      LogBuilt(logger, hash, sources.Count, catalog.Entities.Count, overlay.Count, clock.GetElapsedTime(started).TotalMilliseconds);
      HashSet<string> unreadSources = sources.Where(s => !s.HasSchema).Select(s => s.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);
      OverlayIssues issues = OverlayIssues.Of(catalog, overlay, unreadSources);
      Broken(issues, Volatile.Read(ref current)?.Issues);
      return new CatalogState(generation, hash, clock.GetUtcNow().UtcDateTime, catalog, sources, tree, overlay, issues, inputs);
   }

   /// <summary>Tells of overlay items that stopped working since the last build (a column gone, a source deleted), or that never did, at the first.</summary>
   private void Broken(OverlayIssues issues, OverlayIssues? before)
   {
      HashSet<(OverlayItemKind, int)> known = before?.Broken.ToHashSet() ?? [];
      foreach ((OverlayItemKind kind, int id) in issues.Broken.Where(b => !known.Contains(b)))
      {
         LogBroken(logger, kind, id, issues.For(kind, id).First(d => d.Severity == Query.Diagnostics.DiagnosticSeverity.Error).Message);
      }
   }

   private bool Cached(long id, DateTime checkedAt) => schemas.TryGetValue(id, out Snapshot? cached) && cached.CheckedAt == checkedAt;

   private Snapshot Read(long id, DateTime checkedAt, byte[] data, string? readWith, string alias)
   {
      try
      {
         return new Snapshot(checkedAt, SchemaSnapshots.Read(data), SchemaSnapshots.ReadWith(readWith));
      }
      catch (Exception e) when (e is JsonException or InvalidDataException)
      {
         LogDamaged(logger, e, alias, id);
         return new Snapshot(checkedAt, null, null);
      }
   }

   /// <summary>A snapshot as read: its schema (null when its data is damaged), and what it was read with.</summary>
   private sealed record Snapshot(DateTime CheckedAt, SourceSchema? Schema, SnapshotSource? ReadWith);

   /// <summary>How the source connects: its connection string with its secrets, or why it can't.</summary>
   private SourceRuntime Runtime(SourceConnection row, ConnectionKind kind)
   {
      Dictionary<string, string>? stored = secrets.Unprotect(row.Alias, row.ProtectedSecrets);
      if (stored == null) { return new SourceRuntime(row.Alias, kind, null, StoredConnections.UnreadableSecrets); }
      try
      {
         return new SourceRuntime(row.Alias, kind, kind.ConnectionString(row.Settings(), stored, row.IsReadOnly));
      }
      catch (Exception e) when (e is ArgumentException or FormatException or InvalidCastException or KeyNotFoundException)
      {
         return new SourceRuntime(row.Alias, kind, null, "Its settings can't be made into a connection string; edit them");
      }
   }

   private static void Append(IncrementalHash hash, params object?[] values)
   {
      foreach (object? value in values)
      {
         hash.AppendData(Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "\u0001"));
         hash.AppendData([0]);
      }
   }

   [LoggerMessage(Level = LogLevel.Debug, Message = "Built the catalog {Version}: {Sources} sources, {Entities} entities, {OverlayItems} overlay items, in {Elapsed:0} ms")]
   private static partial void LogBuilt(ILogger logger, string version, int sources, int entities, int overlayItems, double elapsed);

   [LoggerMessage(Level = LogLevel.Warning, Message = "The overlay's {Kind} {Id} doesn't work: {Problem}")]
   private static partial void LogBroken(ILogger logger, OverlayItemKind kind, int id, string problem);

   [LoggerMessage(Level = LogLevel.Error, Message = "The schema snapshot {SnapshotId} of {Alias} can't be read")]
   private static partial void LogDamaged(ILogger logger, Exception exception, string alias, long snapshotId);
}
