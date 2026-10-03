using System;
using System.Collections.Generic;
using System.Text.Json;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Excel;
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

   /// <summary>The engine's source for a database connection, whose schema's default schema is <paramref name="defaultSchema"/>.</summary>
   public static SourceInfo Source(this SourceConnection connection, string defaultSchema)
   {
      ArgumentNullException.ThrowIfNull(connection);
      Dictionary<string, string> options = connection.Options();
      return new SourceInfo(connection.Alias, connection.Kind, defaultSchema)
      {
         IsReadOnly = connection.IsReadOnly,
         TrustForeignKeys = Flag(options, ConnectionKind.TrustForeignKeysOption) ?? false,
         EnforceForeignKeys = Flag(options, SqliteKind.EnforceForeignKeysOption),
      };
   }

   /// <summary>How a connection's folder of workbooks reads.</summary>
   public static ExcelFolderOptions Folder(this SourceConnection connection)
   {
      ArgumentNullException.ThrowIfNull(connection);
      return Folder(connection.Settings(), connection.Options());
   }

   /// <summary>How a folder of workbooks reads with these settings and options.</summary>
   public static ExcelFolderOptions Folder(IReadOnlyDictionary<string, string> settings, Dictionary<string, string> options)
   {
      ArgumentNullException.ThrowIfNull(settings);
      ArgumentNullException.ThrowIfNull(options);
      return new ExcelFolderOptions
      {
         Path = settings[ExcelKind.Folder],
         HeaderRow = Flag(options, ExcelKind.HeaderRowOption) ?? true,
         AllText = Flag(options, ExcelKind.AllTextOption) ?? false,
         IncludeHiddenSheets = Flag(options, ExcelKind.IncludeHiddenSheetsOption) ?? false,
      };
   }

   private static bool? Flag(Dictionary<string, string> options, string name) =>
      options.TryGetValue(name, out string? value) && bool.TryParse(value, out bool flag) ? flag : null;
}
