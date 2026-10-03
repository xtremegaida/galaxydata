using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using GalaxyData.Query.Introspection;
using GalaxyData.Web.Catalog;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GalaxyData.Web.Schemas;

/// <summary>
/// Connections whose schemas are to be read. A connection asked for again while it waits is read once; asked for
/// while it is being read, it is read again after, so the newest settings are always the ones read.
/// </summary>
public sealed class SchemaRefreshQueue
{
   private readonly Channel<int> channel = Channel.CreateUnbounded<int>();
   private readonly ConcurrentDictionary<int, byte> waiting = new();

   public void Enqueue(int connectionId)
   {
      if (waiting.TryAdd(connectionId, 0)) { channel.Writer.TryWrite(connectionId); }
   }

   internal IAsyncEnumerable<int> ReadAllAsync(CancellationToken cancellationToken) => channel.Reader.ReadAllAsync(cancellationToken);

   /// <summary>The connection is being read: asking again reads it again.</summary>
   internal void Started(int connectionId) => waiting.TryRemove(connectionId, out _);
}

/// <summary>
/// Reads a connection's schema and keeps it (<see cref="SchemaSnapshots"/>). Its status says how it went:
/// <see cref="SchemaStatus.Loading"/> while it is read, then <see cref="SchemaStatus.Ready"/>, or
/// <see cref="SchemaStatus.Failed"/> with why (a schema read before is still used). Statuses are written with
/// <c>ExecuteUpdate</c>, which leaves the connection's version as it was, so an administrator editing it isn't in
/// conflict with its being read. The catalog is built again after.
/// </summary>
public sealed partial class SchemaRefresher(IServiceScopeFactory scopes, SchemaReader reader, CatalogService catalog, IOptions<GalaxyDataOptions> options,
                                            TimeProvider clock, ILogger<SchemaRefresher> logger)
{
   public const int MaxErrorLength = 2000;

   /// <summary>Marks a connection's schema as being read, or waiting to be; 0 when there is no such connection.</summary>
   public static Task<int> MarkLoadingAsync(MetadataDb db, int connectionId, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(db);
      return db.Connections.Where(c => c.Id == connectionId).ExecuteUpdateAsync(s => s.SetProperty(c => c.SchemaStatus, SchemaStatus.Loading), cancellationToken);
   }

   /// <summary>Reads the connection's schema; only the application stopping is thrown.</summary>
   public async Task RefreshAsync(int connectionId, CancellationToken cancellationToken)
   {
      await using AsyncServiceScope scope = scopes.CreateAsyncScope();
      MetadataDb db = scope.ServiceProvider.GetRequiredService<MetadataDb>();
      SourceConnection? connection = await db.Connections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
      if (connection == null) { return; }
      await MarkLoadingAsync(db, connectionId, cancellationToken);
      catalog.Invalidate();
      TimeSpan limit = options.Value.Connections.RefreshTimeout;
      using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeout.CancelAfter(limit);
      string? error = null;
      Exception? unexpected = null;
      try
      {
         // A provider that doesn't heed the token is left to finish alone.
         SourceSchema schema = await reader.ReadAsync(connection, timeout.Token).WaitAsync(timeout.Token);
         DateTime now = clock.GetUtcNow().UtcDateTime;
         bool added = await SchemaSnapshots.SaveAsync(db, connection, schema, now, cancellationToken);
         await db.Connections.Where(c => c.Id == connectionId).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.SchemaStatus, SchemaStatus.Ready)
            .SetProperty(c => c.SchemaError, (string?)null)
            .SetProperty(c => c.SchemaRefreshedAt, now), cancellationToken);
         LogRead(logger, connection.Alias, schema.Tables.Count, added ? "a new snapshot" : "as it was");
      }
      catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
      {
         error = $"Reading the schema took longer than {limit}";
      }
      catch (SchemaReadException e)
      {
         error = e.Message;
      }
      catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
      {
         unexpected = e;
      }
      finally
      {
         catalog.Invalidate();
      }
      if (unexpected != null)
      {
         // Deleted while it was read: there is nothing to keep.
         if (!await db.Connections.AnyAsync(c => c.Id == connectionId, cancellationToken)) { return; }
         LogUnexpected(logger, unexpected, connection.Alias);
         error = "Reading the schema failed unexpectedly; the application's log says why";
      }
      if (error == null) { return; }
      LogFailed(logger, connection.Alias, error);
      string kept = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
      await db.Connections.Where(c => c.Id == connectionId).ExecuteUpdateAsync(s => s
         .SetProperty(c => c.SchemaStatus, SchemaStatus.Failed)
         .SetProperty(c => c.SchemaError, kept), cancellationToken);
      catalog.Invalidate();
   }

   [LoggerMessage(Level = LogLevel.Information, Message = "Read the schema of {Alias}: {Tables} tables and views, {Kept}")]
   private static partial void LogRead(ILogger logger, string alias, int tables, string kept);

   [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the schema of {Alias} failed: {Reason}")]
   private static partial void LogFailed(ILogger logger, string alias, string reason);

   [LoggerMessage(Level = LogLevel.Error, Message = "Reading the schema of {Alias} failed unexpectedly")]
   private static partial void LogUnexpected(ILogger logger, Exception exception, string alias);
}

/// <summary>
/// Reads the schemas queued (<see cref="SchemaRefreshQueue"/>), a few at a time, and each connection's one at a time:
/// a connection asked for while it is read is read again after, without taking a turn from the others meanwhile. At
/// startup it queues the connections never read, and those being read when the application stopped.
/// </summary>
public sealed partial class SchemaRefreshWorker(SchemaRefreshQueue queue, SchemaRefresher refresher, IServiceScopeFactory scopes, IOptions<GalaxyDataOptions> options,
                                                ILogger<SchemaRefreshWorker> logger) : BackgroundService
{
   private readonly Lock gate = new();

   /// <summary>The connections being read, and whether each is to be read again after.</summary>
   private readonly Dictionary<int, bool> reading = [];

   protected override async Task ExecuteAsync(CancellationToken stoppingToken)
   {
      try
      {
         try
         {
            await RequeueAsync(stoppingToken);
         }
         catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
         {
            // Those left unread are read when next asked for, or at the next start.
            LogRequeueFailed(logger, e);
         }
         ParallelOptions parallel = new() { MaxDegreeOfParallelism = options.Value.Connections.ParallelRefreshes, CancellationToken = stoppingToken };
         await Parallel.ForEachAsync(queue.ReadAllAsync(stoppingToken), parallel, ReadAsync);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
         // Stopping; schemas being read are read again at the next start.
      }
   }

   private async ValueTask ReadAsync(int connectionId, CancellationToken cancellationToken)
   {
      lock (gate)
      {
         queue.Started(connectionId);
         if (reading.ContainsKey(connectionId))
         {
            reading[connectionId] = true;
            return;
         }
         reading[connectionId] = false;
      }
      try
      {
         await refresher.RefreshAsync(connectionId, cancellationToken);
      }
      catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
      {
         // The metadata database couldn't be read or written: the connection stays as it was, and the others go on.
         LogFailed(logger, e, connectionId);
      }
      finally
      {
         bool again;
         lock (gate)
         {
            again = reading[connectionId];
            reading.Remove(connectionId);
         }
         if (again && !cancellationToken.IsCancellationRequested) { queue.Enqueue(connectionId); }
      }
   }

   private async Task RequeueAsync(CancellationToken cancellationToken)
   {
      await using AsyncServiceScope scope = scopes.CreateAsyncScope();
      MetadataDb db = scope.ServiceProvider.GetRequiredService<MetadataDb>();
      List<int> ids = await db.Connections.Where(c => c.SchemaStatus == SchemaStatus.NotLoaded || c.SchemaStatus == SchemaStatus.Loading)
         .OrderBy(c => c.Id).Select(c => c.Id).ToListAsync(cancellationToken);
      foreach (int id in ids) { queue.Enqueue(id); }
   }

   [LoggerMessage(Level = LogLevel.Error, Message = "Reading the schema of connection {ConnectionId} failed")]
   private static partial void LogFailed(ILogger logger, Exception exception, int connectionId);

   [LoggerMessage(Level = LogLevel.Error, Message = "The connections whose schemas weren't read couldn't be queued")]
   private static partial void LogRequeueFailed(ILogger logger, Exception exception);
}
