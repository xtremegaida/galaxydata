using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Providers;

namespace GalaxyData.Query.SqlServer;

/// <summary>
/// Reads a SQL Server database from the <c>sys</c> catalog views: the user tables and views the login can see, in
/// every schema. An alias type (and <c>sysname</c>) is read as the system type it is based on. Disabled indexes, and
/// the keys they back, are left out. Foreign keys that are disabled or not
/// trusted (added or re-enabled <c>WITH NOCHECK</c>) are reported as not enforced. Row counts are those of the
/// table's partitions. A column's collation is given when it isn't the database's.
/// </summary>
public sealed class SqlServerSchemaIntrospector : ISchemaIntrospector
{
   public const string ProviderKind = "sqlserver";

   private static readonly string[] SystemSchemas = ["sys", "INFORMATION_SCHEMA"];

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

   private static async Task<SourceSchema> ReadAsync(DbConnection connection, IntrospectionOptions options, CancellationToken ct)
   {
      string? version = await connection.ScalarTextAsync("SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128))", ct).ConfigureAwait(false);
      string defaultSchema = await connection.ScalarTextAsync("SELECT SCHEMA_NAME()", ct).ConfigureAwait(false) ?? "dbo";
      string? collation = await connection.ScalarTextAsync("SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128))", ct).ConfigureAwait(false);

      Dictionary<int, Builder> tables = [];
      string types = options.IncludeViews ? "'U', 'V'" : "'U'";
      string shipped = options.IncludeSystemObjects ? string.Empty : " AND o.is_ms_shipped = 0";
      await foreach (DbDataReader row in connection.QueryAsync(
         "SELECT o.object_id, s.name, o.name, o.type, " +
         "(SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = o.object_id AND p.index_id IN (0, 1)), " +
         "(SELECT CAST(e.value AS nvarchar(max)) FROM sys.extended_properties e WHERE e.class = 1 AND e.major_id = o.object_id AND e.minor_id = 0 AND e.name = 'MS_Description'), " +
         "CASE WHEN EXISTS (SELECT 1 FROM sys.triggers t WHERE t.parent_id = o.object_id) THEN 1 ELSE 0 END " +
         $"FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE o.type IN ({types}){shipped}", ct).ConfigureAwait(false))
      {
         string schema = row.GetString(1);
         if (!Included(schema, options)) { continue; }
         bool view = row.GetString(3).Trim() == "V";
         tables[row.GetInt32(0)] = new Builder(schema, row.GetString(2), view ? TableKind.View : TableKind.Table)
         {
            RowCountEstimate = options.IncludeRowCountEstimates && !view && !row.IsDBNull(4) ? row.GetInt64(4) : null,
            Comment = row.IsDBNull(5) ? null : row.GetString(5),
            HasTriggers = row.GetInt32(6) == 1,
         };
      }
      if (tables.Count == 0) { return new SourceSchema(ProviderKind, version, defaultSchema, []); }

      // Hidden columns (graph tables' internal ones, period columns declared HIDDEN) are left out.
      await foreach (DbDataReader row in connection.QueryAsync(
         "SELECT c.object_id, c.name, t.name, CASE WHEN t.is_assembly_type = 0 AND t.user_type_id <> t.system_type_id THEN TYPE_NAME(c.system_type_id) ELSE t.name END, " +
         "c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed, d.definition, c.collation_name, " +
         "(SELECT CAST(e.value AS nvarchar(max)) FROM sys.extended_properties e WHERE e.class = 1 AND e.major_id = c.object_id AND e.minor_id = c.column_id AND e.name = 'MS_Description') " +
         "FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id LEFT JOIN sys.default_constraints d ON d.object_id = c.default_object_id " +
         "WHERE c.is_hidden = 0 ORDER BY c.object_id, c.column_id", ct).ConfigureAwait(false))
      {
         if (!tables.TryGetValue(row.GetInt32(0), out Builder? table)) { continue; }
         string declaredType = row.GetString(2);
         string systemType = row.GetString(3);
         int maxLength = row.GetInt16(4);
         int precision = row.GetByte(5);
         int scale = row.GetByte(6);
         string? columnCollation = row.IsDBNull(11) ? null : row.GetString(11);
         bool alias = !string.Equals(declaredType, systemType, StringComparison.Ordinal);
         bool rowVersion = systemType == "timestamp";
         table.Columns.Add(new ColumnSchema(row.GetString(1), table.Columns.Count,
            alias ? declaredType : rowVersion ? "rowversion" : SqlServerTypeMapper.Declared(systemType, maxLength, precision, scale),
            SqlServerTypeMapper.Map(systemType, maxLength, precision, scale, row.GetBoolean(7)))
         {
            IsIdentity = row.GetBoolean(8),
            IsComputed = row.GetBoolean(9),
            IsRowVersion = rowVersion,
            DefaultSql = row.IsDBNull(10) ? null : row.GetString(10),
            Collation = string.Equals(columnCollation, collation, StringComparison.OrdinalIgnoreCase) ? null : columnCollation,
            Comment = row.IsDBNull(12) ? null : row.GetString(12),
         });
      }

      List<(int Table, string Name, string Type, string Column)> keyColumns = [];
      await foreach (DbDataReader row in connection.QueryAsync(
         "SELECT k.parent_object_id, k.name, k.type, c.name FROM sys.key_constraints k " +
         "JOIN sys.indexes i ON i.object_id = k.parent_object_id AND i.index_id = k.unique_index_id AND i.is_disabled = 0 " +
         "JOIN sys.index_columns ic ON ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id " +
         "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
         "WHERE ic.key_ordinal > 0 ORDER BY k.parent_object_id, k.name, ic.key_ordinal", ct).ConfigureAwait(false))
      {
         keyColumns.Add((row.GetInt32(0), row.GetString(1), row.GetString(2).Trim(), row.GetString(3)));
      }
      foreach (var key in keyColumns.GroupBy(k => (k.Table, k.Name)))
      {
         if (!tables.TryGetValue(key.Key.Table, out Builder? table)) { continue; }
         KeySchema schema = new(key.Key.Name, key.Select(k => k.Column).ToList());
         if (key.First().Type == "PK") { table.PrimaryKey = schema; }
         else { table.UniqueKeys.Add(schema); }
      }

      List<(int Table, string Name, bool Unique, bool Primary, string? Filter, string Column)> indexColumns = [];
      await foreach (DbDataReader row in connection.QueryAsync(
         "SELECT i.object_id, i.name, i.is_unique, i.is_primary_key, i.filter_definition, c.name FROM sys.indexes i " +
         "JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
         "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
         "WHERE i.type > 0 AND i.is_hypothetical = 0 AND i.is_disabled = 0 AND ic.key_ordinal > 0 ORDER BY i.object_id, i.name, ic.key_ordinal", ct).ConfigureAwait(false))
      {
         indexColumns.Add((row.GetInt32(0), row.GetString(1), row.GetBoolean(2), row.GetBoolean(3), row.IsDBNull(4) ? null : row.GetString(4), row.GetString(5)));
      }
      foreach (var index in indexColumns.GroupBy(i => (i.Table, i.Name)))
      {
         if (!tables.TryGetValue(index.Key.Table, out Builder? table)) { continue; }
         var first = index.First();
         table.Indexes.Add(new IndexSchema(index.Key.Name, index.Select(i => i.Column).ToList(), first.Unique)
         {
            IsPrimaryKey = first.Primary,
            Filter = first.Filter,
         });
      }

      List<(int Table, string Name, string Column, string RefSchema, string RefTable, string RefColumn, bool Enforced, string OnDelete, string OnUpdate)> foreignKeys = [];
      await foreach (DbDataReader row in connection.QueryAsync(
         "SELECT f.parent_object_id, f.name, pc.name, rs.name, rt.name, rc.name, " +
         "CASE WHEN f.is_disabled = 0 AND f.is_not_trusted = 0 THEN 1 ELSE 0 END, f.delete_referential_action_desc, f.update_referential_action_desc " +
         "FROM sys.foreign_keys f JOIN sys.foreign_key_columns fc ON fc.constraint_object_id = f.object_id " +
         "JOIN sys.columns pc ON pc.object_id = fc.parent_object_id AND pc.column_id = fc.parent_column_id " +
         "JOIN sys.columns rc ON rc.object_id = fc.referenced_object_id AND rc.column_id = fc.referenced_column_id " +
         "JOIN sys.objects rt ON rt.object_id = f.referenced_object_id JOIN sys.schemas rs ON rs.schema_id = rt.schema_id " +
         "ORDER BY f.parent_object_id, f.name, fc.constraint_column_id", ct).ConfigureAwait(false))
      {
         foreignKeys.Add((row.GetInt32(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetString(4), row.GetString(5),
                          row.GetInt32(6) == 1, row.GetString(7), row.GetString(8)));
      }
      foreach (var key in foreignKeys.GroupBy(f => (f.Table, f.Name)))
      {
         if (!tables.TryGetValue(key.Key.Table, out Builder? table)) { continue; }
         var first = key.First();
         table.ForeignKeys.Add(new ForeignKeySchema(key.Key.Name, key.Select(k => k.Column).ToList(), first.RefSchema, first.RefTable,
                                                    key.Select(k => k.RefColumn).ToList())
         {
            IsEnforced = first.Enforced,
            OnDelete = Action(first.OnDelete),
            OnUpdate = Action(first.OnUpdate),
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

   /// <summary>A referential action, as SQL writes it; null for NO ACTION.</summary>
   private static string? Action(string description) => description switch
   {
      "CASCADE" => "CASCADE",
      "SET_NULL" => "SET NULL",
      "SET_DEFAULT" => "SET DEFAULT",
      _ => null,
   };

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
      public bool HasTriggers { get; init; }

      public TableSchema Build() => new(Schema, Name, kind, Columns)
      {
         PrimaryKey = PrimaryKey,
         UniqueKeys = UniqueKeys.OrderBy(k => k.Name, StringComparer.Ordinal).ToList(),
         Indexes = Indexes.OrderBy(i => i.Name, StringComparer.Ordinal).ToList(),
         ForeignKeys = ForeignKeys.OrderBy(f => f.Name, StringComparer.Ordinal).ToList(),
         RowCountEstimate = RowCountEstimate,
         HasTriggers = HasTriggers,
         Comment = Comment,
      };
   }
}
