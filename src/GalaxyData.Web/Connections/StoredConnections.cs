using System;
using System.Collections.Generic;
using System.Text.Json;
using GalaxyData.Connectors;
using GalaxyData.Query.Catalog;
using GalaxyData.Web.Metadata;

namespace GalaxyData.Web.Connections;

/// <summary>A connection as stored, read back: its settings and options, and the source the engine sees it as.</summary>
public static class StoredConnections
{
   public const string UnreadableSecrets = "The stored secrets can't be read: the keys that protected them are gone. Enter them again.";

   /// <summary>A JSON object of names and values, as settings and options are stored; names ignore case.</summary>
   public static Dictionary<string, string> Read(string json) =>
      new(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [], StringComparer.OrdinalIgnoreCase);

   public static Dictionary<string, string> Settings(this SourceConnection connection)
   {
      ArgumentNullException.ThrowIfNull(connection);
      return Read(connection.SettingsJson);
   }

   public static Dictionary<string, string> Options(this SourceConnection connection)
   {
      ArgumentNullException.ThrowIfNull(connection);
      return Read(connection.OptionsJson);
   }

   /// <summary>
   /// The engine's source for a database connection of <paramref name="kind"/>, whose schema's default schema is
   /// <paramref name="defaultSchema"/>: read-only or not, and as its options set it.
   /// </summary>
   public static SourceInfo Source(this SourceConnection connection, ConnectionKind kind, string defaultSchema)
   {
      ArgumentNullException.ThrowIfNull(connection);
      ArgumentNullException.ThrowIfNull(kind);
      return kind.Configure(new SourceInfo(connection.Alias, connection.Kind, defaultSchema) { IsReadOnly = connection.IsReadOnly }, connection.Options());
   }
}
