using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Providers;

namespace GalaxyData.Query.DuckDb;

/// <summary>
/// Reads a DuckDB database from the <c>duckdb_*()</c> catalog functions. DuckDB foreign keys always refer to a
/// table in the same schema, and it enforces them, so they are reported as enforced.
/// </summary>
public sealed class DuckDbSchemaIntrospector : ISchemaIntrospector
{
   public const string ProviderKind = "duckdb";

   private static readonly string[] SystemSchemas = ["information_schema", "pg_catalog"];

   private readonly string? database;

   /// <param name="database">The attached database to read; the connection's current database when null.</param>
   public DuckDbSchemaIntrospector(string? database = null)
   {
      this.database = database;
   }

   public async Task<SourceSchema> IntrospectAsync(DbConnection connection, IntrospectionOptions options, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(connection);
      ArgumentNullException.ThrowIfNull(options);
      bool opened = false;
      if (connection.State != ConnectionState.Open)
      {
         await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
         opened = true;
      }
      try
      {
         return await ReadAsync(connection, options, cancellationToken).ConfigureAwait(false);
      }
      finally
      {
         if (opened) { await connection.CloseAsync().ConfigureAwait(false); }
      }
   }

   private async Task<SourceSchema> ReadAsync(DbConnection connection, IntrospectionOptions options, CancellationToken ct)
   {
      string? version = await connection.ScalarTextAsync("SELECT version()", ct).ConfigureAwait(false);
      string db = database ?? await connection.ScalarTextAsync("SELECT current_database()", ct).ConfigureAwait(false) ?? "memory";
      string defaultSchema = await connection.ScalarTextAsync("SELECT current_schema()", ct).ConfigureAwait(false) ?? "main";
      (string, object?) dbParameter = ("db", db);

      Dictionary<(string Schema, string Name), Builder> tables = [];
      await foreach (DbDataReader row in connection.QueryAsync(
         "SELECT schema_name, table_name, estimated_size, comment FROM duckdb_tables() " +
         "WHERE database_name = $db AND NOT internal AND NOT temporary ORDER BY schema_name, table_name", ct, dbParameter).ConfigureAwait(false))
      {
         string schema = row.GetString(0);
         if (!Included(schema, options)) { continue; }
         tables[(schema, row.GetString(1))] = new Builder(schema, row.GetString(1), TableKind.Table)
         {
            RowCountEstimate = options.IncludeRowCountEstimates && !row.IsDBNull(2) ? row.GetInt64(2) : null,
            Comment = row.IsDBNull(3) ? null : row.GetString(3),
         };
      }
      if (options.IncludeViews)
      {
         await foreach (DbDataReader row in connection.QueryAsync(
            "SELECT schema_name, view_name, comment FROM duckdb_views() " +
            "WHERE database_name = $db AND NOT internal AND NOT temporary ORDER BY schema_name, view_name", ct, dbParameter).ConfigureAwait(false))
         {
            string schema = row.GetString(0);
            if (!Included(schema, options)) { continue; }
            tables[(schema, row.GetString(1))] = new Builder(schema, row.GetString(1), TableKind.View)
            {
               Comment = row.IsDBNull(2) ? null : row.GetString(2),
            };
         }
      }

      await foreach (DbDataReader row in connection.QueryAsync(
         "SELECT schema_name, table_name, column_name, column_index, is_nullable, column_default, data_type, " +
         "numeric_precision, numeric_scale, comment FROM duckdb_columns() " +
         "WHERE database_name = $db AND NOT internal ORDER BY schema_name, table_name, column_index", ct, dbParameter).ConfigureAwait(false))
      {
         if (!tables.TryGetValue((row.GetString(0), row.GetString(1)), out Builder? table)) { continue; }
         string? defaultSql = row.IsDBNull(5) ? null : row.GetString(5);
         string dataType = row.GetString(6);
         bool nullable = row.GetBoolean(4);
         table.Columns.Add(new ColumnSchema(row.GetString(2), row.GetInt32(3) - 1, dataType,
            DuckDbTypeMapper.Map(dataType, row.IsDBNull(7) ? null : row.GetInt32(7), row.IsDBNull(8) ? null : row.GetInt32(8), nullable))
         {
            DefaultSql = defaultSql,
            IsIdentity = defaultSql != null && defaultSql.StartsWith("nextval(", StringComparison.OrdinalIgnoreCase),
            Comment = row.IsDBNull(9) ? null : row.GetString(9),
         });
      }

      await foreach (DbDataReader row in connection.QueryAsync(
         "SELECT schema_name, table_name, constraint_type, constraint_name, constraint_column_names, referenced_table, " +
         "referenced_column_names FROM duckdb_constraints() WHERE database_name = $db AND constraint_type IN " +
         "('PRIMARY KEY', 'UNIQUE', 'FOREIGN KEY') ORDER BY schema_name, table_name, constraint_index", ct, dbParameter).ConfigureAwait(false))
      {
         if (!tables.TryGetValue((row.GetString(0), row.GetString(1)), out Builder? table)) { continue; }
         string? name = row.IsDBNull(3) ? null : row.GetString(3);
         List<string> columns = Strings(row.GetValue(4));
         switch (row.GetString(2))
         {
            case "PRIMARY KEY":
               table.PrimaryKey = new KeySchema(name, columns);
               break;
            case "UNIQUE":
               table.UniqueKeys.Add(new KeySchema(name, columns));
               break;
            case "FOREIGN KEY":
               table.ForeignKeys.Add(new ForeignKeySchema(name, columns, table.Schema, row.GetString(5), Strings(row.GetValue(6))));
               break;
         }
      }

      await foreach (DbDataReader row in connection.QueryAsync(
         "SELECT schema_name, table_name, index_name, is_unique, is_primary, expressions FROM duckdb_indexes() " +
         "WHERE database_name = $db ORDER BY schema_name, table_name, index_name", ct, dbParameter).ConfigureAwait(false))
      {
         if (!tables.TryGetValue((row.GetString(0), row.GetString(1)), out Builder? table)) { continue; }
         table.Indexes.Add(new IndexSchema(row.GetString(2), IndexColumns(row.IsDBNull(5) ? string.Empty : row.GetString(5)), row.GetBoolean(3))
         {
            IsPrimaryKey = row.GetBoolean(4),
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
      if (!options.IncludeSystemObjects && SystemSchemas.Contains(schema, StringComparer.OrdinalIgnoreCase)) { return false; }
      if (options.IncludeSchemas is { Count: > 0 } include && !include.Contains(schema, StringComparer.OrdinalIgnoreCase)) { return false; }
      return options.ExcludeSchemas is not { Count: > 0 } exclude || !exclude.Contains(schema, StringComparer.OrdinalIgnoreCase);
   }

   private static List<string> Strings(object value)
   {
      if (value is DBNull or null) { return []; }
      if (value is string single) { return [single]; }
      return value is IEnumerable items ? items.Cast<object?>().Select(o => o?.ToString() ?? string.Empty).ToList() : [];
   }

   /// <summary>Splits DuckDB's rendering of index expressions, <c>[a, "b c"]</c>, into names.</summary>
   internal static List<string> IndexColumns(string expressions)
   {
      string text = expressions.Trim();
      if (text.StartsWith('[') && text.EndsWith(']')) { text = text[1..^1]; }
      List<string> columns = [];
      StringBuilder current = new();
      bool quoted = false;
      int depth = 0;
      for (int i = 0; i < text.Length; i++)
      {
         char c = text[i];
         if (quoted)
         {
            if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { current.Append('"'); i++; }
            else if (c == '"') { quoted = false; }
            else { current.Append(c); }
            continue;
         }
         switch (c)
         {
            case ' ' when current.Length == 0:
               break;
            case '"' when current.Length == 0:
               quoted = true;
               break;
            case '(':
               depth++;
               current.Append(c);
               break;
            case ')':
               depth--;
               current.Append(c);
               break;
            case ',' when depth == 0:
               columns.Add(current.ToString().Trim());
               current.Clear();
               break;
            default:
               current.Append(c);
               break;
         }
      }
      if (current.Length > 0) { columns.Add(current.ToString().Trim()); }
      return columns;
   }

   private sealed class Builder(string schema, string name, TableKind kind)
   {
      public string Schema { get; } = schema;
      public string Name { get; } = name;
      public List<ColumnSchema> Columns { get; } = [];
      public KeySchema? PrimaryKey { get; set; }
      public List<KeySchema> UniqueKeys { get; } = [];
      public List<ForeignKeySchema> ForeignKeys { get; } = [];
      public List<IndexSchema> Indexes { get; } = [];
      public long? RowCountEstimate { get; init; }
      public string? Comment { get; init; }

      public TableSchema Build() => new(Schema, Name, kind, Columns)
      {
         PrimaryKey = PrimaryKey,
         UniqueKeys = UniqueKeys,
         Indexes = Indexes,
         ForeignKeys = ForeignKeys,
         RowCountEstimate = RowCountEstimate,
         Comment = Comment,
      };
   }
}
