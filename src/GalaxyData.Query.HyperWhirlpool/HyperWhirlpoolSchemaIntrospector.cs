using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Providers;

namespace GalaxyData.Query.HyperWhirlpool;

/// <summary>
/// Reads a HyperWhirlpool warehouse's schema from its schema document: the latest row of its catalog's <c>schema</c>
/// table, which the warehouse writes when a version of its model goes live, read over the connection as any query is.
/// The document names the public views of every domain (each domain's database a schema, the connection's own the
/// default), their keys, and their references to each other, which become foreign keys the warehouse doesn't enforce.
/// Nothing of the warehouse's own tables is read, or could be: the query service lets its keys read only the views
/// and the catalog.
/// </summary>
/// <remarks>
/// The catalog is <c>hw_catalog</c>, unless the warehouse names another (a warehouse sharing its ClickHouse server
/// with others): then it is the one database with a table of the catalog's shape that the key may read, as a key sees
/// only what it is granted.
/// </remarks>
public sealed class HyperWhirlpoolSchemaIntrospector : ISchemaIntrospector
{
   public const string DefaultCatalog = "hw_catalog";

   private readonly string? catalog;

   public HyperWhirlpoolSchemaIntrospector() { }

   /// <summary>Reads the document from the database <paramref name="catalog"/> (a test's own) instead of the one the connection finds.</summary>
   internal HyperWhirlpoolSchemaIntrospector(string catalog)
   {
      ArgumentException.ThrowIfNullOrEmpty(catalog);
      this.catalog = catalog;
   }

   public async Task<SourceSchema> IntrospectAsync(DbConnection connection, IntrospectionOptions options, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(connection);
      ArgumentNullException.ThrowIfNull(options);
      if (connection.State != ConnectionState.Open) { await connection.OpenAsync(cancellationToken).ConfigureAwait(false); }
      string? version = await connection.ScalarTextAsync("SELECT version()", cancellationToken).ConfigureAwait(false);
      string defaultSchema = await connection.ScalarTextAsync("SELECT currentDatabase()", cancellationToken).ConfigureAwait(false) ?? "default";
      string? json = (catalog ?? await CatalogAsync(connection, cancellationToken).ConfigureAwait(false)) is { } found
         ? await LatestAsync(connection, found, cancellationToken).ConfigureAwait(false)
         : null;
      return json == null
         ? throw new InvalidDataException("The warehouse has no schema document yet: it writes one when a version of its model goes live")
         : SchemaDocument.Parse(json).ToSourceSchema(version, defaultSchema, options);
   }

   /// <summary>
   /// The catalog the connection may read: <c>hw_catalog</c> when it may, or else the one database with a table of the
   /// catalog's shape; null when there is none (no version of the model has gone live). <see cref="InvalidDataException"/>
   /// when there are several, as a login of the server itself may see.
   /// </summary>
   internal static async Task<string?> CatalogAsync(DbConnection connection, CancellationToken cancellationToken)
   {
      List<string> found = [];
      await foreach (DbDataReader row in connection.QueryAsync(
         "SELECT database FROM system.columns WHERE table = 'schema' AND name IN ('seq', 'hash', 'document') " +
         "GROUP BY database HAVING count() = 3 ORDER BY database", cancellationToken).ConfigureAwait(false))
      {
         found.Add(row.GetString(0));
      }
      return found.Contains(DefaultCatalog) ? DefaultCatalog : found switch
      {
         [] => null,
         [string one] => one,
         _ => throw new InvalidDataException($"The connection may read the schema catalogs of several warehouses ({string.Join(", ", found)}): connect through the warehouse's query service"),
      };
   }

   /// <summary>The latest schema document, as text; null when the warehouse has none.</summary>
   internal static Task<string?> LatestAsync(DbConnection connection, string catalog, CancellationToken cancellationToken) =>
      connection.ScalarTextAsync($"SELECT document FROM {Quote(catalog)}.\"schema\" ORDER BY seq DESC LIMIT 1", cancellationToken);

   /// <summary>The latest version's sequence number and the start of its hash, as <c>12 (3f9a0c1d2e4b)</c>; null when the warehouse has none.</summary>
   internal static async Task<string?> LatestVersionAsync(DbConnection connection, CancellationToken cancellationToken) =>
      await CatalogAsync(connection, cancellationToken).ConfigureAwait(false) is { } catalog
         ? await connection.ScalarTextAsync($"SELECT concat(toString(seq), ' (', substring(hash, 1, 12), ')') FROM {Quote(catalog)}.\"schema\" ORDER BY seq DESC LIMIT 1",
            cancellationToken).ConfigureAwait(false)
         : null;

   private static string Quote(string name) => "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
