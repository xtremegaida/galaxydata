using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Planning;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Sql;

public sealed class SqlBuildOptions
{
   public static SqlBuildOptions Default { get; } = new();

   /// <summary>Write constants into the text instead of passing them as parameters; for display only.</summary>
   public bool InlineConstants { get; init; }

   /// <summary>The source's default schema, which table names can leave out.</summary>
   public string? DefaultSchema { get; init; }
}

/// <summary>A language function the dialect has no SQL for.</summary>
public sealed class SqlTranslationException(PlanFunction function, SqlDialect dialect)
   : Exception($"{dialect.Name} can't run {function.Function.Name}(...)")
{
   public PlanFunction Function { get; } = function;

   public SqlDialect Dialect { get; } = dialect;
}

/// <summary>
/// Writes a plan that runs in one database as a single SELECT. Operators fill the clauses of the current SELECT in
/// order; when the next one can't be expressed in it (a filter after a row limit, a sort after DISTINCT) the SELECT
/// so far becomes a derived table. Only the columns something above needs are selected from derived tables.
/// </summary>
internal sealed class SqlBuilder
{
   private readonly SqlDialect dialect;
   private readonly SqlBuildOptions options;
   private readonly List<SqlParameterSlot> parameters = [];
   private readonly Dictionary<(ParameterSource?, object?, string?, ScalarType, PatternTransform?), SqlParameterSlot> slots = [];
   private readonly Dictionary<string, int> aliases = new(StringComparer.OrdinalIgnoreCase);

   private SqlBuilder(SqlDialect dialect, SqlBuildOptions options)
   {
      this.dialect = dialect;
      this.options = options;
   }

   public static SqlStatement Build(LogicalPlan plan, SqlDialect dialect, SqlBuildOptions? options = null)
   {
      ArgumentNullException.ThrowIfNull(plan);
      ArgumentNullException.ThrowIfNull(dialect);
      SqlBuilder builder = new(dialect, options ?? SqlBuildOptions.Default);
      IReadOnlyList<PlanColumn> output = plan.Root.Output;
      Frame frame = builder.Build(plan.Root, [.. output]);
      SqlSelect select = frame.Select;
      select.Items.Clear();
      for (int i = 0; i < output.Count; i++)
      {
         select.Items.Add(new SqlSelectItem(frame.Columns[output[i]], plan.Schema.Columns[i].Name));
      }
      // A SELECT needs at least one item even when the rows have no columns.
      if (select.Items.Count == 0) { select.Items.Add(new SqlSelectItem(new SqlLiteral(1L, ScalarType.Int64), "one")); }
      return new SqlStatement(SqlWriter.Write(select, dialect), builder.parameters);
   }

   /// <summary>A SELECT being filled, and the SQL that computes each available plan column in its scope.</summary>
   private sealed class Frame
   {
      public SqlSelect Select { get; } = new();

      public Dictionary<PlanColumn, SqlExpr> Columns { get; set; } = [];

      /// <summary>Set once DISTINCT applies: the columns it applies to, which the select list must be.</summary>
      public List<PlanColumn>? DistinctColumns { get; set; }

      public bool HasPaging => Select.Limit != null || Select.Offset != null;
   }

   #region Operators

   private Frame Build(PlanNode node, HashSet<PlanColumn> needed)
   {
      switch (node)
      {
         case ScanNode scan:
            return Scan(scan);
         case OneRowNode:
            return new Frame();
         case FilterNode filter:
         {
            HashSet<PlanColumn> below = With(needed, filter.Predicate);
            Frame frame = Build(filter.Input, below);
            if (frame.HasPaging || frame.DistinctColumns != null) { frame = Wrap(frame, filter.Input.Output, below); }
            frame.Select.Where = AndAlso(frame.Select.Where, Condition(filter.Predicate, frame.Columns));
            return frame;
         }
         case ProjectNode project:
            return Project(project, needed);
         case JoinNode join:
            return Join(join, needed);
         case SortNode sort:
         {
            HashSet<PlanColumn> below = With(needed, sort.Keys.Select(k => k.Expr));
            Frame frame = Build(sort.Input, below);
            // After DISTINCT the keys must be selected columns, which they are when they are plain columns.
            bool sortsDistinct = frame.DistinctColumns != null &&
               sort.Keys.All(k => k.Expr is PlanColumnRef reference && frame.DistinctColumns.Contains(reference.Column));
            if (frame.HasPaging || (frame.DistinctColumns != null && !sortsDistinct)) { frame = Wrap(frame, sort.Input.Output, below); }
            frame.Select.OrderBy.Clear();
            foreach (PlanSortKey key in sort.Keys)
            {
               // A key that reads no column is the same for every row, and databases reject constants in ORDER BY.
               SqlExpr sql = Value(key.Expr, frame.Columns);
               if (ReadsColumns(sql)) { frame.Select.OrderBy.Add(new SqlOrderItem(sql, key.Descending, key.Expr.Type.Nullable)); }
            }
            return frame;
         }
         case LimitNode limit:
         {
            Frame frame = Build(limit.Input, needed);
            // OFFSET needs an ORDER BY in SQL Server, and after DISTINCT that can only be on selected columns.
            bool unsortedDistinctOffset = limit.Offset != null && dialect.Paging == PagingStyle.TopOrOffsetFetch &&
               frame.DistinctColumns != null && frame.Select.OrderBy.Count == 0;
            if (frame.HasPaging || unsortedDistinctOffset) { frame = Wrap(frame, limit.Input.Output, needed); }
            frame.Select.Limit = limit.Count == null ? null : Count(limit.Count);
            frame.Select.Offset = limit.Offset == null ? null : Count(limit.Offset);
            return frame;
         }
         case DistinctNode distinct:
         {
            Frame frame = Build(distinct.Input, [.. distinct.Input.Output]);
            if (frame.HasPaging) { frame = Wrap(frame, distinct.Input.Output, [.. distinct.Input.Output]); }
            if (frame.DistinctColumns != null) { return frame; }
            // Rows come out of DISTINCT in no particular order; a sort below it (without a limit) means nothing.
            frame.Select.OrderBy.Clear();
            frame.Select.Distinct = true;
            frame.DistinctColumns = [.. distinct.Input.Output];
            SetItems(frame.Select, frame.DistinctColumns, frame.Columns);
            return frame;
         }
         default:
            throw new NotSupportedException($"SQL for {node.GetType().Name} is not supported yet");
      }
   }

   private Frame Scan(ScanNode scan)
   {
      Frame frame = new();
      TableEntity table = scan.Entity;
      string alias = Alias(table.Table);
      string? schema = string.IsNullOrEmpty(table.Schema) || string.Equals(table.Schema, options.DefaultSchema ?? table.Source.DefaultSchema, StringComparison.Ordinal)
         ? null
         : table.Schema;
      frame.Select.From = new SqlTable(schema, table.Table, alias);
      foreach (ScanColumn column in scan.Columns) { frame.Columns[column.Output] = new SqlColumn(alias, column.Column.Name); }
      return frame;
   }

   private Frame Project(ProjectNode project, HashSet<PlanColumn> needed)
   {
      List<ProjectItem> items = project.Items.Where(i => needed.Contains(i.Column)).ToList();
      HashSet<PlanColumn> below = With([], items.Select(i => i.Expr));
      Frame frame = Build(project.Input, below);
      if (frame.DistinctColumns != null && !PassesThrough(items, frame.DistinctColumns))
      {
         frame = Wrap(frame, frame.DistinctColumns, [.. frame.DistinctColumns]);
      }
      Dictionary<PlanColumn, SqlExpr> columns = [];
      foreach (ProjectItem item in items) { columns[item.Column] = Value(item.Expr, frame.Columns); }
      frame.Columns = columns;
      return frame;
   }

   /// <summary>Whether the items only pass the DISTINCT columns through, all of them; then the SELECT can stay as it is.</summary>
   private static bool PassesThrough(List<ProjectItem> items, List<PlanColumn> distinct) =>
      items.All(i => i.IsPassThrough) && items.Count == distinct.Count && distinct.All(c => items.Any(i => ReferenceEquals(i.Column, c)));

   private Frame Join(JoinNode join, HashSet<PlanColumn> needed)
   {
      if (join.Kind is not (JoinKind.Inner or JoinKind.Left)) { throw new NotSupportedException($"SQL for {join.Kind} joins is not supported yet"); }
      HashSet<PlanColumn> all = join.Condition == null ? needed : With(needed, join.Condition);
      HashSet<PlanColumn> leftOutput = [.. join.Left.Output];
      HashSet<PlanColumn> leftNeeded = [.. all.Where(leftOutput.Contains)];
      HashSet<PlanColumn> rightNeeded = [.. all.Where(c => !leftOutput.Contains(c))];

      Frame left = Build(join.Left, leftNeeded);
      if (left.HasPaging || left.DistinctColumns != null) { left = Wrap(left, join.Left.Output, leftNeeded); }
      Frame right = Build(join.Right, rightNeeded);
      if (!right.HasPaging) { right.Select.OrderBy.Clear(); }
      // Values computed from an outer-joined row must be computed before the join: after it, a missing row's
      // columns are null, and coalesce(city, 'none') would give 'none' where the whole value should be null.
      bool computed = join.Kind == JoinKind.Left && rightNeeded.Any(c => right.Columns[c] is not SqlColumn);
      if (right.Select.From is not SqlTable || right.HasPaging || right.DistinctColumns != null || computed)
      {
         right = Wrap(right, join.Right.Output, rightNeeded);
         right.Select.OrderBy.Clear();
      }

      Dictionary<PlanColumn, SqlExpr> columns = new(left.Columns);
      foreach ((PlanColumn column, SqlExpr sql) in right.Columns) { columns[column] = sql; }
      // A table's own filter can join it in the ON clause: the same rows, for inner and left joins alike.
      SqlExpr? condition = AndAlso(join.Condition == null ? null : Condition(join.Condition, columns), right.Select.Where);
      left.Select.From = new SqlJoin(join.Kind == JoinKind.Inner ? SqlJoinKind.Inner : SqlJoinKind.Left, left.Select.From!, right.Select.From!, condition);
      left.Columns = columns;
      return left;
   }

   /// <summary>
   /// Makes the SELECT so far a derived table with the needed columns. Its order is kept: the sort keys are selected
   /// too and the outer query sorts on them, while the inner one keeps ORDER BY only when it limits the rows.
   /// </summary>
   private Frame Wrap(Frame inner, IReadOnlyList<PlanColumn> available, HashSet<PlanColumn> needed)
   {
      SqlSelect select = inner.Select;
      List<PlanColumn> columns = inner.DistinctColumns ?? available.Where(needed.Contains).ToList();
      string alias = Alias("d");
      Frame outer = new();
      HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
      select.Items.Clear();
      foreach (PlanColumn column in columns)
      {
         string name = Unique(names, column.Name);
         select.Items.Add(new SqlSelectItem(inner.Columns[column], name));
         outer.Columns[column] = new SqlColumn(alias, name);
      }
      foreach (SqlOrderItem key in select.OrderBy)
      {
         string? name = select.Items.FirstOrDefault(i => ReferenceEquals(i.Expr, key.Expr))?.Alias;
         if (name == null)
         {
            name = Unique(names, "sort_key");
            select.Items.Add(new SqlSelectItem(key.Expr, name));
         }
         outer.Select.OrderBy.Add(key with { Expr = new SqlColumn(alias, name) });
      }
      if (!inner.HasPaging) { select.OrderBy.Clear(); }
      outer.Select.From = new SqlDerivedTable(select, alias);
      return outer;
   }

   private static void SetItems(SqlSelect select, IEnumerable<PlanColumn> columns, Dictionary<PlanColumn, SqlExpr> sql)
   {
      HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
      select.Items.Clear();
      foreach (PlanColumn column in columns) { select.Items.Add(new SqlSelectItem(sql[column], Unique(names, column.Name))); }
   }

   private static string Unique(HashSet<string> names, string name)
   {
      string candidate = name;
      for (int n = 2; !names.Add(candidate); n++) { candidate = $"{name}_{n}"; }
      return candidate;
   }

   /// <summary>A readable table alias: the table's first letter (<c>o</c>, <c>c</c>), numbered when taken (<c>c2</c>).</summary>
   private string Alias(string table)
   {
      char first = table.FirstOrDefault(char.IsAsciiLetter);
      string stem = first == default ? "t" : char.ToLowerInvariant(first).ToString();
      int count = aliases.TryGetValue(stem, out int seen) ? seen + 1 : 1;
      aliases[stem] = count;
      return count == 1 ? stem : stem + count;
   }

   private static HashSet<PlanColumn> With(HashSet<PlanColumn> needed, PlanExpr expr) => With(needed, [expr]);

   private static HashSet<PlanColumn> With(HashSet<PlanColumn> needed, IEnumerable<PlanExpr> exprs)
   {
      HashSet<PlanColumn> result = [.. needed];
      foreach (PlanExpr expr in exprs) { AddColumns(expr, result); }
      return result;
   }

   private static void AddColumns(PlanExpr expr, HashSet<PlanColumn> columns)
   {
      switch (expr)
      {
         case PlanColumnRef reference:
            columns.Add(reference.Column);
            break;
         case PlanUnary unary:
            AddColumns(unary.Operand, columns);
            break;
         case PlanBinary binary:
            AddColumns(binary.Left, columns);
            AddColumns(binary.Right, columns);
            break;
         case PlanIsNull isNull:
            AddColumns(isNull.Operand, columns);
            break;
         case PlanInList inList:
            AddColumns(inList.Operand, columns);
            foreach (PlanExpr item in inList.Items) { AddColumns(item, columns); }
            break;
         case PlanConditional conditional:
            AddColumns(conditional.Condition, columns);
            AddColumns(conditional.WhenTrue, columns);
            AddColumns(conditional.WhenFalse, columns);
            break;
         case PlanFunction call:
            foreach (PlanExpr argument in call.Arguments) { AddColumns(argument, columns); }
            break;
      }
   }

   #endregion

   #region Expressions

   internal SqlExpr Value(PlanExpr expr, IReadOnlyDictionary<PlanColumn, SqlExpr> columns) => Adapt(Translate(expr, columns), expr, predicate: false);

   internal SqlExpr Condition(PlanExpr expr, IReadOnlyDictionary<PlanColumn, SqlExpr> columns) => Adapt(Translate(expr, columns), expr, predicate: true);

   /// <summary>Where conditions aren't values (SQL Server), converts between the two as the position needs.</summary>
   private SqlExpr Adapt(SqlExpr sql, PlanExpr expr, bool predicate)
   {
      if (dialect.HasBooleanValues || expr.Type.Kind != ScalarKind.Boolean || sql.IsPredicate == predicate) { return sql; }
      SqlLiteral one = new(1L, ScalarType.Int64);
      if (predicate)
      {
         return sql is SqlLiteral { Value: bool flag }
            ? new SqlBinary(SqlBinaryOp.Equal, one, new SqlLiteral(flag ? 1L : 0L, ScalarType.Int64))
            : new SqlBinary(SqlBinaryOp.Equal, sql, one);
      }
      SqlLiteral yes = new(true, ScalarType.Boolean);
      SqlLiteral no = new(false, ScalarType.Boolean);
      SqlExpr negated = sql is SqlUnary { Op: SqlUnaryOp.Not } not ? not.Operand : new SqlUnary(SqlUnaryOp.Not, sql);
      return expr.Type.Nullable
         ? new SqlCase([new SqlWhen(sql, yes), new SqlWhen(negated, no)], null)
         : new SqlCase([new SqlWhen(sql, yes)], no);
   }

   private SqlExpr Translate(PlanExpr expr, IReadOnlyDictionary<PlanColumn, SqlExpr> columns)
   {
      switch (expr)
      {
         case PlanColumnRef reference:
            return columns.TryGetValue(reference.Column, out SqlExpr? column)
               ? column
               : throw new InvalidOperationException($"The column {reference.Column} is not available here");
         case PlanLiteral literal:
            return Constant(literal.Value, literal.Type);
         case PlanParameter parameter:
            return new SqlParameterRef(Slot(parameter.Source, null, parameter.Source == ParameterSource.User ? parameter.Name : null, parameter.Type, null));
         case PlanUnary unary:
            return unary.Op switch
            {
               UnaryOp.Negate => new SqlUnary(SqlUnaryOp.Negate, Value(unary.Operand, columns)),
               UnaryOp.Not => new SqlUnary(SqlUnaryOp.Not, Condition(unary.Operand, columns)),
               _ => Value(unary.Operand, columns),
            };
         case PlanBinary binary:
            return Binary(binary, columns);
         case PlanIsNull isNull:
            return new SqlIsNull(Value(isNull.Operand, columns), isNull.Negated);
         case PlanInList inList:
            return new SqlIn(Value(inList.Operand, columns), inList.Items.Select(i => Value(i, columns)).ToList(), inList.Negated);
         case PlanConditional conditional:
            return new SqlCase([new SqlWhen(Condition(conditional.Condition, columns), Value(conditional.WhenTrue, columns))], Value(conditional.WhenFalse, columns));
         case PlanFunction call:
            return dialect.Function(new SqlCall(this, call, columns)) ?? throw new SqlTranslationException(call, dialect);
         default:
            throw new NotSupportedException($"SQL for {expr.GetType().Name} is not supported yet");
      }
   }

   private SqlExpr Binary(PlanBinary binary, IReadOnlyDictionary<PlanColumn, SqlExpr> columns)
   {
      if (binary.Op is BinaryOp.And or BinaryOp.Or)
      {
         return new SqlBinary(binary.Op == BinaryOp.And ? SqlBinaryOp.And : SqlBinaryOp.Or, Condition(binary.Left, columns), Condition(binary.Right, columns));
      }
      SqlExpr left = Value(binary.Left, columns);
      SqlExpr right = Value(binary.Right, columns);
      ScalarType leftType = binary.Left.Type;
      ScalarType rightType = binary.Right.Type;
      if (binary.Op == BinaryOp.Divide) { return dialect.Divide(left, right, leftType, rightType); }
      if (binary.Op == BinaryOp.Modulo) { return dialect.Modulo(left, right, leftType, rightType); }
      SqlBinaryOp op = binary.Op switch
      {
         BinaryOp.Add => SqlBinaryOp.Add,
         BinaryOp.Subtract => SqlBinaryOp.Subtract,
         BinaryOp.Multiply => SqlBinaryOp.Multiply,
         BinaryOp.Concat => SqlBinaryOp.Concat,
         BinaryOp.Equal => SqlBinaryOp.Equal,
         BinaryOp.NotEqual => SqlBinaryOp.NotEqual,
         BinaryOp.Less => SqlBinaryOp.Less,
         BinaryOp.LessOrEqual => SqlBinaryOp.LessOrEqual,
         BinaryOp.Greater => SqlBinaryOp.Greater,
         BinaryOp.GreaterOrEqual => SqlBinaryOp.GreaterOrEqual,
         _ => throw new NotSupportedException($"SQL for {binary.Op} is not supported"),
      };
      return OperatorText.IsComparison(binary.Op) ? dialect.Compare(op, left, right, leftType, rightType) : new SqlBinary(op, left, right);
   }

   /// <summary>Null and booleans are written into the SQL; everything else from the query text is a parameter.</summary>
   private SqlExpr Constant(object? value, ScalarType type) =>
      value is null or bool || options.InlineConstants
         ? new SqlLiteral(value, type)
         : new SqlParameterRef(Slot(null, value, null, type, null));

   /// <summary>Row counts are written into the SQL when they are constants.</summary>
   private SqlExpr Count(PlanExpr count) => count switch
   {
      PlanLiteral literal => new SqlLiteral(literal.Value, literal.Type),
      _ => Translate(count, new Dictionary<PlanColumn, SqlExpr>()),
   };

   internal SqlExpr PatternParameter(PlanExpr text, PatternTransform pattern) => text switch
   {
      PlanLiteral { Value: string value } literal => new SqlParameterRef(Slot(null, value, null, literal.Type, pattern)),
      PlanParameter { Source: ParameterSource.User } parameter => new SqlParameterRef(Slot(ParameterSource.User, null, parameter.Name, parameter.Type, pattern)),
      _ => throw new InvalidOperationException("Only constants and parameters can become patterns"),
   };

   private SqlParameterSlot Slot(ParameterSource? source, object? constant, string? name, ScalarType type, PatternTransform? pattern)
   {
      var key = (source, constant, name, type, pattern);
      if (!dialect.SharesParameters || !slots.TryGetValue(key, out SqlParameterSlot? slot))
      {
         slot = new SqlParameterSlot("p" + parameters.Count, type, source, constant, name, pattern);
         slots[key] = slot;
         parameters.Add(slot);
      }
      return slot;
   }

   #endregion

   private static SqlExpr? AndAlso(SqlExpr? a, SqlExpr? b) => a == null ? b : b == null ? a : new SqlBinary(SqlBinaryOp.And, a, b);

   private static bool ReadsColumns(SqlExpr expr) => expr switch
   {
      SqlColumn => true,
      SqlUnary unary => ReadsColumns(unary.Operand),
      SqlBinary binary => ReadsColumns(binary.Left) || ReadsColumns(binary.Right),
      SqlIsNull isNull => ReadsColumns(isNull.Operand),
      SqlIn inList => ReadsColumns(inList.Operand) || inList.Items.Any(ReadsColumns),
      SqlBetween between => ReadsColumns(between.Operand) || ReadsColumns(between.Low) || ReadsColumns(between.High),
      SqlLike like => ReadsColumns(like.Operand) || ReadsColumns(like.Pattern),
      SqlCase caseExpr => caseExpr.Whens.Any(w => ReadsColumns(w.Condition) || ReadsColumns(w.Result)) || (caseExpr.Else != null && ReadsColumns(caseExpr.Else)),
      SqlCast cast => ReadsColumns(cast.Operand),
      SqlFunctionCall call => call.Arguments.Any(ReadsColumns),
      SqlTemplate template => template.Arguments.Any(ReadsColumns),
      _ => false,
   };
}
