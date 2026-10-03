using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Introspection;
using GalaxyData.Web.Catalog;
using GalaxyData.Web.Connections;
using GalaxyData.Web.Metadata;

namespace GalaxyData.Web.Schemas;

/// <summary>Reading a connection's schema failed; the message is the database's (its secrets masked), or why it wasn't tried.</summary>
public sealed class SchemaReadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads a connection's schema: connects as queries do (read-only or not, as the connection is set, since DuckDB
/// opens a file one way at a time) and introspects. A folder of workbooks is read under a name of its own, so the
/// sheets loaded for queries stay as they are.
/// </summary>
public sealed class SchemaReader(ConnectionKinds kinds, SourceProviders providers, ConnectionSecrets secrets)
{
   private static long folders;

   /// <summary>The schema; <see cref="SchemaReadException"/> when it can't be read.</summary>
   public async Task<SourceSchema> ReadAsync(SourceConnection connection, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(connection);
      ConnectionKind kind = kinds.Find(connection.Kind) ?? throw new SchemaReadException($"The application has no kind of connection '{connection.Kind}'");
      Dictionary<string, string> stored = secrets.Unprotect(connection.Alias, connection.ProtectedSecrets) ?? throw new SchemaReadException(StoredConnections.UnreadableSecrets);
      Dictionary<string, string> settings = connection.Settings();
      if (kind.Missing(settings) is { } missing) { throw new SchemaReadException(missing); }
      try
      {
         return kind is ExcelKind ? await ReadFolderAsync(connection, cancellationToken) : await ReadDatabaseAsync(connection, kind, settings, stored, cancellationToken);
      }
      catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException or SchemaReadException))
      {
         throw new SchemaReadException(ConnectionStrings.Scrub(e.Message, stored.Values), e);
      }
   }

   private async Task<SourceSchema> ReadDatabaseAsync(SourceConnection connection, ConnectionKind kind, Dictionary<string, string> settings,
                                                      Dictionary<string, string> stored, CancellationToken cancellationToken)
   {
      SourceProvider provider = providers.For(kind.Id);
      await using SourceConnector connector = kind.OneOffConnector(kind.ConnectionString(settings, stored, connection.IsReadOnly));
      await using DbConnection opened = await connector.OpenAsync(cancellationToken);
      await provider.PrepareConnectionAsync(opened, cancellationToken);
      return await provider.Introspector.IntrospectAsync(opened, IntrospectionOptions.Default, cancellationToken);
   }

   private async Task<SourceSchema> ReadFolderAsync(SourceConnection connection, CancellationToken cancellationToken)
   {
      // A name no alias has (aliases have no '-'), so a source's own registration stays as it is.
      string name = "schema-" + Interlocked.Increment(ref folders).ToString(CultureInfo.InvariantCulture);
      providers.Excel.AddFolder(name, connection.Folder());
      try
      {
         return await providers.Excel.IntrospectAsync(name, IntrospectionOptions.Default, cancellationToken);
      }
      finally
      {
         providers.Excel.RemoveFolder(name);
      }
   }
}
