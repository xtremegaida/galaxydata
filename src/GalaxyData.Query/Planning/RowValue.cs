using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Results;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Planning;

/// <summary>
/// A groupBy being lowered: the aggregate node, the plan of the grouped rows (navigations used by aggregate
/// arguments join there, below the aggregate), and the key parts and aggregates made so far. Aggregates are added
/// as the operators above use them; projections above that pass every column through get the new ones too.
/// </summary>
internal sealed class GroupValue(AggregateNode node, Cursor elements, RowValue element, PlanIds ids)
{
   private readonly Dictionary<string, PlanColumnRef> made = new(StringComparer.Ordinal);
   private readonly List<ProjectNode> passThroughs = [];

   public AggregateNode Node { get; } = node;

   public Cursor Elements { get; } = elements;

   /// <summary>The grouped rows' value, for aggregate arguments.</summary>
   public RowValue Element { get; } = element;

   public PlanColumnRef Key(PlanExpr expr, string name, ScalarType type, ColumnLineage lineage)
   {
      string text = "key " + PlanPrinter.Expr(expr);
      if (made.TryGetValue(text, out PlanColumnRef? existing)) { return existing; }
      PlanColumn column = expr is PlanColumnRef reference && string.Equals(reference.Column.Name, name, StringComparison.Ordinal) && reference.Column.Type == type
         ? reference.Column
         : new PlanColumn(ids.Next(), name, type, lineage);
      Node.AddKey(new ProjectItem(column, expr));
      return Added(text, column);
   }

   public PlanColumnRef Aggregate(AggregateFunction function, PlanExpr? argument, string name, ScalarType type, ColumnLineage lineage)
   {
      string text = function + " " + (argument == null ? string.Empty : PlanPrinter.Expr(argument));
      if (made.TryGetValue(text, out PlanColumnRef? existing)) { return existing; }
      PlanColumn column = new(ids.Next(), name, type, lineage);
      Node.AddAggregate(new AggregateItem(column, function, argument));
      return Added(text, column);
   }

   public void PassThrough(ProjectNode project) => passThroughs.Add(project);

   private PlanColumnRef Added(string text, PlanColumn column)
   {
      // Arguments may have joined navigations onto the grouped rows' plan.
      Node.Input = Elements.Node;
      foreach (ProjectNode project in passThroughs) { project.AddItem(new ProjectItem(column, new PlanColumnRef(column))); }
      PlanColumnRef reference = new(column);
      made[text] = reference;
      return reference;
   }
}

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

   /// <summary>Makes columns on first use, for a record whose columns are only computed when asked for (a group key record).</summary>
   public Func<string, PlanColumnRef>? Lazy { get; set; }

   /// <summary>The grouping, when the row is a group.</summary>
   public GroupValue? Group { get; set; }

   public void Set(string name, PlanColumnRef column) => Put(name, column);

   public void Set(string name, RowValue record) => Put(name, record);

   private void Put(string name, object value)
   {
      if (!members.ContainsKey(name)) { order.Add(name); }
      members[name] = value;
   }

   public PlanColumnRef Column(string name)
   {
      if (members.TryGetValue(name, out object? value) && value is PlanColumnRef column) { return column; }
      if (Lazy != null && !members.ContainsKey(name))
      {
         PlanColumnRef made = Lazy(name);
         Put(name, made);
         return made;
      }
      throw new InvalidOperationException($"The row has no column '{name}'; it has {string.Join(", ", members.Keys)}");
   }

   public RowValue Record(string name) =>
      members.TryGetValue(name, out object? value) && value is RowValue record
         ? record
         : throw new InvalidOperationException($"The row has no record '{name}'");

   /// <summary>The plan columns of the row's own members, records included, in member order.</summary>
   public List<PlanColumn> Columns()
   {
      if (Lazy != null)
      {
         foreach (ColumnMember column in Shape.Columns) { Column(column.Name); }
      }
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
   public RowValue Detached(Cursor cursor) => Copy(cursor, Nullable);

   /// <summary>The same row as the inner row of a left join: it may be absent, and its navigations join afresh.</summary>
   public RowValue AsNullable(Cursor cursor)
   {
      RowValue copy = Copy(cursor, nullable: true);
      if (copy.Indicator == null)
      {
         string? present = Shape.Entity?.Key?.Columns[0].Name ?? order.FirstOrDefault(n => members[n] is PlanColumnRef { Column.Type.Nullable: false });
         if (present != null && members.ContainsKey(present) && members[present] is PlanColumnRef column) { copy.Indicator = column; }
      }
      return copy;
   }

   private RowValue Copy(Cursor cursor, bool nullable)
   {
      RowValue copy = new(cursor, Shape, Path, nullable) { Indicator = Indicator, Lazy = Lazy };
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
      RowValue copy = new(Cursor, shape, Path, Nullable, Navigations) { Indicator = Indicator, Lazy = Lazy, Group = Group };
      foreach (string name in order) { copy.Put(name, members[name]); }
      return copy;
   }
}
