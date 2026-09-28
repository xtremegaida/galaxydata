using System;
using System.Collections.Generic;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;

namespace GalaxyData.Query.Planning;

/// <summary>The plan being built for one query chain; navigations join onto it as they are used.</summary>
internal sealed class Cursor(PlanNode node)
{
   public PlanNode Node { get; set; } = node;
}

/// <summary>
/// How the members of a row (a row variable's value, or a record inside one) map to plan values while a query
/// is lowered: columns to column references, records to nested row values. Navigations are joined on first use
/// and cached, so <c>customer.name</c> and <c>customer.city</c> share one join.
/// </summary>
internal sealed class RowValue
{
   private readonly Dictionary<string, object> members = new(StringComparer.Ordinal);
   private readonly List<string> order = [];

   public RowValue(Cursor cursor, RowShape shape, IReadOnlyList<NavigationDef> path, bool nullable,
                   Dictionary<NavigationDef, RowValue>? navigations = null)
   {
      Cursor = cursor;
      Shape = shape;
      Path = path;
      Nullable = nullable;
      Navigations = navigations ?? [];
   }

   /// <summary>The plan the row's columns belong to; joins for its navigations are added here.</summary>
   public Cursor Cursor { get; private set; }

   public RowShape Shape { get; }

   /// <summary>The navigations that lead to this row from the rows of the query, for lineage.</summary>
   public IReadOnlyList<NavigationDef> Path { get; }

   /// <summary>The row may be absent: an optional navigation, or a record of such a row.</summary>
   public bool Nullable { get; }

   /// <summary>A value that is null exactly when the row is absent; null when there is none to use.</summary>
   public PlanExpr? Indicator { get; set; }

   public Dictionary<NavigationDef, RowValue> Navigations { get; }

   public void Set(string name, PlanColumnRef column) => Put(name, column);

   public void Set(string name, RowValue record) => Put(name, record);

   private void Put(string name, object value)
   {
      if (!members.ContainsKey(name)) { order.Add(name); }
      members[name] = value;
   }

   public PlanColumnRef Column(string name) =>
      members.TryGetValue(name, out object? value) && value is PlanColumnRef column
         ? column
         : throw new InvalidOperationException($"The row has no column '{name}'; it has {string.Join(", ", members.Keys)}");

   public RowValue Record(string name) =>
      members.TryGetValue(name, out object? value) && value is RowValue record
         ? record
         : throw new InvalidOperationException($"The row has no record '{name}'");

   /// <summary>The plan columns of the row's own members, records included, in member order.</summary>
   public List<PlanColumn> Columns()
   {
      List<PlanColumn> columns = [];
      Collect(columns);
      return columns;
   }

   private void Collect(List<PlanColumn> columns)
   {
      foreach (string name in order)
      {
         switch (members[name])
         {
            case PlanColumnRef column when !columns.Contains(column.Column):
               columns.Add(column.Column);
               break;
            case RowValue record:
               record.Collect(columns);
               break;
         }
      }
   }

   /// <summary>Moves the row (and its records) to another plan, after its columns were joined into it.</summary>
   public void Rehome(Cursor cursor)
   {
      Cursor = cursor;
      foreach (object value in members.Values)
      {
         if (value is RowValue record) { record.Rehome(cursor); }
      }
      foreach (RowValue target in Navigations.Values) { target.Rehome(cursor); }
   }

   /// <summary>The same members on another plan that keeps the columns but none of the joined navigations.</summary>
   public RowValue Detached(Cursor cursor)
   {
      RowValue copy = new(cursor, Shape, Path, Nullable) { Indicator = Indicator };
      foreach (string name in order)
      {
         object value = members[name];
         copy.Put(name, value is RowValue record ? record.Detached(cursor) : value);
      }
      return copy;
   }

   /// <summary>The same row with more members (<c>extend</c>); it shares the plan and the joined navigations.</summary>
   public RowValue Extended(RowShape shape)
   {
      RowValue copy = new(Cursor, shape, Path, Nullable, Navigations) { Indicator = Indicator };
      foreach (string name in order) { copy.Put(name, members[name]); }
      return copy;
   }
}
