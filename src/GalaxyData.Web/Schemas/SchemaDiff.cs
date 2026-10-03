using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GalaxyData.Query.Introspection;

namespace GalaxyData.Web.Schemas;

public enum SchemaChangeKind
{
   Added,
   Removed,
   Changed,
}

public enum SchemaObject
{
   /// <summary>The source itself: its server's version, default schema, or what it couldn't read.</summary>
   Source,
   Table,
   Column,
   PrimaryKey,
   UniqueKey,
   Index,
   ForeignKey,
}

/// <summary>A property of a changed object: what it was and what it is (as text; null for none).</summary>
public sealed record PropertyChange(string Property, string? From, string? To);

/// <summary>
/// One difference between two schemas: an object added, removed or changed. <see cref="Schema"/> and
/// <see cref="Table"/> name the table (or view) it is of, <see cref="Name"/> the column, key, index or foreign key.
/// </summary>
public sealed record SchemaChange(SchemaChangeKind Change, SchemaObject Object, string? Schema = null, string? Table = null, string? Name = null)
{
   /// <summary>What changed, for a changed object.</summary>
   public IReadOnlyList<PropertyChange>? Properties { get; init; }

   /// <summary><c>changed column main.orders.total: type decimal(10,2) to decimal(12,2)</c>, <c>added column main.orders.note: type string?</c></summary>
   public override string ToString()
   {
      StringBuilder text = new();
      text.Append(Change.ToString().ToLowerInvariant()).Append(' ').Append(Object switch
      {
         SchemaObject.PrimaryKey => "primary key",
         SchemaObject.UniqueKey => "unique key",
         SchemaObject.ForeignKey => "foreign key",
         _ => Object.ToString().ToLowerInvariant(),
      });
      string?[] parts = [string.IsNullOrEmpty(Schema) ? null : Schema, Table, Name];
      if (parts.Any(p => p != null)) { text.Append(' ').Append(string.Join('.', parts.Where(p => p != null))); }
      if (Properties is { Count: > 0 })
      {
         text.Append(": ").Append(string.Join("; ", Properties.Select(p => Change == SchemaChangeKind.Added ? $"{p.Property} {p.To}" : $"{p.Property} {p.From ?? "none"} to {p.To ?? "none"}")));
      }
      return text.ToString();
   }
}

/// <summary>
/// What changed between two schemas of a source: tables and views added and removed, and of those in both, their
/// columns, keys, indexes and foreign keys. Names are compared exactly, as the databases spell them; row counts are
/// left out, as they change all the time.
/// </summary>
public static class SchemaDiff
{
   public static List<SchemaChange> Compare(SourceSchema before, SourceSchema after)
   {
      ArgumentNullException.ThrowIfNull(before);
      ArgumentNullException.ThrowIfNull(after);
      List<SchemaChange> changes = [];
      List<PropertyChange> source = [];
      Property(source, "serverVersion", before.ServerVersion, after.ServerVersion);
      Property(source, "defaultSchema", before.DefaultSchema, after.DefaultSchema);
      Property(source, "warnings", Join(before.Warnings ?? []), Join(after.Warnings ?? []));
      if (source.Count > 0) { changes.Add(new SchemaChange(SchemaChangeKind.Changed, SchemaObject.Source) { Properties = source }); }

      Dictionary<(string, string), TableSchema> old = before.Tables.ToDictionary(t => (t.Schema, t.Name));
      Dictionary<(string, string), TableSchema> current = after.Tables.ToDictionary(t => (t.Schema, t.Name));
      foreach ((string schema, string name) in old.Keys.Union(current.Keys).OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => k.Item2, StringComparer.Ordinal))
      {
         bool was = old.TryGetValue((schema, name), out TableSchema? from);
         bool @is = current.TryGetValue((schema, name), out TableSchema? to);
         if (!was) { changes.Add(new SchemaChange(SchemaChangeKind.Added, SchemaObject.Table, schema, name)); }
         else if (!@is) { changes.Add(new SchemaChange(SchemaChangeKind.Removed, SchemaObject.Table, schema, name)); }
         else { Table(from!, to!, changes); }
      }
      return changes;
   }

   private static void Table(TableSchema before, TableSchema after, List<SchemaChange> changes)
   {
      string schema = after.Schema;
      string table = after.Name;
      List<PropertyChange> properties = [];
      Property(properties, "kind", Camel(before.Kind), Camel(after.Kind));
      Property(properties, "hasTriggers", Flag(before.HasTriggers), Flag(after.HasTriggers));
      Property(properties, "comment", before.Comment, after.Comment);
      if (properties.Count > 0) { changes.Add(new SchemaChange(SchemaChangeKind.Changed, SchemaObject.Table, schema, table) { Properties = properties }); }

      Columns(before, after, changes);

      string? keyBefore = before.PrimaryKey is { } a ? Columns(a.Columns) : null;
      string? keyAfter = after.PrimaryKey is { } b ? Columns(b.Columns) : null;
      if (keyBefore == null && keyAfter != null) { changes.Add(new SchemaChange(SchemaChangeKind.Added, SchemaObject.PrimaryKey, schema, table, after.PrimaryKey!.Name) { Properties = [new("columns", null, keyAfter)] }); }
      else if (keyBefore != null && keyAfter == null) { changes.Add(new SchemaChange(SchemaChangeKind.Removed, SchemaObject.PrimaryKey, schema, table, before.PrimaryKey!.Name)); }
      else if (keyBefore != null)
      {
         List<PropertyChange> key = [];
         Property(key, "columns", keyBefore, keyAfter);
         Property(key, "name", before.PrimaryKey!.Name, after.PrimaryKey!.Name);
         if (key.Count > 0) { changes.Add(new SchemaChange(SchemaChangeKind.Changed, SchemaObject.PrimaryKey, schema, table, after.PrimaryKey.Name) { Properties = key }); }
      }

      // Unique keys are what they hold, whatever they are named.
      Matched(before.UniqueKeys, after.UniqueKeys, k => Columns(k.Columns), (from, to) =>
      {
         List<PropertyChange> key = [];
         Property(key, "name", from.Name, to.Name);
         return key;
      }, k => k.Name ?? Columns(k.Columns), SchemaObject.UniqueKey, schema, table, changes);
      Matched(before.Indexes, after.Indexes, i => i.Name, (from, to) =>
      {
         List<PropertyChange> index = [];
         Property(index, "columns", Columns(from.Columns), Columns(to.Columns));
         Property(index, "unique", Flag(from.IsUnique), Flag(to.IsUnique));
         Property(index, "primaryKey", Flag(from.IsPrimaryKey), Flag(to.IsPrimaryKey));
         Property(index, "filter", from.Filter, to.Filter);
         return index;
      }, i => i.Name, SchemaObject.Index, schema, table, changes);
      Matched(before.ForeignKeys, after.ForeignKeys, ForeignKeyIdentity, (from, to) =>
      {
         List<PropertyChange> key = [];
         Property(key, "columns", Columns(from.Columns), Columns(to.Columns));
         Property(key, "references", References(from), References(to));
         Property(key, "enforced", Flag(from.IsEnforced), Flag(to.IsEnforced));
         Property(key, "onDelete", from.OnDelete, to.OnDelete);
         Property(key, "onUpdate", from.OnUpdate, to.OnUpdate);
         return key;
      }, k => k.Name ?? $"{Columns(k.Columns)} to {References(k)}", SchemaObject.ForeignKey, schema, table, changes);
   }

   private static void Columns(TableSchema before, TableSchema after, List<SchemaChange> changes)
   {
      Dictionary<string, ColumnSchema> old = before.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
      Dictionary<string, ColumnSchema> current = after.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
      // Columns move among those both have: those outside the longest run in the same order, so one column moved is
      // one change. Columns added or removed move none.
      List<string> oldOrder = before.Columns.OrderBy(c => c.Ordinal).Select(c => c.Name).Where(current.ContainsKey).ToList();
      List<string> newOrder = after.Columns.OrderBy(c => c.Ordinal).Select(c => c.Name).Where(old.ContainsKey).ToList();
      HashSet<string> stayed = InOrder(oldOrder, newOrder);
      foreach (ColumnSchema column in before.Columns.Where(c => !current.ContainsKey(c.Name)).OrderBy(c => c.Ordinal))
      {
         changes.Add(new SchemaChange(SchemaChangeKind.Removed, SchemaObject.Column, after.Schema, after.Name, column.Name));
      }
      foreach (ColumnSchema to in after.Columns.OrderBy(c => c.Ordinal))
      {
         if (!old.TryGetValue(to.Name, out ColumnSchema? from))
         {
            changes.Add(new SchemaChange(SchemaChangeKind.Added, SchemaObject.Column, after.Schema, after.Name, to.Name) { Properties = [new("type", null, to.Type.ToString())] });
            continue;
         }
         List<PropertyChange> properties = [];
         Property(properties, "type", from.Type.ToString(), to.Type.ToString());
         Property(properties, "nativeType", from.NativeType, to.NativeType);
         if (!stayed.Contains(to.Name)) { Property(properties, "position", Position(oldOrder.IndexOf(to.Name)), Position(newOrder.IndexOf(to.Name))); }
         Property(properties, "identity", Flag(from.IsIdentity), Flag(to.IsIdentity));
         Property(properties, "computed", Flag(from.IsComputed), Flag(to.IsComputed));
         Property(properties, "default", from.DefaultSql, to.DefaultSql);
         Property(properties, "rowVersion", Flag(from.IsRowVersion), Flag(to.IsRowVersion));
         Property(properties, "collation", from.Collation, to.Collation);
         Property(properties, "readAs", from.ReadAs, to.ReadAs);
         Property(properties, "comment", from.Comment, to.Comment);
         if (properties.Count > 0) { changes.Add(new SchemaChange(SchemaChangeKind.Changed, SchemaObject.Column, after.Schema, after.Name, to.Name) { Properties = properties }); }
      }
   }

   /// <summary>Objects matched by <paramref name="identity"/>: added, removed, and changed as <paramref name="compare"/> finds them.</summary>
   private static void Matched<T>(IReadOnlyList<T> before, IReadOnlyList<T> after, Func<T, string> identity, Func<T, T, List<PropertyChange>> compare,
                                  Func<T, string> name, SchemaObject what, string schema, string table, List<SchemaChange> changes)
   {
      Dictionary<string, T> old = [];
      foreach (T item in before) { old.TryAdd(identity(item), item); }
      Dictionary<string, T> current = [];
      foreach (T item in after) { current.TryAdd(identity(item), item); }
      foreach ((string id, T item) in old.OrderBy(o => o.Key, StringComparer.Ordinal))
      {
         if (!current.ContainsKey(id)) { changes.Add(new SchemaChange(SchemaChangeKind.Removed, what, schema, table, name(item))); }
      }
      foreach ((string id, T item) in current.OrderBy(c => c.Key, StringComparer.Ordinal))
      {
         if (!old.TryGetValue(id, out T? from)) { changes.Add(new SchemaChange(SchemaChangeKind.Added, what, schema, table, name(item))); }
         else if (compare(from, item) is { Count: > 0 } properties) { changes.Add(new SchemaChange(SchemaChangeKind.Changed, what, schema, table, name(item)) { Properties = properties }); }
      }
   }

   /// <summary>The names in the longest subsequence the two orders share.</summary>
   private static HashSet<string> InOrder(List<string> a, List<string> b)
   {
      int[,] longest = new int[a.Count + 1, b.Count + 1];
      for (int i = a.Count - 1; i >= 0; i--)
      {
         for (int j = b.Count - 1; j >= 0; j--)
         {
            longest[i, j] = a[i] == b[j] ? longest[i + 1, j + 1] + 1 : Math.Max(longest[i + 1, j], longest[i, j + 1]);
         }
      }
      HashSet<string> names = new(StringComparer.Ordinal);
      for (int i = 0, j = 0; i < a.Count && j < b.Count;)
      {
         if (a[i] == b[j])
         {
            names.Add(a[i]);
            i++;
            j++;
         }
         else if (longest[i + 1, j] >= longest[i, j + 1]) { i++; }
         else { j++; }
      }
      return names;
   }

   /// <summary>A named foreign key is its name; an unnamed one, what it links.</summary>
   private static string ForeignKeyIdentity(ForeignKeySchema key) => key.Name ?? $"({Columns(key.Columns)}) {References(key)}";

   private static string References(ForeignKeySchema key) =>
      $"{(string.IsNullOrEmpty(key.RefSchema) ? string.Empty : key.RefSchema + ".")}{key.RefTable}({Columns(key.RefColumns)})";

   private static string Columns(IReadOnlyList<string> columns) => string.Join(", ", columns);

   private static string Join(IReadOnlyList<string> lines) => lines.Count == 0 ? string.Empty : string.Join("\n", lines);

   private static string Flag(bool value) => value ? "true" : "false";

   private static string Position(int index) => (index + 1).ToString(CultureInfo.InvariantCulture);

   private static string Camel(TableKind kind) => kind switch
   {
      TableKind.MaterializedView => "materializedView",
      _ => kind.ToString().ToLowerInvariant(),
   };

   private static void Property(List<PropertyChange> properties, string name, string? from, string? to)
   {
      if (string.IsNullOrEmpty(from)) { from = null; }
      if (string.IsNullOrEmpty(to)) { to = null; }
      if (!string.Equals(from, to, StringComparison.Ordinal)) { properties.Add(new PropertyChange(name, from, to)); }
   }
}
