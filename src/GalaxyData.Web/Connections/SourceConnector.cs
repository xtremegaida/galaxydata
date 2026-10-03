using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace GalaxyData.Web.Connections;

/// <summary>
/// Opens connections to a source with one connection string, pooled as the provider pools them. Disposed when no
/// source connects with the string any more (its settings changed, or it was deleted), which lets the pool go.
/// </summary>
public abstract class SourceConnector : IAsyncDisposable
{
   /// <summary>An open connection, which the caller disposes.</summary>
   public abstract ValueTask<DbConnection> OpenAsync(CancellationToken cancellationToken);

   public abstract ValueTask DisposeAsync();
}

/// <summary>
/// Opens connections the provider makes from the connection string; <paramref name="clearPool"/> lets its pool for the
/// string go. Once disposed it opens none (<see cref="ObjectDisposedException"/>, as a data source does), so a
/// connection opened as its source's settings change is opened with the new ones.
/// </summary>
public sealed class ProviderConnector(Func<DbConnection> create, Action? clearPool = null) : SourceConnector
{
   private volatile bool disposed;

   public override async ValueTask<DbConnection> OpenAsync(CancellationToken cancellationToken)
   {
      ObjectDisposedException.ThrowIf(disposed, this);
      DbConnection connection = create();
      try
      {
         await connection.OpenAsync(cancellationToken);
         ObjectDisposedException.ThrowIf(disposed, this);
         return connection;
      }
      catch
      {
         await connection.DisposeAsync();
         throw;
      }
   }

   public override ValueTask DisposeAsync()
   {
      disposed = true;
      clearPool?.Invoke();
      return ValueTask.CompletedTask;
   }
}

/// <summary>Opens connections from a data source (PostgreSQL's, which reads the types queries need), disposed with it.</summary>
public sealed class DataSourceConnector(DbDataSource source) : SourceConnector
{
   public override ValueTask<DbConnection> OpenAsync(CancellationToken cancellationToken) => source.OpenConnectionAsync(cancellationToken);

   public override ValueTask DisposeAsync() => source.DisposeAsync();
}
