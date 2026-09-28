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
   private readonly List<ProjectItem> items;
   private List<PlanColumn>? output;

   public ProjectNode(PlanNode input, IEnumerable<ProjectItem> items)
   {
      Input = input;
      this.items = items.ToList();
   }

   public PlanNode Input { get; }

   public IReadOnlyList<ProjectItem> Items => items;

   public override IReadOnlyList<PlanColumn> Output => output ??= items.Select(i => i.Column).ToList();

   public override IReadOnlyList<PlanNode> Inputs => [Input];

   /// <summary>While lowering: passes through a column added below after this projection was made.</summary>
   internal void AddItem(ProjectItem item)
   {
      items.Add(item);
      output = null;
   }
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

public enum AggregateFunction : byte
{
   /// <summary><c>count(*)</c>: the rows.</summary>
   CountRows,

   /// <summary>The rows where the argument isn't null.</summary>
   Count,

   CountDistinct,

   /// <summary>The sum, which is 0 when there are no values.</summary>
   Sum,

   Avg,
   Min,
   Max,
}

public sealed record AggregateItem(PlanColumn Column, AggregateFunction Function, PlanExpr? Argument);

/// <summary>
/// Groups rows by the key items and computes aggregates per group; with no keys, one row for all the input. The
/// output is the key columns, then the aggregate columns. While a query is lowered the lists grow as aggregates are
/// used by the operators above.
/// </summary>
public sealed class AggregateNode : PlanNode
{
   private readonly List<ProjectItem> keys;
   private readonly List<AggregateItem> aggregates;

   public AggregateNode(PlanNode input, IEnumerable<ProjectItem> keys, IEnumerable<AggregateItem> aggregates)
   {
      Input = input;
      this.keys = keys.ToList();
      this.aggregates = aggregates.ToList();
   }

   public PlanNode Input { get; internal set; }

   public IReadOnlyList<ProjectItem> Keys => keys;

   public IReadOnlyList<AggregateItem> Aggregates => aggregates;

   public override IReadOnlyList<PlanColumn> Output => [.. keys.Select(k => k.Column), .. aggregates.Select(a => a.Column)];

   public override IReadOnlyList<PlanNode> Inputs => [Input];

   internal void AddKey(ProjectItem key) => keys.Add(key);

   internal void AddAggregate(AggregateItem aggregate) => aggregates.Add(aggregate);
}

public enum SetOperation : byte
{
   Union,
   UnionAll,
   Intersect,
   Except,
}

/// <summary>A set operation; the inputs' outputs line up by position with <see cref="Output"/>.</summary>
public sealed class SetOpNode(SetOperation operation, PlanNode left, PlanNode right, IReadOnlyList<PlanColumn> output) : PlanNode
{
   public SetOperation Operation { get; } = operation;

   public PlanNode Left { get; } = left;

   public PlanNode Right { get; } = right;

   public override IReadOnlyList<PlanColumn> Output { get; } = output;

   public override IReadOnlyList<PlanNode> Inputs => [Left, Right];
}
