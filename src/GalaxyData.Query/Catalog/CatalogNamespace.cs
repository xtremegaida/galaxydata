using System.Collections.Generic;

namespace GalaxyData.Query.Catalog;

public enum NamespaceKind : byte
{
   Root,
   Source,
   Schema,
   Virtual,
}

/// <summary>
/// A node of the catalog tree: the root, a source (by alias), a schema, or a namespace for virtual entities.
/// A source namespace also links the entities of its default schema directly, so <c>shop.orders</c> finds
/// <c>shop.main.orders</c>.
/// </summary>
public sealed class CatalogNamespace : CatalogItem
{
   private readonly NameTable<CatalogItem> members = new();
   private readonly List<CatalogNamespace> namespaces = [];
   private readonly List<EntityDef> entities = [];
   private readonly List<EntityDef> shortcuts = [];

   internal CatalogNamespace(string name, CatalogNamespace? parent, NamespaceKind kind, SourceInfo? source) : base(name)
   {
      Parent = parent;
      Kind = kind;
      Source = source;
      Path = parent == null ? [] : [.. parent.Path, name];
   }

   public CatalogNamespace? Parent { get; }

   public NamespaceKind Kind { get; }

   /// <summary>The source this namespace belongs to; null for the root and virtual namespaces.</summary>
   public SourceInfo? Source { get; }

   /// <summary>The parts from the root to this namespace; empty for the root.</summary>
   public IReadOnlyList<string> Path { get; }

   public IReadOnlyList<CatalogNamespace> Namespaces => namespaces;

   /// <summary>The entities that live in this namespace.</summary>
   public IReadOnlyList<EntityDef> Entities => entities;

   /// <summary>Default-schema entities linked under a source namespace.</summary>
   public IReadOnlyList<EntityDef> Shortcuts => shortcuts;

   public NameMatch<CatalogItem> Lookup(string name) => members.Find(name);

   internal bool Contains(string name) => members.Contains(name);

   internal CatalogNamespace GetOrAddNamespace(string name, NamespaceKind kind, SourceInfo? source)
   {
      foreach (CatalogNamespace existing in namespaces)
      {
         if (string.Equals(existing.Name, name, System.StringComparison.Ordinal)) { return existing; }
      }
      CatalogNamespace child = new(name, this, kind, source);
      namespaces.Add(child);
      members.Add(name, child);
      return child;
   }

   internal void AddEntity(EntityDef entity)
   {
      entities.Add(entity);
      members.Add(entity.Name, entity);
   }

   internal void AddShortcut(EntityDef entity)
   {
      shortcuts.Add(entity);
      members.Add(entity.Name, entity);
   }

   public override string ToString() => Path.Count == 0 ? "<root>" : string.Join('.', Path);
}
