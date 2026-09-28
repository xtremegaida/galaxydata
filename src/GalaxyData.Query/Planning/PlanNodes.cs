using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Catalog;

namespace GalaxyData.Query.Planning;

/// <summary>A logical operator. Its output is a list of columns; operators above refer to them by identity.</summary>
public abstract class PlanNode
{
   public abstract IReadOnlyList<PlanColumn> Output { get; }

   public abstract IReadOnlyList<PlanNode> Inputs { get; }
}

public sealed record ScanColumn(ColumnDef Column, PlanColumn Output);

/// <summary>Reads a table or view of one source.</summary>
public sealed class ScanNode : PlanNode
{
   public ScanNode(TableEntity entity, IReadOnlyList<ScanColumn> columns)
   {
      Entity = entity;
      Columns = columns;
      Output = columns.Select(c => c.Output).ToList();
   }

   public TableEntity Entity { get; }

   public IReadOnlyList<ScanColumn> Columns { get; }

   public override IReadOnlyList<PlanColumn> Output { get; }

   public override IReadOnlyList<PlanNode> Inputs => [];
}

/// <summary>A single row with no columns: the input of a query that reads no table.</summary>
public sealed class OneRowNode : PlanNode
{
   public override IReadOnlyList<PlanColumn> Output => [];

   public override IReadOnlyList<PlanNode> Inputs => [];
}

public sealed class FilterNode(PlanNode input, PlanExpr predicate) : PlanNode
{
   public PlanNode Input { get; } = input;

   public PlanExpr Predicate { get; } = predicate;

   public override IReadOnlyList<PlanColumn> Output => Input.Output;

   public override IReadOnlyList<PlanNode> Inputs => [Input];
}

/// <summary>An output column and how it is computed. When the expression refers to the same column, the item passes it through.</summary>
public sealed record ProjectItem(PlanColumn Column, PlanExpr Expr)
{
   public bool IsPassThrough => Expr is PlanColumnRef reference && ReferenceEquals(reference.Column, Column);
}

public sealed class ProjectNode : PlanNode
{
   public ProjectNode(PlanNode input, IReadOnlyList<ProjectItem> items)
   {
      Input = input;
      Items = items;
      Output = items.Select(i => i.Column).ToList();
   }

   public PlanNode Input { get; }

   public IReadOnlyList<ProjectItem> Items { get; }

   public override IReadOnlyList<PlanColumn> Output { get; }

   public override IReadOnlyList<PlanNode> Inputs => [Input];
}

public enum JoinKind : byte
{
   Inner,
   Left,
   Semi,
   Anti,
   Cross,
}

/// <summary>A join; <see cref="Navigation"/> is set when it follows a many-to-one navigation.</summary>
public sealed class JoinNode : PlanNode
{
   public JoinNode(JoinKind kind, PlanNode left, PlanNode right, PlanExpr? condition, NavigationDef? navigation)
   {
      Kind = kind;
      Left = left;
      Right = right;
      Condition = condition;
      Navigation = navigation;
      Output = kind is JoinKind.Semi or JoinKind.Anti ? left.Output : [.. left.Output, .. right.Output];
   }

   public JoinKind Kind { get; }

   public PlanNode Left { get; }

   public PlanNode Right { get; }

   public PlanExpr? Condition { get; }

   public NavigationDef? Navigation { get; }

   public override IReadOnlyList<PlanColumn> Output { get; }

   public override IReadOnlyList<PlanNode> Inputs => [Left, Right];
}

public sealed record PlanSortKey(PlanExpr Expr, bool Descending);

public sealed class SortNode(PlanNode input, IReadOnlyList<PlanSortKey> keys) : PlanNode
{
   public PlanNode Input { get; } = input;

   public IReadOnlyList<PlanSortKey> Keys { get; } = keys;

   public override IReadOnlyList<PlanColumn> Output => Input.Output;

   public override IReadOnlyList<PlanNode> Inputs => [Input];
}

/// <summary>Skips <see cref="Offset"/> rows, then keeps at most <see cref="Count"/>; either may be absent.</summary>
public sealed class LimitNode(PlanNode input, PlanExpr? count, PlanExpr? offset) : PlanNode
{
   public PlanNode Input { get; } = input;

   public PlanExpr? Count { get; } = count;

   public PlanExpr? Offset { get; } = offset;

   public override IReadOnlyList<PlanColumn> Output => Input.Output;

   public override IReadOnlyList<PlanNode> Inputs => [Input];
}

public sealed class DistinctNode(PlanNode input) : PlanNode
{
   public PlanNode Input { get; } = input;

   public override IReadOnlyList<PlanColumn> Output => Input.Output;

   public override IReadOnlyList<PlanNode> Inputs => [Input];
}
