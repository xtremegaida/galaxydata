using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Catalog;

/// <summary>A registered source (connection). The alias is the first part of every name in it.</summary>
public sealed record SourceInfo(string Alias, string ProviderKind, string DefaultSchema)
{
   public bool IsReadOnly { get; init; }

   public bool SupportsDml { get; init; } = true;

   /// <summary>
   /// Treat the source's foreign keys as enforced even when the database doesn't guarantee them (SQLite without
   /// <c>PRAGMA foreign_keys</c>, untrusted constraints), so a non-null key links to exactly one row.
   /// </summary>
   public bool TrustForeignKeys { get; init; }
}

/// <summary>Anything a name can resolve to in the catalog tree: a namespace or an entity.</summary>
public abstract class CatalogItem
{
   private protected CatalogItem(string name) { Name = name; }

   public string Name { get; }
}

public enum EntityKind : byte
{
   Table,
   View,
   Virtual,
}

public abstract class EntityDef : CatalogItem
{
   private readonly NameTable<ColumnDef> columnsByName = new();
   private NameTable<NavigationDef> navigationsByName = new();
   private readonly List<ColumnDef> columns = [];
   private readonly List<NavigationDef> navigations = [];
   private readonly List<KeyDef> uniqueKeys = [];
   private readonly List<NavigationDef> inheritedNavigations = [];
   private readonly NameTable<NavigationDef> inheritedByName = new();
   private RowShape? rowShape;

   private protected EntityDef(EntityName name, CatalogNamespace ns) : base(name.Last)
   {
      QualifiedName = name;
      Namespace = ns;
      DisplayName = name.ToString();
   }

   /// <summary>The canonical path, e.g. <c>shop.main.orders</c>.</summary>
   public EntityName QualifiedName { get; }

   /// <summary>The shortest path that finds the entity, e.g. <c>shop.orders</c> when the default-schema shortcut applies.</summary>
   public string DisplayName { get; internal set; }

   public CatalogNamespace Namespace { get; }

   public abstract EntityKind Kind { get; }

   public IReadOnlyList<ColumnDef> Columns => columns;

   public KeyDef? Key { get; internal set; }

   /// <summary>Unique keys other than the primary key.</summary>
   public IReadOnlyList<KeyDef> UniqueKeys => uniqueKeys;

   public IReadOnlyList<NavigationDef> Navigations => navigations;

   /// <summary>Navigations of another entity that this one's rows also have (a virtual entity over a filtered table).</summary>
   public IReadOnlyList<NavigationDef> InheritedNavigations => inheritedNavigations;

   public ColumnDef? DisplayColumn { get; internal set; }

   public bool Hidden { get; internal set; }

   public string? Comment { get; internal set; }

   public NameMatch<ColumnDef> FindColumn(string name) => columnsByName.Find(name);

   public NameMatch<NavigationDef> FindNavigation(string name) => navigationsByName.Find(name);

   /// <summary>The shape of this entity's rows; rebuilt when members change while the catalog is built.</summary>
   internal RowShape RowShape => rowShape ??= RowShape.Build(this);

   /// <summary>True when the columns, in any order, are the key or one of the unique keys.</summary>
   public bool IsUnique(IReadOnlyCollection<ColumnDef> keyColumns)
   {
      if (Key != null && SameSet(Key.Columns, keyColumns)) { return true; }
      foreach (KeyDef key in uniqueKeys)
      {
         if (SameSet(key.Columns, keyColumns)) { return true; }
      }
      return false;
   }

   internal bool HasMemberNamed(string name) =>
      columnsByName.Contains(name) || navigationsByName.Contains(name) || inheritedByName.Contains(name);

   internal void AddColumn(ColumnDef column)
   {
      columns.Add(column);
      columnsByName.Add(column.Name, column);
      rowShape = null;
   }

   internal void AddInheritedNavigation(NavigationDef navigation)
   {
      inheritedNavigations.Add(navigation);
      inheritedByName.Add(navigation.Name, navigation);
      rowShape = null;
   }

   internal void AddUniqueKey(KeyDef key)
   {
      if (Key != null && SameSet(Key.Columns, key.Columns)) { return; }
      foreach (KeyDef existing in uniqueKeys)
      {
         if (SameSet(existing.Columns, key.Columns)) { return; }
      }
      uniqueKeys.Add(key);
   }

   internal void AddNavigation(NavigationDef navigation)
   {
      navigations.Add(navigation);
      navigationsByName.Add(navigation.Name, navigation);
      rowShape = null;
   }

   internal void ReindexNavigations()
   {
      NameTable<NavigationDef> fresh = new();
      foreach (NavigationDef navigation in navigations) { fresh.Add(navigation.Name, navigation); }
      navigationsByName = fresh;
      rowShape = null;
   }

   private static bool SameSet(IReadOnlyCollection<ColumnDef> a, IReadOnlyCollection<ColumnDef> b) =>
      a.Count == b.Count && a.All(b.Contains);

   public override string ToString() => DisplayName;
}

/// <summary>A physical table or view in one source.</summary>
public sealed class TableEntity : EntityDef
{
   internal TableEntity(EntityName name, CatalogNamespace ns, SourceInfo source, TableSchema table) : base(name, ns)
   {
      Source = source;
      Schema = table.Schema;
      Table = table.Name;
      TableKind = table.Kind;
      RowCountEstimate = table.RowCountEstimate;
      HasTriggers = table.HasTriggers;
      Comment = table.Comment;
   }

   public SourceInfo Source { get; }

   /// <summary>The physical schema name, as the source spells it.</summary>
   public string Schema { get; }

   /// <summary>The physical table name, as the source spells it.</summary>
   public string Table { get; }

   public TableKind TableKind { get; }

   public override EntityKind Kind => TableKind == TableKind.Table ? EntityKind.Table : EntityKind.View;

   public long? RowCountEstimate { get; }

   public bool HasTriggers { get; }

   /// <summary>A table with a key, in a source that is writable and supports DML.</summary>
   public bool IsWritable => Kind == EntityKind.Table && Key != null && Source.SupportsDml && !Source.IsReadOnly;
}

/// <summary>
/// An entity defined by a query in the overlay. Its columns are the query's output; when the query keeps the rows
/// of one entity (only filters, sorts or pages them) it also keeps that entity's key and navigations.
/// </summary>
public sealed class VirtualEntity : EntityDef
{
   internal VirtualEntity(EntityName name, CatalogNamespace ns, string queryText) : base(name, ns)
   {
      QueryText = queryText;
   }

   public string QueryText { get; }

   public override EntityKind Kind => EntityKind.Virtual;

   /// <summary>The bound definition; null until bound, or when binding failed.</summary>
   public BoundProgram? Definition { get; internal set; }

   /// <summary>Why the entity can't be used, when its definition doesn't bind.</summary>
   public string? Problem { get; internal set; }

   /// <summary>The entity whose rows the definition keeps, if any.</summary>
   public EntityDef? BaseEntity { get; internal set; }

   internal VirtualState State { get; set; }
}

internal enum VirtualState : byte
{
   Pending,
   Binding,
   Bound,
   Failed,
}

public sealed class ColumnDef
{
   internal ColumnDef(EntityDef owner, string name, int ordinal, ScalarType type)
   {
      Owner = owner;
      Name = name;
      Ordinal = ordinal;
      Type = type;
   }

   internal ColumnDef(EntityDef owner, ColumnSchema column) : this(owner, column.Name, column.Ordinal, column.Type)
   {
      NativeType = column.NativeType;
      IsIdentity = column.IsIdentity;
      IsComputed = column.IsComputed;
      HasDefault = column.HasDefault;
      IsRowVersion = column.IsRowVersion;
      Comment = column.Comment;
   }

   public EntityDef Owner { get; }

   public string Name { get; }

   public int Ordinal { get; }

   public ScalarType Type { get; internal set; }

   public string? NativeType { get; }

   public bool IsIdentity { get; }

   public bool IsComputed { get; }

   public bool HasDefault { get; }

   public bool IsRowVersion { get; }

   public bool Hidden { get; internal set; }

   /// <summary>A label for display, from the overlay; the name when unset.</summary>
   public string? Label { get; internal set; }

   public string? Comment { get; }

   public bool IsKey => Owner.Key?.Columns.Contains(this) ?? false;

   public override string ToString() => $"{Owner.DisplayName}.{Name}";
}

public sealed class KeyDef
{
   internal KeyDef(string? name, IReadOnlyList<ColumnDef> columns, bool isDeclared = false)
   {
      Name = name;
      Columns = columns;
      IsDeclared = isDeclared;
   }

   public string? Name { get; }

   public IReadOnlyList<ColumnDef> Columns { get; }

   /// <summary>Set by the overlay rather than read from the source.</summary>
   public bool IsDeclared { get; }
}

public enum RelationOrigin : byte
{
   ForeignKey,
   Overlay,
}

public enum Multiplicity : byte
{
   /// <summary>Exactly one related row: a non-null, enforced foreign key.</summary>
   One,

   ZeroOrOne,

   Many,
}

/// <summary>
/// A many-to-one (or one-to-one) link from dependent columns (<see cref="From"/>) to unique columns of the
/// principal (<see cref="To"/>). It yields a forward navigation on the dependent and an inverse on the principal.
/// </summary>
public sealed class RelationDef
{
   internal RelationDef(string? name, RelationOrigin origin, EntityDef from, IReadOnlyList<ColumnDef> fromColumns,
                        EntityDef to, IReadOnlyList<ColumnDef> toColumns, bool isEnforced)
   {
      Name = name;
      Origin = origin;
      From = from;
      FromColumns = fromColumns;
      To = to;
      ToColumns = toColumns;
      IsEnforced = isEnforced;
      FromIsUnique = from.IsUnique(fromColumns);
   }

   /// <summary>The constraint name, when the source has one.</summary>
   public string? Name { get; }

   public RelationOrigin Origin { get; }

   public EntityDef From { get; }

   public IReadOnlyList<ColumnDef> FromColumns { get; }

   public EntityDef To { get; }

   public IReadOnlyList<ColumnDef> ToColumns { get; }

   public bool IsEnforced { get; }

   /// <summary>The dependent columns are themselves unique, so the inverse is a single reference.</summary>
   public bool FromIsUnique { get; }

   public NavigationDef Forward { get; internal set; } = null!;

   public NavigationDef Inverse { get; internal set; } = null!;

   /// <summary>The two sides live in different sources.</summary>
   public bool IsCrossSource =>
      From is not TableEntity from || To is not TableEntity to || !string.Equals(from.Source.Alias, to.Source.Alias, StringComparison.Ordinal);

   public override string ToString() =>
      $"{From.DisplayName}({string.Join(", ", FromColumns.Select(c => c.Name))}) -> {To.DisplayName}({string.Join(", ", ToColumns.Select(c => c.Name))})";
}

public sealed class NavigationDef
{
   internal NavigationDef(RelationDef relation, bool isInverse)
   {
      Relation = relation;
      IsInverse = isInverse;
      if (isInverse)
      {
         Multiplicity = relation.FromIsUnique ? Multiplicity.ZeroOrOne : Multiplicity.Many;
      }
      else
      {
         bool required = relation.IsEnforced && relation.FromColumns.All(c => !c.Type.Nullable);
         Multiplicity = required ? Multiplicity.One : Multiplicity.ZeroOrOne;
      }
   }

   public string Name { get; internal set; } = string.Empty;

   /// <summary>The name the naming convention gave it, which overlay renames are keyed on.</summary>
   public string ConventionName { get; internal set; } = string.Empty;

   public RelationDef Relation { get; }

   public bool IsInverse { get; }

   public Multiplicity Multiplicity { get; }

   public bool IsCollection => Multiplicity == Multiplicity.Many;

   public bool Hidden { get; internal set; }

   public EntityDef Owner => IsInverse ? Relation.To : Relation.From;

   public EntityDef Target => IsInverse ? Relation.From : Relation.To;

   /// <summary>The owner's columns that the link matches on.</summary>
   public IReadOnlyList<ColumnDef> OwnerColumns => IsInverse ? Relation.ToColumns : Relation.FromColumns;

   /// <summary>The target's columns that the link matches on.</summary>
   public IReadOnlyList<ColumnDef> TargetColumns => IsInverse ? Relation.FromColumns : Relation.ToColumns;

   public NavigationDef Opposite => IsInverse ? Relation.Forward : Relation.Inverse;

   public override string ToString() => $"{Owner.DisplayName}.{Name} -> {Target.DisplayName} ({Multiplicity})";
}
