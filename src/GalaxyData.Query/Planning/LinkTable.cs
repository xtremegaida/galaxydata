using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Results;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Planning;

/// <summary>A link a column will have, while lowering: what it leads to, and the plan columns whose values it needs.</summary>
internal abstract record LinkSpec(IReadOnlyList<PlanColumn> Values);

/// <summary>The row of a record: <c>select(customer)</c> shows the display column and links to the row by its key.</summary>
internal sealed record RowLinkSpec(EntityDef Target, IReadOnlyList<ColumnDef> TargetColumns, NavigationDef? Navigation, IReadOnlyList<PlanColumn> Values)
   : LinkSpec(Values);

/// <summary>The rows of a collection navigation from a row: <c>orders.count()</c> links to the orders; the values are the owner's.</summary>
internal sealed record CollectionLinkSpec(NavigationDef Navigation, IReadOnlyList<PlanColumn> Values) : LinkSpec(Values);

internal sealed record DrillDownKeySpec(string Expression, string? Parameter, ScalarType Type);

/// <summary>The grouped rows behind an aggregate: the query before groupBy, and the key parts (one value each).</summary>
internal sealed record DrillDownSpec(string QueryText, IReadOnlyList<DrillDownKeySpec> Keys, IReadOnlyList<PlanColumn> Values) : LinkSpec(Values);

/// <summary>
/// The links of the columns being lowered, and the key values they and edit targets need. A projection that drops
/// those values keeps them as hidden columns (<see cref="AddHidden"/>), so the result can still resolve them.
/// </summary>
internal sealed class LinkTable
{
   private readonly Dictionary<PlanColumn, LinkSpec> columns = [];
   private readonly Dictionary<PlanSubquery, LinkSpec> subqueries = [];

   public void Set(PlanColumn column, LinkSpec spec) => columns[column] = spec;

   /// <summary>Records that a subquery's value leads somewhere, for the column it becomes when projected.</summary>
   public void Set(PlanSubquery subquery, LinkSpec spec) => subqueries[subquery] = spec;

   /// <summary>Gives a column made from an expression the link of what it copies: a column, or a linked subquery.</summary>
   public void Inherit(PlanColumn column, PlanExpr expr)
   {
      LinkSpec? spec = expr switch
      {
         PlanColumnRef reference => columns.GetValueOrDefault(reference.Column),
         PlanSubquery subquery => subqueries.GetValueOrDefault(subquery),
         _ => null,
      };
      if (spec != null) { columns.TryAdd(column, spec); }
   }

   /// <summary>The values a column's edit target and link need.</summary>
   public IEnumerable<PlanColumn> Dependencies(PlanColumn column)
   {
      if (column.Origin is { } origin && RowOrigin.SourceOf(column) is { } source)
      {
         if (Editable(origin) is { } key)
         {
            foreach (ColumnDef part in key.Columns) { yield return origin[part]; }
         }
         if (ForeignKey(source) is { } navigation)
         {
            foreach (ColumnDef part in navigation.OwnerColumns) { yield return origin[part]; }
         }
      }
      if (columns.TryGetValue(column, out LinkSpec? spec))
      {
         foreach (PlanColumn value in spec.Values) { yield return value; }
      }
   }

   /// <summary>
   /// Adds pass-through items for the values the first <paramref name="visible"/> items' links and edit targets need
   /// and no item has, when <paramref name="available"/> (the input's columns) has them.
   /// </summary>
   public void AddHidden(List<ProjectItem> items, int visible, IReadOnlyList<PlanColumn> available)
   {
      List<PlanColumn> needed = [];
      for (int i = 0; i < visible; i++) { needed.AddRange(Dependencies(items[i].Column)); }
      foreach (PlanColumn wanted in needed)
      {
         if (items.Any(i => RowOrigin.Provides(i.Column, wanted))) { continue; }
         PlanColumn? found = available.FirstOrDefault(c => RowOrigin.Provides(c, wanted));
         if (found != null) { items.Add(new ProjectItem(found, new PlanColumnRef(found))); }
      }
   }

   /// <summary>The edit target of a result column, when the key of its row is among the result's columns.</summary>
   public EditTarget? EditTarget(PlanColumn column, IReadOnlyList<PlanColumn> result)
   {
      if (column.Origin is not { } origin || RowOrigin.SourceOf(column) is not { } source || Editable(origin) is not { } key) { return null; }
      List<int>? ordinals = Ordinals(key.Columns.Select(c => origin[c]), result);
      return ordinals == null ? null : new EditTarget(origin.Entity, source, ordinals);
   }

   /// <summary>The link of a result column: its own (a record, a collection, a group's rows), else its foreign key's.</summary>
   public ColumnLink? Link(PlanColumn column, IReadOnlyList<PlanColumn> result)
   {
      if (columns.TryGetValue(column, out LinkSpec? spec))
      {
         List<int>? ordinals = Ordinals(spec.Values, result);
         if (ordinals == null) { return null; }
         return spec switch
         {
            RowLinkSpec row => new RowLink(row.Target, row.TargetColumns, ordinals, row.Navigation),
            CollectionLinkSpec collection => new CollectionLink(collection.Navigation, ordinals),
            DrillDownSpec drill => new DrillDownLink(drill.QueryText,
               drill.Keys.Select((k, i) => new DrillDownKey(k.Expression, k.Parameter, ordinals[i], k.Type)).ToList()),
            _ => throw new InvalidOperationException($"Unexpected link {spec}"),
         };
      }
      if (column.Origin is { } origin && RowOrigin.SourceOf(column) is { } source && ForeignKey(source) is { } navigation)
      {
         List<int>? ordinals = Ordinals(navigation.OwnerColumns.Select(c => origin[c]), result);
         return ordinals == null ? null : new RowLink(navigation.Target, navigation.TargetColumns, ordinals, navigation);
      }
      return null;
   }

   /// <summary>Where each value is among the result's columns; null when one of them isn't.</summary>
   private static List<int>? Ordinals(IEnumerable<PlanColumn> values, IReadOnlyList<PlanColumn> result)
   {
      List<int> ordinals = [];
      foreach (PlanColumn value in values)
      {
         int ordinal = -1;
         for (int i = 0; i < result.Count && ordinal < 0; i++)
         {
            if (RowOrigin.Provides(result[i], value)) { ordinal = i; }
         }
         if (ordinal < 0) { return null; }
         ordinals.Add(ordinal);
      }
      return ordinals;
   }

   /// <summary>The key edits of a row go by: tables with a key; views and keyless tables can't be edited by key.</summary>
   private static KeyDef? Editable(RowOrigin origin) => origin.Entity.Kind == EntityKind.Table ? origin.Entity.Key : null;

   /// <summary>The many-to-one navigation a column is the foreign key of; a single-column one first.</summary>
   public static NavigationDef? ForeignKey(ColumnDef column)
   {
      NavigationDef? found = null;
      foreach (NavigationDef navigation in column.Owner.Navigations)
      {
         if (navigation.IsInverse || navigation.IsCollection || navigation.Hidden || !navigation.OwnerColumns.Contains(column)) { continue; }
         if (navigation.OwnerColumns.Count == 1) { return navigation; }
         found ??= navigation;
      }
      return found;
   }
}
