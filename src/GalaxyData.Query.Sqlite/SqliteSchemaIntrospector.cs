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

namespace GalaxyData.Query.Sqlite;

/// <summary>
/// Reads the <c>main</c> schema of a SQLite database from <c>sqlite_master</c> and the <c>pragma_*</c> table
/// functions. SQLite enforces foreign keys only when a connection turns them on, so they are reported as not
/// enforced.
/// </summary>
public sealed class SqliteSchemaIntrospector : ISchemaIntrospector
{
   public const string ProviderKind = "sqlite";
   public const string MainSchema = "main";

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
      string? version = await connection.ScalarTextAsync("SELECT sqlite_version()", ct).ConfigureAwait(false);
      if (!SchemaIncluded(options))
      {
         return new SourceSchema(ProviderKind, version, MainSchema, []);
      }

      List<(string Type, string Name)> objects = [];
      string systemFilter = options.IncludeSystemObjects ? string.Empty : " AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\'";
      string types = options.IncludeViews ? "'table', 'view'" : "'table'";
      await foreach (DbDataReader row in connection.QueryAsync($"SELECT type, name FROM sqlite_master WHERE type IN ({types}){systemFilter} ORDER BY name", ct).ConfigureAwait(false))
      {
         objects.Add((row.GetString(0), row.GetString(1)));
      }

      HashSet<string> triggered = new(StringComparer.OrdinalIgnoreCase);
      await foreach (DbDataReader row in connection.QueryAsync("SELECT DISTINCT tbl_name FROM sqlite_master WHERE type = 'trigger'", ct).ConfigureAwait(false))
      {
         triggered.Add(row.GetString(0));
      }

      Dictionary<string, long> estimates = options.IncludeRowCountEstimates
         ? await ReadStatisticsAsync(connection, ct).ConfigureAwait(false)
         : new Dictionary<string, long>();

      List<TableSchema> tables = new(objects.Count);
      foreach ((string type, string name) in objects)
      {
         bool isView = type == "view";
         tables.Add(await ReadTableAsync(connection, name, isView, triggered.Contains(name),
            estimates.TryGetValue(name, out long estimate) ? estimate : null, ct).ConfigureAwait(false));
      }
      return new SourceSchema(ProviderKind, version, MainSchema, tables);
   }

   private static bool SchemaIncluded(IntrospectionOptions options)
   {
      if (options.IncludeSchemas is { Count: > 0 } include && !include.Contains(MainSchema, StringComparer.OrdinalIgnoreCase)) { return false; }
      return options.ExcludeSchemas is not { Count: > 0 } exclude || !exclude.Contains(MainSchema, StringComparer.OrdinalIgnoreCase);
   }

   private static async Task<Dictionary<string, long>> ReadStatisticsAsync(DbConnection connection, CancellationToken ct)
   {
      Dictionary<string, long> estimates = new(StringComparer.OrdinalIgnoreCase);
      string? exists = await connection.ScalarTextAsync("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'sqlite_stat1'", ct).ConfigureAwait(false);
      if (exists == null) { return estimates; }
      await foreach (DbDataReader row in connection.QueryAsync("SELECT tbl, stat FROM sqlite_stat1", ct).ConfigureAwait(false))
      {
         if (row.IsDBNull(1)) { continue; }
         string stat = row.GetString(1);
         int space = stat.IndexOf(' ', StringComparison.Ordinal);
         if (long.TryParse(space < 0 ? stat : stat[..space], NumberStyles.None, CultureInfo.InvariantCulture, out long rows))
         {
            string table = row.GetString(0);
            estimates[table] = estimates.TryGetValue(table, out long known) ? Math.Max(known, rows) : rows;
         }
      }
      return estimates;
   }

   private static async Task<TableSchema> ReadTableAsync(DbConnection connection, string name, bool isView, bool hasTriggers,
                                                         long? estimate, CancellationToken ct)
   {
      List<(int Cid, string Name, string Declared, bool NotNull, string? Default, int Pk, int Hidden)> raw = [];
      await foreach (DbDataReader row in connection.QueryAsync("SELECT cid, name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo(@t) ORDER BY cid", ct, ("@t", name)).ConfigureAwait(false))
      {
         raw.Add((row.GetInt32(0), row.GetString(1), row.IsDBNull(2) ? string.Empty : row.GetString(2), row.GetInt64(3) != 0,
                  row.IsDBNull(4) ? null : row.GetString(4), row.GetInt32(5), row.GetInt32(6)));
      }

      var keyColumns = raw.Where(c => c.Pk > 0).OrderBy(c => c.Pk).ToList();
      bool rowidAlias = !isView && keyColumns.Count == 1 &&
         string.Equals(keyColumns[0].Declared.Trim(), "INTEGER", StringComparison.OrdinalIgnoreCase);

      List<ColumnSchema> columns = new(raw.Count);
      int ordinal = 0;
      foreach (var column in raw)
      {
         if (column.Hidden == 1) { continue; }
         bool nullable = !column.NotNull && column.Pk == 0;
         columns.Add(new ColumnSchema(column.Name, ordinal++, column.Declared, SqliteTypeMapper.Map(column.Declared, nullable))
         {
            IsIdentity = rowidAlias && column.Pk == 1,
            IsComputed = column.Hidden is 2 or 3,
            DefaultSql = column.Default,
         });
      }

      if (isView)
      {
         return new TableSchema(MainSchema, name, TableKind.View, columns);
      }

      KeySchema? primaryKey = keyColumns.Count > 0 ? new KeySchema(null, keyColumns.Select(c => c.Name).ToList()) : null;
      List<IndexSchema> indexes = [];
      List<KeySchema> uniqueKeys = [];
      List<(string Name, bool Unique, string Origin, bool Partial)> indexList = [];
      await foreach (DbDataReader row in connection.QueryAsync("SELECT name, \"unique\", origin, partial FROM pragma_index_list(@t) ORDER BY name", ct, ("@t", name)).ConfigureAwait(false))
      {
         indexList.Add((row.GetString(0), row.GetInt64(1) != 0, row.GetString(2), row.GetInt64(3) != 0));
      }
      foreach ((string indexName, bool unique, string origin, bool partial) in indexList)
      {
         List<string> indexColumns = [];
         await foreach (DbDataReader row in connection.QueryAsync("SELECT seqno, cid, name FROM pragma_index_info(@i) ORDER BY seqno", ct, ("@i", indexName)).ConfigureAwait(false))
         {
            indexColumns.Add(row.IsDBNull(2) ? "(expression)" : row.GetString(2));
         }
         string? filter = partial ? await ReadIndexFilterAsync(connection, indexName, ct).ConfigureAwait(false) : null;
         indexes.Add(new IndexSchema(indexName, indexColumns, unique) { IsPrimaryKey = origin == "pk", Filter = filter });
         if (unique && origin == "u") { uniqueKeys.Add(new KeySchema(indexName, indexColumns)); }
      }

      List<ForeignKeySchema> foreignKeys = [];
      List<(long Id, string Table, string From, string? To, string OnUpdate, string OnDelete)> fkRows = [];
      await foreach (DbDataReader row in connection.QueryAsync("SELECT id, seq, \"table\", \"from\", \"to\", on_update, on_delete FROM pragma_foreign_key_list(@t) ORDER BY id, seq", ct, ("@t", name)).ConfigureAwait(false))
      {
         fkRows.Add((row.GetInt64(0), row.GetString(2), row.GetString(3), row.IsDBNull(4) ? null : row.GetString(4),
                     row.GetString(5), row.GetString(6)));
      }
      // SQLite numbers foreign keys in reverse order of declaration.
      foreach (var group in fkRows.GroupBy(r => r.Id).OrderByDescending(g => g.Key))
      {
         var first = group.First();
         List<string> to = group.All(r => r.To != null) ? group.Select(r => r.To!).ToList() : [];
         foreignKeys.Add(new ForeignKeySchema(null, group.Select(r => r.From).ToList(), MainSchema, first.Table, to)
         {
            IsEnforced = false,
            OnUpdate = Action(first.OnUpdate),
            OnDelete = Action(first.OnDelete),
         });
      }

      return new TableSchema(MainSchema, name, TableKind.Table, columns)
      {
         PrimaryKey = primaryKey,
         UniqueKeys = uniqueKeys,
         Indexes = indexes,
         ForeignKeys = foreignKeys,
         RowCountEstimate = estimate,
         HasTriggers = hasTriggers,
      };
   }

   private static string? Action(string action) =>
      string.Equals(action, "NO ACTION", StringComparison.OrdinalIgnoreCase) ? null : action;

   private static async Task<string?> ReadIndexFilterAsync(DbConnection connection, string index, CancellationToken ct)
   {
      string? sql = await connection.ScalarTextAsync("SELECT sql FROM sqlite_master WHERE type = 'index' AND name = @i", ct, ("@i", index)).ConfigureAwait(false);
      if (sql == null) { return "(partial)"; }
      int where = sql.LastIndexOf(" WHERE ", StringComparison.OrdinalIgnoreCase);
      return where < 0 ? "(partial)" : sql[(where + 7)..].Trim();
   }
}
