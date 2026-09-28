using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Binding;

/// <summary>A named part of a row: a column, a navigation (on entity rows) or a nested record.</summary>
public abstract class ShapeMember
{
   private protected ShapeMember(string name) { Name = name; }

   public string Name { get; }

   public override string ToString() => Name;
}

public sealed class ColumnMember : ShapeMember
{
   public ColumnMember(string name, ScalarType type, ColumnDef? column = null) : base(name)
   {
      Type = type;
      Column = column;
   }

   public ScalarType Type { get; }

   /// <summary>The catalog column, when the member is a column of an entity row.</summary>
   public ColumnDef? Column { get; }
}

public sealed class NavigationMember : ShapeMember
{
   public NavigationMember(NavigationDef navigation) : base(navigation.Name) { Navigation = navigation; }

   public NavigationDef Navigation { get; }
}

public sealed class RecordMember : ShapeMember
{
   public RecordMember(string name, RowShape shape, bool nullable) : base(name)
   {
      Shape = shape;
      Nullable = nullable;
   }

   public RowShape Shape { get; }

   public bool Nullable { get; }
}

/// <summary>
/// The members of the rows flowing through a query. When <see cref="Entity"/> is set, the rows are rows of that
/// entity: filtering, sorting and paging keep it, projecting drops it.
/// </summary>
public sealed class RowShape
{
   private readonly NameTable<ShapeMember> byName = new();

   public RowShape(IEnumerable<ShapeMember> members, EntityDef? entity = null)
   {
      Members = members.ToList();
      Entity = entity;
      foreach (ShapeMember member in Members) { byName.Add(member.Name, member); }
   }

   public EntityDef? Entity { get; }

   public IReadOnlyList<ShapeMember> Members { get; }

   public IEnumerable<ColumnMember> Columns => Members.OfType<ColumnMember>();

   public NameMatch<ShapeMember> Find(string name) => byName.Find(name);

   public bool Contains(string name) => byName.Contains(name);

   /// <summary>The shape of an entity's rows: its columns, then its navigations (including inherited ones).</summary>
   public static RowShape ForEntity(EntityDef entity) => entity.RowShape;

   internal static RowShape Build(EntityDef entity)
   {
      List<ShapeMember> members = [.. entity.Columns.Select(c => new ColumnMember(c.Name, c.Type, c))];
      members.AddRange(entity.Navigations.Select(n => new NavigationMember(n)));
      members.AddRange(entity.InheritedNavigations.Select(n => new NavigationMember(n)));
      return new RowShape(members, entity);
   }

   public RowShape Extend(IEnumerable<ShapeMember> extra) => new([.. Members, .. extra], Entity);

   public override string ToString() => "[" + string.Join(", ", Members.Select(Describe)) + "]";

   private static string Describe(ShapeMember member) => member switch
   {
      ColumnMember column => $"{column.Name} {column.Type}",
      NavigationMember navigation => $"{navigation.Name} -> {navigation.Navigation.Target.DisplayName}{(navigation.Navigation.IsCollection ? "*" : string.Empty)}",
      RecordMember record => $"{record.Name} {record.Shape}{(record.Nullable ? "?" : string.Empty)}",
      _ => member.Name,
   };
}

/// <summary>
/// A row a scope ranges over: the implicit <c>it</c> of a method argument or a lambda parameter. Expressions
/// refer to it with <see cref="BoundRowRef"/>; a reference from inside a nested query is a correlation.
/// </summary>
public sealed class RowVariable
{
   private static int next;

   public RowVariable(string name, RowShape shape)
   {
      Name = name;
      Shape = shape;
      Id = Interlocked.Increment(ref next);
   }

   public int Id { get; }

   public string Name { get; }

   public RowShape Shape { get; }

   public override string ToString() => Name;
}
