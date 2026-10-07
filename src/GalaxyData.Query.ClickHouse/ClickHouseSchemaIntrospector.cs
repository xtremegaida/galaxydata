using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Providers;

namespace GalaxyData.Query.ClickHouse;

/// <summary>
/// Reads a ClickHouse server from <c>system.tables</c> and <c>system.columns</c>: the tables and views of the
/// connection's database, as a connection to another server reads its database's; or, when the options name schemas,
/// of the databases they name (each database a schema, the connection's own the default). ClickHouse has no foreign
/// keys, and its primary key is the start of the sorting key, which needn't be unique: tables are read with no key
/// (the overlay can declare one), and the sorting key as an index that isn't unique, to tell how the table is ordered.
/// Dictionaries, the tables behind materialized views and ephemeral columns (which can't be read) are left out.
/// Row counts are the server's own, exact for MergeTree tables.
/// </summary>
public sealed class ClickHouseSchemaIntrospector : ISchemaIntrospector
{
   public const string ProviderKind = "clickhouse";

   private static readonly string[] SystemSchemas = ["system", "INFORMATION_SCHEMA", "information_schema"];

   private const string Databases = "database NOT IN ('system', 'INFORMATION_SCHEMA', 'information_schema')";

   public async Task<SourceSchema> IntrospectAsync(DbConnection connection, IntrospectionOptions options, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(connection);
      ArgumentNullException.ThrowIfNull(options);
      if (connection.State != ConnectionState.Open) { await connection.OpenAsync(cancellationToken).ConfigureAwait(false); }
      return await ReadAsync(connection, options, cancellationToken).ConfigureAwait(false);
   }

   private static async Task<SourceSchema> ReadAsync(DbConnection connection, IntrospectionOptions options, CancellationToken ct)
   {
      string? version = await connection.ScalarTextAsync("SELECT version()", ct).ConfigureAwait(false);
      string defaultSchema = await connection.ScalarTextAsync("SELECT currentDatabase()", ct).ConfigureAwait(false) ?? "default";
      // The connection's database, unless others are named: a server's other databases are other sources.
      string where = options.IncludeSchemas is { Count: > 0 } ? options.IncludeSystemObjects ? "1" : Databases : "database = currentDatabase()";

      Dictionary<(string, string), Builder> tables = [];
      await foreach (DbDataReader row in connection.QueryAsync(
         "SELECT database, name, engine, comment, total_rows, sorting_key FROM system.tables " +
         $"WHERE {where} AND NOT is_temporary AND engine NOT IN ('Dictionary', 'LiveView', 'WindowView') AND NOT startsWith(name, '.inner') " +
         "ORDER BY database, name", ct).ConfigureAwait(false))
      {
         string schema = row.GetString(0);
         if (!Included(schema, options)) { continue; }
         TableKind kind = row.GetString(2) switch
         {
            "View" => TableKind.View,
            "MaterializedView" => TableKind.MaterializedView,
            _ => TableKind.Table,
         };
         if (kind != TableKind.Table && !options.IncludeViews) { continue; }
         string comment = row.GetString(3);
         string sortingKey = row.GetString(5);
         tables[(schema, row.GetString(1))] = new Builder(schema, row.GetString(1), kind)
         {
            RowCountEstimate = options.IncludeRowCountEstimates && kind == TableKind.Table && !row.IsDBNull(4) ? Convert.ToInt64(row.GetValue(4), CultureInfo.InvariantCulture) : null,
            Comment = comment.Length == 0 ? null : comment,
            SortingKey = sortingKey.Length == 0 ? null : Expressions(sortingKey),
         };
      }
      if (tables.Count == 0) { return new SourceSchema(ProviderKind, version, defaultSchema, []); }

      await foreach (DbDataReader row in connection.QueryAsync(
         "SELECT database, table, name, type, default_kind, default_expression, comment FROM system.columns " +
         $"WHERE {where} ORDER BY database, table, position", ct).ConfigureAwait(false))
      {
         if (!tables.TryGetValue((row.GetString(0), row.GetString(1)), out Builder? table)) { continue; }
         string defaultKind = row.GetString(4);
         if (defaultKind == "EPHEMERAL") { continue; }
         string nativeType = row.GetString(3);
         string defaultSql = row.GetString(5);
         string comment = row.GetString(6);
         bool computed = defaultKind is "MATERIALIZED" or "ALIAS";
         table.Columns.Add(new ColumnSchema(row.GetString(2), table.Columns.Count, nativeType, ClickHouseTypeMapper.Map(nativeType))
         {
            ReadAs = ClickHouseTypeMapper.ReadAs(nativeType),
            IsComputed = computed,
            DefaultSql = computed || defaultSql.Length == 0 ? null : defaultSql,
            Comment = comment.Length == 0 ? null : comment,
         });
      }

      List<TableSchema> result = tables.Values
         .OrderBy(t => t.Schema, StringComparer.Ordinal)
         .ThenBy(t => t.Name, StringComparer.Ordinal)
         .Select(t => t.Build())
         .ToList();
      return new SourceSchema(ProviderKind, version, defaultSchema, result);
   }

   private static bool Included(string schema, IntrospectionOptions options)
   {
      if (!options.IncludeSystemObjects && SystemSchemas.Contains(schema, StringComparer.Ordinal)) { return false; }
      if (options.IncludeSchemas is { Count: > 0 } include && !include.Contains(schema, StringComparer.OrdinalIgnoreCase)) { return false; }
      return options.ExcludeSchemas is not { Count: > 0 } exclude || !exclude.Contains(schema, StringComparer.OrdinalIgnoreCase);
   }

   /// <summary>A key's expressions, split at the commas outside parentheses and quotes: <c>customer_id, toYYYYMM(placed_at)</c>.</summary>
   private static List<string> Expressions(string key)
   {
      List<string> parts = [];
      int depth = 0;
      bool quoted = false;
      int start = 0;
      for (int i = 0; i < key.Length; i++)
      {
         char c = key[i];
         if (c == '\'' && (i == 0 || key[i - 1] != '\\')) { quoted = !quoted; }
         else if (!quoted && c == '(') { depth++; }
         else if (!quoted && c == ')') { depth--; }
         else if (!quoted && depth == 0 && c == ',')
         {
            parts.Add(key[start..i].Trim());
            start = i + 1;
         }
      }
      parts.Add(key[start..].Trim());
      return parts;
   }

   private sealed class Builder(string schema, string name, TableKind kind)
   {
      public string Schema { get; } = schema;
      public string Name { get; } = name;
      public List<ColumnSchema> Columns { get; } = [];
      public long? RowCountEstimate { get; init; }
      public string? Comment { get; init; }
      public List<string>? SortingKey { get; init; }

      /// <summary>The sorting key is how the table is kept, not a key: an index that isn't unique.</summary>
      public TableSchema Build() => new(Schema, Name, kind, Columns)
      {
         Indexes = SortingKey == null ? [] : [new IndexSchema("sorting key", SortingKey, IsUnique: false)],
         RowCountEstimate = RowCountEstimate,
         Comment = Comment,
      };
   }
}
