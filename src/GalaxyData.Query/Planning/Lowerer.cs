using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Common.Ast;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Functions;
using GalaxyData.Query.Results;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Planning;

/// <summary>A query's logical plan; the root's output columns line up with the schema's columns.</summary>
public sealed class LogicalPlan
{
   internal LogicalPlan(PlanNode root, ResultSchema schema)
   {
      Root = root;
      Schema = schema;
   }

   public PlanNode Root { get; }

   public ResultSchema Schema { get; }

   public override string ToString() => PlanPrinter.Print(Root);
}

/// <summary>
/// Turns a bound query into a logical plan. Entity scans read every column; navigations become joins, made once
/// per row and reused; virtual entities and named subtrees are expanded afresh at each use. Lineage is worked
/// out as columns are created, so it is known before anything runs.
/// </summary>
internal sealed class Lowerer
{
   private readonly PlanIds ids;
   private readonly string text;
   private readonly bool spans;
   private readonly IReadOnlyList<NavigationDef> pathPrefix;
   private readonly Dictionary<RowVariable, RowValue> rows = [];

   private Lowerer(PlanIds ids, string text, bool spans, IReadOnlyList<NavigationDef> pathPrefix)
   {
      this.ids = ids;
      this.text = text;
      this.spans = spans;
      this.pathPrefix = pathPrefix;
   }

   /// <summary>Lowers a program that bound successfully; <paramref name="offset"/> and <paramref name="limit"/> page the result.</summary>
   public static LogicalPlan Lower(BoundProgram program, long? offset = null, long? limit = null)
   {
      ArgumentNullException.ThrowIfNull(program);
      Lowerer lowerer = new(new PlanIds(), program.Text, spans: true, []);
      (Cursor cursor, ResultSchema schema) = program.Result switch
      {
         BoundQuery query => lowerer.Result(query),
         BoundExpr { IsScalar: true } value => lowerer.Result(value),
         _ => throw new ArgumentException("The program has no result to plan; bind it without errors first", nameof(program)),
      };
      if (offset > 0 || limit != null)
      {
         cursor.Node = new LimitNode(cursor.Node, limit == null ? null : Int64(limit.Value), offset > 0 ? Int64(offset.Value) : null);
      }
      return new LogicalPlan(cursor.Node, schema);
   }

   #region Results

   private (Cursor, ResultSchema) Result(BoundQuery query)
   {
      (Cursor cursor, RowValue row) = Query(query);
      List<ProjectItem> items = [];
      List<ResultColumn> columns = [];
      foreach (ShapeMember member in query.Shape.Members)
      {
         PlanColumnRef? value = member switch
         {
            ColumnMember scalar => row.Column(scalar.Name),
            RecordMember record => Display(row.Record(record.Name)),
            _ => null,
         };
         if (value == null) { continue; }
         PlanColumn column = Output(member.Name, value, value.Type, value.Column.Lineage);
         items.Add(new ProjectItem(column, value));
         columns.Add(new ResultColumn(columns.Count, member.Name, column.Type, column.Lineage));
      }
      cursor.Node = Project(cursor.Node, items);
      return (cursor, new ResultSchema(columns, query.Shape.Entity));
   }

   private (Cursor, ResultSchema) Result(BoundExpr value)
   {
      Cursor cursor = new(new OneRowNode());
      PlanExpr expr = Scalar(value);
      PlanColumn column = new(ids.Next(), "value", value.Scalar, Lineage(expr, value.Syntax));
      cursor.Node = new ProjectNode(cursor.Node, [new ProjectItem(column, expr)]);
      return (cursor, new ResultSchema([new ResultColumn(0, column.Name, column.Type, column.Lineage)], null));
   }

   /// <summary>The value that stands for a record in a result: its entity's display column, or its first column.</summary>
   private static PlanColumnRef? Display(RowValue record)
   {
      string? name = record.Shape.Entity?.DisplayColumn?.Name ?? record.Shape.Columns.FirstOrDefault()?.Name;
      if (name == null) { return null; }
      PlanColumnRef column = record.Column(name);
      return record.Nullable && !column.Type.Nullable ? new PlanColumnRef(column.Column, column.Type.AsNullable()) : column;
   }

   #endregion

   #region Queries

   private (Cursor, RowValue) Query(BoundQuery query)
   {
      switch (query)
      {
         case BoundEntityScan scan:
            return Scan(scan.Entity, pathPrefix, nullable: false);
         case BoundLetQuery let:
            return Query((BoundQuery)let.Let.Value);
         case BoundWhere where:
         {
            (Cursor cursor, RowValue row) = Query(where.Input);
            PlanExpr predicate;
            using (Bind(where.Row, row)) { predicate = Scalar(where.Predicate); }
            cursor.Node = new FilterNode(cursor.Node, predicate);
            return (cursor, row);
         }
         case BoundOrderBy order:
         {
            (Cursor cursor, RowValue row) = Query(order.Input);
            List<PlanSortKey> keys;
            using (Bind(order.Row, row)) { keys = order.Keys.Select(k => new PlanSortKey(Scalar(k.Expr), k.Descending)).ToList(); }
            cursor.Node = new SortNode(cursor.Node, keys);
            return (cursor, row);
         }
         case BoundTake take:
         {
            (Cursor cursor, RowValue row) = Query(take.Input);
            cursor.Node = Take(cursor.Node, Scalar(take.Count));
            return (cursor, row);
         }
         case BoundSkip skip:
         {
            (Cursor cursor, RowValue row) = Query(skip.Input);
            cursor.Node = Skip(cursor.Node, Scalar(skip.Count));
            return (cursor, row);
         }
         case BoundDistinct distinct:
         {
            (Cursor cursor, RowValue row) = Query(distinct.Input);
            List<ProjectItem> items = row.Columns().Select(c => new ProjectItem(c, new PlanColumnRef(c))).ToList();
            cursor.Node = new DistinctNode(Project(cursor.Node, items));
            return (cursor, row.Detached(cursor));
         }
         case BoundSelect select:
            return Select(select);
         case BoundExtend extend:
            return Extend(extend);
         default:
            throw new NotSupportedException($"Lowering {query.GetType().Name} is not supported yet");
      }
   }

   private (Cursor, RowValue) Select(BoundSelect select)
   {
      (Cursor cursor, RowValue input) = Query(select.Input);
      RowValue output = new(cursor, select.Shape, [], nullable: false);
      List<ProjectItem> items = [];
      using (Bind(select.Row, input))
      {
         foreach (BoundProjection item in select.Items)
         {
            switch (Value(item.Expr))
            {
               case PlanExpr expr:
                  PlanColumn column = Output(item.Name, expr, item.Expr.Scalar, Lineage(expr, item.Expr.Syntax));
                  items.Add(new ProjectItem(column, expr));
                  output.Set(item.Name, new PlanColumnRef(column));
                  break;
               case RowValue record:
                  output.Set(item.Name, Forward(record, items, cursor));
                  break;
            }
         }
      }
      cursor.Node = new ProjectNode(cursor.Node, items);
      return (cursor, output);
   }

   private (Cursor, RowValue) Extend(BoundExtend extend)
   {
      (Cursor cursor, RowValue input) = Query(extend.Input);
      RowValue output = input.Extended(extend.Shape);
      List<ProjectItem> added = [];
      using (Bind(extend.Row, input))
      {
         foreach (BoundProjection item in extend.Items)
         {
            switch (Value(item.Expr))
            {
               case PlanExpr expr:
                  PlanColumn column = Output(item.Name, expr, item.Expr.Scalar, Lineage(expr, item.Expr.Syntax));
                  if (!ReferenceEquals(column, (expr as PlanColumnRef)?.Column)) { added.Add(new ProjectItem(column, expr)); }
                  output.Set(item.Name, new PlanColumnRef(column));
                  break;
               case RowValue record:
                  output.Set(item.Name, record);
                  break;
            }
         }
      }
      // Everything below passes through, joined navigations included, so the rows keep them.
      List<ProjectItem> items = cursor.Node.Output.Select(c => new ProjectItem(c, new PlanColumnRef(c))).ToList();
      items.AddRange(added);
      cursor.Node = new ProjectNode(cursor.Node, items);
      return (cursor, output);
   }

   /// <summary>Carries a record's columns through a projection, as new items; the copy joins navigations afresh.</summary>
   private RowValue Forward(RowValue record, List<ProjectItem> items, Cursor cursor)
   {
      RowValue copy = new(cursor, record.Shape, record.Path, record.Nullable);
      foreach (ShapeMember member in record.Shape.Members)
      {
         switch (member)
         {
            case ColumnMember:
               PlanColumnRef value = record.Column(member.Name);
               PlanColumn column = Output(member.Name, value, value.Type, value.Column.Lineage);
               if (!items.Any(i => ReferenceEquals(i.Column, column))) { items.Add(new ProjectItem(column, value)); }
               PlanColumnRef forwarded = new(column);
               copy.Set(member.Name, forwarded);
               if (record.Indicator is PlanColumnRef indicator && ReferenceEquals(indicator.Column, value.Column)) { copy.Indicator = forwarded; }
               break;
            case RecordMember nested:
               copy.Set(member.Name, Forward(record.Record(nested.Name), items, cursor));
               break;
         }
      }
      return copy;
   }

   /// <summary>A projection, or just the input when the items pass its columns through unchanged and in order.</summary>
   private static PlanNode Project(PlanNode input, List<ProjectItem> items)
   {
      IReadOnlyList<PlanColumn> output = input.Output;
      bool same = items.Count == output.Count;
      for (int i = 0; same && i < items.Count; i++) { same = items[i].IsPassThrough && ReferenceEquals(items[i].Column, output[i]); }
      return same ? input : new ProjectNode(input, items);
   }

   /// <summary>The column a projection item produces: the input column itself when name and type are unchanged.</summary>
   private PlanColumn Output(string name, PlanExpr expr, ScalarType type, ColumnLineage lineage) =>
      expr is PlanColumnRef reference && string.Equals(reference.Column.Name, name, StringComparison.Ordinal) && reference.Column.Type == type
         ? reference.Column
         : new PlanColumn(ids.Next(), name, type, lineage);

   private static PlanNode Take(PlanNode input, PlanExpr count)
   {
      if (input is LimitNode { Count: null } skip) { return new LimitNode(skip.Input, count, skip.Offset); }
      if (input is LimitNode { Count: PlanLiteral { Value: long before } } limit && count is PlanLiteral { Value: long now })
      {
         return new LimitNode(limit.Input, Int64(Math.Min(before, now)), limit.Offset);
      }
      return new LimitNode(input, count, null);
   }

   private static PlanNode Skip(PlanNode input, PlanExpr offset)
   {
      if (input is LimitNode limit && offset is PlanLiteral { Value: long skipped } &&
          limit.Offset is null or PlanLiteral { Value: long } && limit.Count is null or PlanLiteral { Value: long })
      {
         // Skipping more of a constant window: skip(a).skip(b) is skip(a + b), take(n).skip(b) takes n - b after b.
         long before = limit.Offset is PlanLiteral { Value: long o } ? o : 0;
         PlanExpr? count = limit.Count is PlanLiteral { Value: long c } ? Int64(Math.Max(c - skipped, 0)) : null;
         return new LimitNode(limit.Input, count, Int64(before + skipped));
      }
      return new LimitNode(input, null, offset);
   }

   private static PlanLiteral Int64(long value) => new(value, ScalarType.Int64.AsNonNullable());

   #endregion

   #region Entities and navigations

   private (Cursor, RowValue) Scan(EntityDef entity, IReadOnlyList<NavigationDef> path, bool nullable)
   {
      switch (entity)
      {
         case TableEntity table:
         {
            List<ScanColumn> columns = table.Columns
               .Select(c => new ScanColumn(c, new PlanColumn(ids.Next(), c.Name, c.Type.WithNullable(c.Type.Nullable || nullable),
                                                             ColumnLineage.Direct(new ColumnSource(c, path)))))
               .ToList();
            Cursor cursor = new(new ScanNode(table, columns));
            RowValue row = new(cursor, RowShape.ForEntity(table), path, nullable);
            foreach (ScanColumn column in columns) { row.Set(column.Column.Name, new PlanColumnRef(column.Output)); }
            return (cursor, row);
         }
         case VirtualEntity { Definition.Query: { } definition } virtualEntity:
         {
            Lowerer nested = new(ids, virtualEntity.QueryText, spans: false, path);
            (Cursor cursor, RowValue inner) = nested.Query(definition);
            RowValue row = new(cursor, RowShape.ForEntity(virtualEntity), path, nullable,
                               virtualEntity.BaseEntity != null ? inner.Navigations : null);
            foreach (ColumnDef column in virtualEntity.Columns)
            {
               PlanColumnRef value = inner.Column(column.Name);
               row.Set(column.Name, nullable && !value.Type.Nullable ? new PlanColumnRef(value.Column, value.Type.AsNullable()) : value);
            }
            return (cursor, row);
         }
         default:
            throw new InvalidOperationException($"{entity.DisplayName} has no usable definition");
      }
   }

   /// <summary>The target row of a many-to-one navigation, joined onto the owner's plan the first time it is used.</summary>
   private RowValue Navigate(RowValue owner, NavigationDef navigation)
   {
      if (owner.Navigations.TryGetValue(navigation, out RowValue? joined)) { return joined; }
      if (navigation.IsCollection) { throw new NotSupportedException($"The collection navigation {navigation} can't be joined as a row"); }

      bool outer = navigation.Multiplicity != Multiplicity.One || owner.Nullable;
      (Cursor targetCursor, RowValue target) = Scan(navigation.Target, [.. owner.Path, navigation], outer);
      PlanExpr? condition = null;
      for (int i = 0; i < navigation.OwnerColumns.Count; i++)
      {
         PlanColumnRef left = owner.Column(navigation.OwnerColumns[i].Name);
         PlanColumnRef right = target.Column(navigation.TargetColumns[i].Name);
         PlanExpr equal = new PlanBinary(BinaryOp.Equal, left, right, ScalarType.Boolean.WithNullable(left.Type.Nullable || right.Type.Nullable));
         condition = condition == null ? equal : And(condition, equal);
      }
      Cursor cursor = owner.Cursor;
      cursor.Node = new JoinNode(outer ? JoinKind.Left : JoinKind.Inner, cursor.Node, targetCursor.Node, condition, navigation);
      target.Rehome(cursor);
      // The joined row's key matched an owner value with =, so it is never null in a row that exists.
      target.Indicator = target.Column(navigation.TargetColumns[0].Name);
      owner.Navigations[navigation] = target;
      return target;
   }

   #endregion

   #region Expressions

   /// <summary>A member or row reference as a plan value: a column reference, or the row value of a record.</summary>
   private object Value(BoundExpr expr)
   {
      switch (expr)
      {
         case BoundRowRef reference:
            return rows.TryGetValue(reference.Row, out RowValue? row)
               ? row
               : throw new InvalidOperationException($"The row '{reference.Row}' is not in scope");
         case BoundMemberAccess access:
            RowValue target = (RowValue)Value(access.Target);
            return access.Member switch
            {
               ColumnMember column => new PlanColumnRef(target.Column(column.Name).Column, access.Scalar),
               NavigationMember navigation => Navigate(target, navigation.Navigation),
               RecordMember record => target.Record(record.Name),
               _ => throw new InvalidOperationException($"Unexpected member {access.Member}"),
            };
         default:
            return Scalar(expr);
      }
   }

   private PlanExpr Scalar(BoundExpr expr)
   {
      switch (expr)
      {
         case BoundLiteral literal:
            return new PlanLiteral(literal.Value, literal.Scalar);
         case BoundParameter parameter:
            return new PlanParameter(ParameterSource.User, parameter.Name, parameter.Scalar);
         case BoundLetValue let:
            return Scalar((BoundExpr)let.Let.Value);
         case BoundMemberAccess:
            return Value(expr) as PlanExpr ?? throw new InvalidOperationException($"{BoundTreePrinter.Expr(expr)} is not a scalar");
         case BoundUnary unary:
            return new PlanUnary(unary.Op, Scalar(unary.Operand), unary.Scalar);
         case BoundBinary binary:
            return new PlanBinary(binary.Op, Scalar(binary.Left), Scalar(binary.Right), binary.Scalar);
         case BoundIsNull isNull:
            return isNull.Operand.IsScalar ? new PlanIsNull(Scalar(isNull.Operand), isNull.Negated) : RecordIsNull(isNull.Operand, isNull.Negated);
         case BoundInList inList:
            return new PlanInList(Scalar(inList.Operand), inList.Items.Select(Scalar).ToList(), inList.Negated, inList.Scalar);
         case BoundConditional conditional:
            return new PlanConditional(Scalar(conditional.Condition), Scalar(conditional.WhenTrue), Scalar(conditional.WhenFalse), conditional.Scalar);
         case BoundFunctionCall { Function.Id: FunctionId.Now } now:
            return new PlanParameter(ParameterSource.Now, "now", now.Scalar);
         case BoundFunctionCall { Function.Id: FunctionId.Today } today:
            return new PlanParameter(ParameterSource.Today, "today", today.Scalar);
         case BoundFunctionCall call:
            return new PlanFunction(call.Function, call.Arguments.Select(Scalar).ToList(), call.Scalar, Span(call.Syntax));
         default:
            throw new NotSupportedException($"Lowering {expr.GetType().Name} is not supported yet");
      }
   }

   /// <summary>
   /// <c>record == null</c>. Through an enforced foreign key the key columns tell, with no join; otherwise the joined
   /// row's indicator does (a column that is never null in a row that exists).
   /// </summary>
   private PlanExpr RecordIsNull(BoundExpr operand, bool negated)
   {
      if (operand is BoundMemberAccess { Member: NavigationMember { Navigation: { IsInverse: false, Relation.IsEnforced: true } navigation } } access)
      {
         RowValue owner = (RowValue)Value(access.Target);
         return AnyNull(navigation.OwnerColumns.Select(c => (PlanExpr)owner.Column(c.Name)), negated);
      }
      RowValue record = (RowValue)Value(operand);
      if (!record.Nullable) { return new PlanLiteral(negated, ScalarType.Boolean.AsNonNullable()); }
      if (record.Indicator != null) { return new PlanIsNull(record.Indicator, negated); }
      return AllNull(record.Columns().Select(c => (PlanExpr)new PlanColumnRef(c)), negated);
   }

   private static PlanExpr AnyNull(IEnumerable<PlanExpr> values, bool negated) =>
      values.Select(v => (PlanExpr)new PlanIsNull(v, negated)).Aggregate((a, b) => negated ? And(a, b) : Or(a, b));

   private static PlanExpr AllNull(IEnumerable<PlanExpr> values, bool negated) =>
      values.Select(v => (PlanExpr)new PlanIsNull(v, negated)).Aggregate((a, b) => negated ? Or(a, b) : And(a, b));

   private static PlanExpr And(PlanExpr a, PlanExpr b) =>
      new PlanBinary(BinaryOp.And, a, b, ScalarType.Boolean.WithNullable(a.Type.Nullable || b.Type.Nullable));

   private static PlanExpr Or(PlanExpr a, PlanExpr b) =>
      new PlanBinary(BinaryOp.Or, a, b, ScalarType.Boolean.WithNullable(a.Type.Nullable || b.Type.Nullable));

   #endregion

   #region Lineage

   private ColumnLineage Lineage(PlanExpr expr, SyntaxNode? syntax)
   {
      if (expr is PlanColumnRef reference) { return reference.Column.Lineage; }
      List<ColumnSource> sources = [];
      bool readsColumns = false;
      Collect(expr, sources, ref readsColumns);
      string? expressionText = ExpressionText(syntax);
      return readsColumns ? ColumnLineage.Computed(sources, expressionText) : ColumnLineage.Constant(expressionText);
   }

   private static void Collect(PlanExpr expr, List<ColumnSource> sources, ref bool readsColumns)
   {
      switch (expr)
      {
         case PlanColumnRef reference:
            readsColumns = true;
            sources.AddRange(reference.Column.Lineage.Sources);
            break;
         case PlanUnary unary:
            Collect(unary.Operand, sources, ref readsColumns);
            break;
         case PlanBinary binary:
            Collect(binary.Left, sources, ref readsColumns);
            Collect(binary.Right, sources, ref readsColumns);
            break;
         case PlanIsNull isNull:
            Collect(isNull.Operand, sources, ref readsColumns);
            break;
         case PlanInList inList:
            Collect(inList.Operand, sources, ref readsColumns);
            foreach (PlanExpr item in inList.Items) { Collect(item, sources, ref readsColumns); }
            break;
         case PlanConditional conditional:
            Collect(conditional.Condition, sources, ref readsColumns);
            Collect(conditional.WhenTrue, sources, ref readsColumns);
            Collect(conditional.WhenFalse, sources, ref readsColumns);
            break;
         case PlanFunction call:
            foreach (PlanExpr argument in call.Arguments) { Collect(argument, sources, ref readsColumns); }
            break;
      }
   }

   private string? ExpressionText(SyntaxNode? syntax) =>
      syntax != null && syntax.Start >= 0 && syntax.End <= text.Length && syntax.End > syntax.Start ? text[syntax.Start..syntax.End] : null;

   private SourceSpan? Span(SyntaxNode? syntax) =>
      spans && syntax != null && syntax.End > syntax.Start ? new SourceSpan(syntax.Start, syntax.End) : null;

   #endregion

   private Binding Bind(RowVariable row, RowValue value)
   {
      rows.TryGetValue(row, out RowValue? previous);
      rows[row] = value;
      return new Binding(rows, row, previous);
   }

   /// <summary>Puts a row variable in scope for the arguments of one operator.</summary>
   private readonly struct Binding(Dictionary<RowVariable, RowValue> rows, RowVariable row, RowValue? previous) : IDisposable
   {
      public void Dispose()
      {
         if (previous == null) { rows.Remove(row); }
         else { rows[row] = previous; }
      }
   }
}

internal sealed class PlanIds
{
   private int next;

   public int Next() => ++next;
}
