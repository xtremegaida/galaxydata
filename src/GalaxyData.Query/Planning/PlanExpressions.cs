using System.Collections.Generic;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Functions;
using GalaxyData.Query.Results;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Planning;

/// <summary>
/// A value flowing through a plan. Identity is what matters: an operator that passes a column through keeps the
/// same object, so a column means the same values wherever it is referenced.
/// </summary>
public sealed class PlanColumn
{
   internal PlanColumn(int id, string name, ScalarType type, ColumnLineage lineage, RowOrigin? origin = null)
   {
      Id = id;
      Name = name;
      Type = type;
      Lineage = lineage;
      Origin = origin;
   }

   /// <summary>Unique within a plan; used for printing.</summary>
   public int Id { get; }

   /// <summary>A readable name: the column or member the value came from.</summary>
   public string Name { get; }

   public ScalarType Type { get; }

   public ColumnLineage Lineage { get; }

   /// <summary>
   /// The table row the value was read from, while it still stands for that row's column (its lineage is direct and
   /// every operator on the way kept rows apart); null for other values.
   /// </summary>
   internal RowOrigin? Origin { get; }

   public override string ToString() => $"{Name}#{Id}";
}

/// <summary>
/// One scan's rows, while lowering: the columns of a table row read by that scan. Columns that carry the same origin
/// and direct lineage hold the same row's value of the same column, however they were renamed on the way.
/// </summary>
internal sealed class RowOrigin(TableEntity entity)
{
   private readonly Dictionary<ColumnDef, PlanColumn> columns = [];

   public TableEntity Entity { get; } = entity;

   /// <summary>The scan's column for a column of the table.</summary>
   public PlanColumn this[ColumnDef column] => columns[column];

   public void Add(ColumnDef column, PlanColumn output) => columns[column] = output;

   /// <summary>Whether <paramref name="candidate"/> holds the value <paramref name="wanted"/> stands for: the same column, or a copy of it.</summary>
   public static bool Provides(PlanColumn candidate, PlanColumn wanted) =>
      ReferenceEquals(candidate, wanted) ||
      (wanted.Origin != null && ReferenceEquals(candidate.Origin, wanted.Origin) && SourceOf(candidate) is { } source && ReferenceEquals(source, SourceOf(wanted)));

   /// <summary>The table column a column with an origin holds.</summary>
   public static ColumnDef? SourceOf(PlanColumn column) =>
      column.Origin != null && column.Lineage.Kind == LineageKind.Direct ? column.Lineage.Sources[0].Column : null;
}

public enum ParameterSource : byte
{
   /// <summary>A <c>$name</c> parameter supplied with the query.</summary>
   User,

   /// <summary><c>now()</c>: the UTC date-time the query started, the same everywhere in it.</summary>
   Now,

   /// <summary><c>today()</c>: the UTC date the query started.</summary>
   Today,

   /// <summary>
   /// A value worked out while the query runs, before the SQL that uses it: a scalar subquery of one source, in a
   /// query that combines sources. Named <c>s1</c>, <c>s2</c>, ...
   /// </summary>
   Runtime,
}

public abstract class PlanExpr
{
   private protected PlanExpr(ScalarType type) { Type = type; }

   public ScalarType Type { get; }
}

/// <summary>A column reference; the type can be more nullable than the column's (a column of an outer-joined row).</summary>
public sealed class PlanColumnRef(PlanColumn column, ScalarType type) : PlanExpr(type)
{
   public PlanColumnRef(PlanColumn column) : this(column, column.Type) { }

   public PlanColumn Column { get; } = column;
}

public sealed class PlanLiteral(object? value, ScalarType type) : PlanExpr(type)
{
   public object? Value { get; } = value;
}

/// <summary>A value known when the query runs rather than when it is planned.</summary>
public sealed class PlanParameter(ParameterSource source, string name, ScalarType type) : PlanExpr(type)
{
   public ParameterSource Source { get; } = source;

   /// <summary>The parameter name without <c>$</c>, <c>now</c>/<c>today</c>, or a runtime value's name.</summary>
   public string Name { get; } = name;
}

public sealed class PlanUnary(UnaryOp op, PlanExpr operand, ScalarType type) : PlanExpr(type)
{
   public UnaryOp Op { get; } = op;

   public PlanExpr Operand { get; } = operand;
}

public sealed class PlanBinary(BinaryOp op, PlanExpr left, PlanExpr right, ScalarType type) : PlanExpr(type)
{
   public BinaryOp Op { get; } = op;

   public PlanExpr Left { get; } = left;

   public PlanExpr Right { get; } = right;
}

public sealed class PlanIsNull(PlanExpr operand, bool negated) : PlanExpr(ScalarType.Boolean.AsNonNullable())
{
   public PlanExpr Operand { get; } = operand;

   public bool Negated { get; } = negated;
}

public sealed class PlanInList(PlanExpr operand, IReadOnlyList<PlanExpr> items, bool negated, ScalarType type) : PlanExpr(type)
{
   public PlanExpr Operand { get; } = operand;

   public IReadOnlyList<PlanExpr> Items { get; } = items;

   public bool Negated { get; } = negated;
}

public sealed class PlanConditional(PlanExpr condition, PlanExpr whenTrue, PlanExpr whenFalse, ScalarType type) : PlanExpr(type)
{
   public PlanExpr Condition { get; } = condition;

   public PlanExpr WhenTrue { get; } = whenTrue;

   public PlanExpr WhenFalse { get; } = whenFalse;
}

public sealed class PlanFunction(FunctionDef function, IReadOnlyList<PlanExpr> arguments, ScalarType type, SourceSpan? span) : PlanExpr(type)
{
   public FunctionDef Function { get; } = function;

   public IReadOnlyList<PlanExpr> Arguments { get; } = arguments;

   /// <summary>Where the call is in the query text, for messages; null when it comes from a virtual entity's definition.</summary>
   public SourceSpan? Span { get; } = span;
}

public enum SubqueryKind : byte
{
   /// <summary>Whether the plan has rows (none, when negated).</summary>
   Exists,

   /// <summary>The single value of a plan with one row and one column (null for no rows).</summary>
   Scalar,

   /// <summary>Whether the operand is among the values of the plan's single column.</summary>
   In,
}

/// <summary>
/// A plan used as a value. It may refer to columns of the plan around it (a correlated subquery); the optimizer
/// turns the common shapes into joins.
/// </summary>
public sealed class PlanSubquery(SubqueryKind kind, PlanNode plan, PlanExpr? operand, bool negated, ScalarType type) : PlanExpr(type)
{
   public SubqueryKind Kind { get; } = kind;

   public PlanNode Plan { get; } = plan;

   public PlanExpr? Operand { get; } = operand;

   public bool Negated { get; } = negated;
}

/// <summary>A range of the query text: [Start, End).</summary>
public readonly record struct SourceSpan(int Start, int End);
