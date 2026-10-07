using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Connectors;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Introspection;
using GalaxyData.Web.Connections;
using GalaxyData.Web.Metadata;

namespace GalaxyData.Web.Schemas;

/// <summary>Reading a connection's schema failed; the message is the database's (its secrets masked), or why it wasn't tried.</summary>
public sealed class SchemaReadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads a connection's schema: connects as queries do (read-only or not, as the connection is set, since DuckDB
/// opens a file one way at a time) and introspects. A source its connector opens itself (a folder of workbooks) is
/// read under a name of its own, so what is loaded for queries stays as it is.
/// </summary>
public sealed class SchemaReader(ConnectorSet connectors, ConnectionSecrets secrets)
{
   /// <summary>The schema; <see cref="SchemaReadException"/> when it can't be read.</summary>
   public async Task<SourceSchema> ReadAsync(SourceConnection connection, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(connection);
      Connector connector = connectors.Find(connection.Kind) ?? throw new SchemaReadException($"The application has no kind of connection '{connection.Kind}'");
      ConnectionKind kind = connector.Kind;
      Dictionary<string, string> stored = secrets.Unprotect(connection.Alias, connection.ProtectedSecrets) ?? throw new SchemaReadException(StoredConnections.UnreadableSecrets);
      Dictionary<string, string> settings = connection.Settings();
      if (kind.Missing(settings) is { } missing) { throw new SchemaReadException(missing); }
      try
      {
         return connector.Attached is { } attached
            ? await attached.ReadSchemaAsync(settings, connection.Options(), cancellationToken)
            : await ReadDatabaseAsync(connection, connector, settings, stored, cancellationToken);
      }
      catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException or SchemaReadException))
      {
         throw new SchemaReadException(ConnectionStrings.Scrub(e.Message, stored.Values), e);
      }
   }

   private static async Task<SourceSchema> ReadDatabaseAsync(SourceConnection connection, Connector connector, Dictionary<string, string> settings,
                                                             Dictionary<string, string> stored, CancellationToken cancellationToken)
   {
      ConnectionKind kind = connector.Kind;
      SourceProvider provider = connector.Provider;
      await using SourceConnector opener = kind.OneOffConnector(kind.ConnectionString(settings, stored, connection.IsReadOnly));
      await using DbConnection opened = await opener.OpenAsync(cancellationToken);
      await provider.PrepareConnectionAsync(opened, cancellationToken);
      return await provider.Introspector.IntrospectAsync(opened, IntrospectionOptions.Default, cancellationToken);
   }
}
