using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Language;
using GalaxyData.Web.Metadata;

namespace GalaxyData.Web.Catalog;

public enum TreeNodeKind
{
   Source,
   Schema,

   /// <summary>A namespace of virtual entities.</summary>
   Folder,
   Table,
   View,
   Virtual,
}

/// <summary>A connection as the catalog has it: how its schema stands, and whether a schema of it is in the catalog.</summary>
public sealed record CatalogSource(int ConnectionId, string Alias, string Kind, string? DisplayName, SchemaStatus Status, DateTime? RefreshedAt, bool IsReadOnly)
{
   /// <summary>A schema of it is in the catalog: the last read, or one before when the last failed.</summary>
   public bool HasSchema { get; init; }

   /// <summary>Why its schema isn't in the catalog though one was read (its snapshot is damaged); null when nothing is wrong.</summary>
   public string? Problem { get; init; }

   /// <summary>Its kind's name for people (<c>PostgreSQL</c>); null when the application has no such kind.</summary>
   public string? KindName { get; init; }

   /// <summary>The icon of its kind (a Material Symbols name); null when the application has no such kind.</summary>
   public string? KindIcon { get; init; }
}

/// <summary>A node of the catalog's tree: a source, a schema or folder in it, or an entity. Its id is its path, as queries write it.</summary>
public sealed class TreeNode
{
   private readonly List<TreeNode> children = [];

   internal TreeNode(string id, TreeNode? parent, TreeNodeKind kind, string name)
   {
      Id = id;
      Parent = parent;
      Kind = kind;
      Name = name;
   }

   public string Id { get; }

   public TreeNode? Parent { get; }

   public TreeNodeKind Kind { get; }

   public string Name { get; }

   /// <summary>The connection, for a source.</summary>
   public CatalogSource? Source { get; internal init; }

   /// <summary>The entity, for a table, view or virtual entity.</summary>
   public EntityDef? Entity { get; internal init; }

   public IReadOnlyList<TreeNode> Children => children;

   /// <summary>The names of an entity's columns (but those hidden), which search looks in too.</summary>
   internal IReadOnlyList<string> Columns { get; init; } = [];

   internal void Add(TreeNode child) => children.Add(child);

   internal void Sort() => children.Sort(Order);

   /// <summary>Namespaces before entities, each by name.</summary>
   internal static int Order(TreeNode a, TreeNode b)
   {
      int kinds = (a.Entity == null ? 0 : 1).CompareTo(b.Entity == null ? 0 : 1);
      if (kinds != 0) { return kinds; }
      int names = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
      return names != 0 ? names : StringComparer.Ordinal.Compare(a.Name, b.Name);
   }
}

/// <summary>A node search found; for an entity found by its columns, the columns.</summary>
public sealed record TreeHit(TreeNode Node, IReadOnlyList<string> Columns);

/// <summary>
/// The catalog as a tree, as queries name what is in it: the sources, then under each the entities its names reach
/// directly (those of the default schema, <c>shop.orders</c>), and its other schemas (<c>shop.sales</c>), with their
/// entities under them. Hidden entities are left out. Every source is in it, read or not.
/// </summary>
public sealed class CatalogTree
{
   private readonly Dictionary<string, TreeNode> nodes;
   private readonly List<TreeNode> roots;

   private CatalogTree(Dictionary<string, TreeNode> nodes, List<TreeNode> roots)
   {
      this.nodes = nodes;
      this.roots = roots;
   }

   public IReadOnlyList<TreeNode> Roots => roots;

   public int Count => nodes.Count;

   public TreeNode? Find(string id) => nodes.GetValueOrDefault(id);

   /// <summary>The node's ancestors, the root's child first.</summary>
   public static IReadOnlyList<TreeNode> Ancestors(TreeNode node)
   {
      ArgumentNullException.ThrowIfNull(node);
      List<TreeNode> ancestors = [];
      for (TreeNode? parent = node.Parent; parent != null; parent = parent.Parent) { ancestors.Insert(0, parent); }
      return ancestors;
   }

   /// <summary>
   /// The nodes whose names have <paramref name="text"/> in them, ignoring case: those named so first, then those
   /// whose names start so, then the others; then entities with columns that have it. Text with a <c>.</c> or
   /// <c>[</c> is looked for in paths too: a node at that path first, then those under it or starting so
   /// (<c>shop.ord</c>), then those whose paths have it. <c>More</c> when there are more than <paramref name="take"/>.
   /// </summary>
   public (IReadOnlyList<TreeHit> Hits, bool More) Search(string text, int take)
   {
      ArgumentNullException.ThrowIfNull(text);
      string wanted = text.Trim();
      // A path is looked for in paths; a name, in names alone (or every entity of shop would be found for "shop").
      bool path = wanted.Contains('.', StringComparison.Ordinal) || wanted.Contains('[', StringComparison.Ordinal);
      List<(int Rank, TreeHit Hit)> found = [];
      foreach (TreeNode node in nodes.Values)
      {
         int rank = Rank(node.Name, wanted, contains: 2);
         if (path && Rank(node.Id, wanted, contains: 3) is >= 0 and var byPath) { rank = rank < 0 ? byPath : Math.Min(rank, byPath); }
         if (rank >= 0)
         {
            found.Add((rank, new TreeHit(node, [])));
            continue;
         }
         List<string> columns = node.Columns.Where(c => c.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
         if (columns.Count > 0) { found.Add((4, new TreeHit(node, columns))); }
      }
      List<TreeHit> hits = found
         .OrderBy(f => f.Rank)
         .ThenBy(f => f.Hit.Node.Name.Length)
         .ThenBy(f => f.Hit.Node.Id, StringComparer.OrdinalIgnoreCase)
         .ThenBy(f => f.Hit.Node.Id, StringComparer.Ordinal)
         .Take(take)
         .Select(f => f.Hit)
         .ToList();
      return (hits, found.Count > take);
   }

   /// <summary>0 for the text itself, 1 for text that starts with it, <paramref name="contains"/> for text that has it; -1 otherwise.</summary>
   private static int Rank(string text, string wanted, int contains) =>
      text.Equals(wanted, StringComparison.OrdinalIgnoreCase) ? 0
         : text.StartsWith(wanted, StringComparison.OrdinalIgnoreCase) ? 1
         : text.Contains(wanted, StringComparison.OrdinalIgnoreCase) ? contains
         : -1;

   public static CatalogTree Build(ICatalog catalog, IReadOnlyList<CatalogSource> sources)
   {
      ArgumentNullException.ThrowIfNull(catalog);
      ArgumentNullException.ThrowIfNull(sources);
      Dictionary<string, TreeNode> nodes = new(StringComparer.Ordinal);
      List<TreeNode> roots = [];
      foreach (CatalogSource source in sources)
      {
         TreeNode node = new(source.Alias, null, TreeNodeKind.Source, source.Alias) { Source = source };
         nodes.Add(node.Id, node);
         roots.Add(node);
      }
      foreach (EntityDef entity in catalog.Entities)
      {
         if (entity.Hidden) { continue; }
         IReadOnlyList<string> path = PathOf(entity);
         TreeNode? parent = null;
         bool placed = true;
         for (int i = 1; i < path.Count && placed; i++)
         {
            string id = QueryText.FormatPath(path.Take(i).ToList());
            if (!nodes.TryGetValue(id, out TreeNode? ns))
            {
               bool schema = catalog.Resolve(new EntityName(path.Take(i))) is { IsFound: true, Item: CatalogNamespace { Kind: NamespaceKind.Schema } };
               ns = new TreeNode(id, parent, schema ? TreeNodeKind.Schema : TreeNodeKind.Folder, path[i - 1]);
               nodes.Add(id, ns);
               if (parent == null) { roots.Add(ns); } else { parent.Add(ns); }
            }
            placed = ns.Entity == null;
            parent = ns;
         }
         string entityId = QueryText.FormatPath(path);
         if (!placed || nodes.ContainsKey(entityId)) { continue; }
         TreeNode node = new(entityId, parent, KindOf(entity), entity.Name)
         {
            Entity = entity,
            Columns = entity.Columns.Where(c => !c.Hidden).Select(c => c.Name).ToList(),
         };
         nodes.Add(entityId, node);
         if (parent == null) { roots.Add(node); } else { parent.Add(node); }
      }
      roots.Sort(TreeNode.Order);
      foreach (TreeNode node in nodes.Values) { node.Sort(); }
      return new CatalogTree(nodes, roots);
   }

   /// <summary>The path the entity is shown at: <c>shop.orders</c> for a table the default-schema shortcut reaches, its full name otherwise.</summary>
   private static IReadOnlyList<string> PathOf(EntityDef entity) =>
      string.Equals(entity.DisplayName, entity.QualifiedName.ToString(), StringComparison.Ordinal)
         ? entity.QualifiedName.Parts
         : [entity.QualifiedName.Parts[0], entity.Name];

   private static TreeNodeKind KindOf(EntityDef entity) => entity.Kind switch
   {
      EntityKind.Table => TreeNodeKind.Table,
      EntityKind.View => TreeNodeKind.View,
      _ => TreeNodeKind.Virtual,
   };
}
