using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Providers;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.PostgreSql;

/// <summary>
/// Reads a PostgreSQL database from <c>pg_catalog</c>: the tables, views, materialized views and foreign tables the
/// login may read, in every schema but the system ones (partitions are read through their parent). Foreign keys
/// that are <c>NOT VALID</c> are reported as not enforced. Row counts are the planner's estimates, unknown until a
/// table has been analyzed. Enums, and columns of types the language has no values for, are read as text
/// (<see cref="ColumnSchema.ReadAs"/>): text functions and text take no enum, and Npgsql can't read every type.
/// </summary>
public sealed class PostgreSqlSchemaIntrospector : ISchemaIntrospector
{
   public const string ProviderKind = "postgres";

   private static readonly string[] SystemSchemas = ["pg_catalog", "information_schema"];

   /// <summary>The relations read, as a condition on <c>pg_class c</c> joined to <c>pg_namespace n</c>.</summary>
   private const string Relations =
      "c.relkind IN ('r', 'p', 'v', 'm', 'f') AND NOT c.relispartition AND n.nspname NOT LIKE 'pg\\_toast%' " +
      "AND n.nspname NOT LIKE 'pg\\_temp\\_%' AND has_table_privilege(c.oid, 'SELECT')";

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
      string? version = await connection.ScalarTextAsync("SELECT current_setting('server_version')", ct).ConfigureAwait(false);
      string defaultSchema = await connection.ScalarTextAsync("SELECT current_schema()", ct).ConfigureAwait(false) ?? "public";

      Dictionary<long, Builder> tables = [];
      string kinds = options.IncludeViews ? string.Empty : " AND c.relkind NOT IN ('v', 'm')";
      await foreach (DbDataReader row in connection.QueryAsync(
         "SELECT c.oid::bigint, n.nspname, c.relname, c.relkind::text, c.reltuples::float8, obj_description(c.oid, 'pg_class'), " +
         "EXISTS (SELECT 1 FROM pg_trigger t WHERE t.tgrelid = c.oid AND NOT t.tgisinternal) " +
         $"FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE {Relations}{kinds}", ct).ConfigureAwait(false))
      {
         string schema = row.GetString(1);
         if (!Included(schema, options)) { continue; }
         TableKind kind = row.GetString(3) switch
         {
            "v" => TableKind.View,
            "m" => TableKind.MaterializedView,
            _ => TableKind.Table,
         };
         double tuples = row.GetDouble(4);
         tables[row.GetInt64(0)] = new Builder(schema, row.GetString(2), kind)
         {
            RowCountEstimate = options.IncludeRowCountEstimates && kind != TableKind.View && tuples >= 0 ? (long)tuples : null,
            Comment = row.IsDBNull(5) ? null : row.GetString(5),
            HasTriggers = row.GetBoolean(6),
         };
      }
      if (tables.Count == 0) { return new SourceSchema(ProviderKind, version, defaultSchema, []); }

      // A domain is read as the type it is based on, through domains over domains; a column's collation only when it
      // isn't its type's.
      await foreach (DbDataReader row in connection.QueryAsync(
         "WITH RECURSIVE chain(oid, base, typmod) AS (" +
         "SELECT t.oid, t.typbasetype, t.typtypmod FROM pg_type t WHERE t.typtype = 'd' UNION ALL " +
         "SELECT d.oid, t.typbasetype, CASE WHEN d.typmod >= 0 THEN d.typmod ELSE t.typtypmod END FROM chain d JOIN pg_type t ON t.oid = d.base WHERE t.typtype = 'd'), " +
         "domains AS (SELECT d.oid, d.base, d.typmod FROM chain d JOIN pg_type t ON t.oid = d.base WHERE t.typtype <> 'd') " +
         "SELECT a.attrelid::bigint, a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull, a.attidentity::text, " +
         "a.attgenerated::text, pg_get_expr(d.adbin, d.adrelid), CASE WHEN a.attcollation <> t.typcollation THEN co.collname END, " +
         "col_description(a.attrelid, a.attnum), " +
         "format_type(b.oid, CASE WHEN m.oid IS NOT NULL THEN m.typmod ELSE a.atttypmod END), b.typtype::text, b.typcategory::text " +
         "FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
         "JOIN pg_type t ON t.oid = a.atttypid LEFT JOIN domains m ON m.oid = t.oid JOIN pg_type b ON b.oid = coalesce(m.base, t.oid) " +
         "LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum LEFT JOIN pg_collation co ON co.oid = a.attcollation " +
         $"WHERE {Relations} AND a.attnum > 0 AND NOT a.attisdropped ORDER BY a.attrelid, a.attnum", ct).ConfigureAwait(false))
      {
         if (!tables.TryGetValue(row.GetInt64(0), out Builder? table)) { continue; }
         string? defaultSql = row.IsDBNull(6) ? null : row.GetString(6);
         string identity = row.GetString(4);
         string generated = row.GetString(5);
         bool isComputed = generated.Length > 0;
         char kind = row.GetString(10)[0];
         ScalarType type = PostgreSqlTypeMapper.Map(row.GetString(9), kind, row.GetString(11)[0], !row.GetBoolean(3));
         table.Columns.Add(new ColumnSchema(row.GetString(1), table.Columns.Count, row.GetString(2), type)
         {
            ReadAs = kind == 'e' || type.Kind == ScalarKind.Unknown ? "text" : null,
            IsIdentity = identity.Length > 0 || (defaultSql != null && defaultSql.StartsWith("nextval(", StringComparison.Ordinal)),
            IsComputed = isComputed,
            DefaultSql = isComputed ? null : defaultSql,
            Collation = row.IsDBNull(7) ? null : row.GetString(7),
            Comment = row.IsDBNull(8) ? null : row.GetString(8),
         });
      }

      await foreach (DbDataReader row in connection.QueryAsync(
         "SELECT k.conrelid::bigint, k.conname, k.contype::text, k.convalidated, " +
         "ARRAY(SELECT a.attname::text FROM unnest(k.conkey) WITH ORDINALITY u(attnum, i) JOIN pg_attribute a ON a.attrelid = k.conrelid AND a.attnum = u.attnum ORDER BY u.i), " +
         "rn.nspname, rc.relname, " +
         "ARRAY(SELECT a.attname::text FROM unnest(k.confkey) WITH ORDINALITY u(attnum, i) JOIN pg_attribute a ON a.attrelid = k.confrelid AND a.attnum = u.attnum ORDER BY u.i), " +
         "k.confupdtype::text, k.confdeltype::text " +
         "FROM pg_constraint k JOIN pg_class c ON c.oid = k.conrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
         "LEFT JOIN pg_class rc ON rc.oid = k.confrelid LEFT JOIN pg_namespace rn ON rn.oid = rc.relnamespace " +
         $"WHERE {Relations} AND k.contype IN ('p', 'u', 'f') ORDER BY k.conrelid, k.conname", ct).ConfigureAwait(false))
      {
         if (!tables.TryGetValue(row.GetInt64(0), out Builder? table)) { continue; }
         string name = row.GetString(1);
         List<string> columns = [.. (string[])row.GetValue(4)];
         switch (row.GetString(2))
         {
            case "p":
               table.PrimaryKey = new KeySchema(name, columns);
               break;
            case "u":
               table.UniqueKeys.Add(new KeySchema(name, columns));
               break;
            default:
               table.ForeignKeys.Add(new ForeignKeySchema(name, columns, row.GetString(5), row.GetString(6), [.. (string[])row.GetValue(7)])
               {
                  IsEnforced = row.GetBoolean(3),
                  OnUpdate = Action(row.GetString(8)),
                  OnDelete = Action(row.GetString(9)),
               });
               break;
         }
      }

      // Key columns only (not INCLUDE ones); an expression is written as PostgreSQL writes it.
      await foreach (DbDataReader row in connection.QueryAsync(
         "SELECT i.indrelid::bigint, ic.relname, i.indisunique, i.indisprimary, " +
         "ARRAY(SELECT CASE WHEN u.attnum = 0 THEN pg_get_indexdef(i.indexrelid, u.i::int, true) ELSE a.attname::text END " +
         "FROM unnest(i.indkey::int2[]) WITH ORDINALITY u(attnum, i) LEFT JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = u.attnum " +
         "WHERE u.i <= i.indnkeyatts ORDER BY u.i), pg_get_expr(i.indpred, i.indrelid, true) " +
         "FROM pg_index i JOIN pg_class ic ON ic.oid = i.indexrelid JOIN pg_class c ON c.oid = i.indrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
         $"WHERE {Relations} ORDER BY i.indrelid, ic.relname", ct).ConfigureAwait(false))
      {
         if (!tables.TryGetValue(row.GetInt64(0), out Builder? table)) { continue; }
         table.Indexes.Add(new IndexSchema(row.GetString(1), [.. (string[])row.GetValue(4)], row.GetBoolean(2))
         {
            IsPrimaryKey = row.GetBoolean(3),
            Filter = row.IsDBNull(5) ? null : row.GetString(5),
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

   private static string? Action(string code) => code switch
   {
      "r" => "RESTRICT",
      "c" => "CASCADE",
      "n" => "SET NULL",
      "d" => "SET DEFAULT",
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
         UniqueKeys = UniqueKeys,
         Indexes = Indexes,
         ForeignKeys = ForeignKeys,
         RowCountEstimate = RowCountEstimate,
         HasTriggers = HasTriggers,
         Comment = Comment,
      };
   }
}
