using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Planning;
using GalaxyData.Query.Planning.Federation;
using GalaxyData.Query.Planning.Optimizer;
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
   private readonly HashSet<string> usedAliases = new(StringComparer.OrdinalIgnoreCase);

   /// <summary>The columns of the queries around the subquery being written, innermost last: what correlated references see.</summary>
   private readonly List<IReadOnlyDictionary<PlanColumn, SqlExpr>> outer = [];

   /// <summary>For the merge engine's SQL: the columns read from each fragment's table.</summary>
   private readonly Dictionary<MergeTableNode, HashSet<PlanColumn>>? reads;

   private SqlBuilder(SqlDialect dialect, SqlBuildOptions options, Dictionary<MergeTableNode, HashSet<PlanColumn>>? reads = null)
   {
      this.dialect = dialect;
      this.options = options;
      this.reads = reads;
   }

   public static SqlStatement Build(LogicalPlan plan, SqlDialect dialect, SqlBuildOptions? options = null)
   {
      ArgumentNullException.ThrowIfNull(plan);
      ArgumentNullException.ThrowIfNull(dialect);
      return new SqlBuilder(dialect, options ?? SqlBuildOptions.Default).Result(plan);
   }

   /// <summary>The merge engine's SQL for a plan over fragments' tables; <paramref name="reads"/> gets the columns it reads from each.</summary>
   public static SqlStatement BuildMerge(LogicalPlan plan, SqlDialect dialect, Dictionary<MergeTableNode, HashSet<PlanColumn>> reads)
   {
      ArgumentNullException.ThrowIfNull(plan);
      ArgumentNullException.ThrowIfNull(dialect);
      return new SqlBuilder(dialect, SqlBuildOptions.Default, reads).Result(plan);
   }

   /// <summary>
   /// A fragment's SQL: the plan's rows with the given columns under the given names. The rows go into a table, where
   /// order means nothing, so there is an ORDER BY only when it decides which rows a limit keeps.
   /// </summary>
   public static SqlStatement BuildFragment(PlanNode root, IReadOnlyList<PlanColumn> columns, IReadOnlyList<string> names, SqlDialect dialect, SqlBuildOptions? options = null)
   {
      ArgumentNullException.ThrowIfNull(root);
      ArgumentNullException.ThrowIfNull(dialect);
      SqlBuilder builder = new(dialect, options ?? SqlBuildOptions.Default);
      Frame frame = builder.Build(root, [.. columns]);
      // Selecting fewer columns than DISTINCT applies to would merge rows that differ in the others.
      if (frame.DistinctColumns != null && !(frame.DistinctColumns.Count == columns.Count && frame.DistinctColumns.All(columns.Contains)))
      {
         frame = builder.Wrap(frame, root.Output, [.. root.Output]);
      }
      if (!frame.HasPaging) { frame.Select.OrderBy.Clear(); }
      return builder.Write(frame, columns, names);
   }

   private SqlStatement Result(LogicalPlan plan)
   {
      IReadOnlyList<PlanColumn> output = plan.Root.Output;
      Frame frame = Build(plan.Root, [.. output]);
      return Write(frame, output, plan.Schema.Columns.Select(c => c.Name).ToList());
   }

   private SqlStatement Write(Frame frame, IReadOnlyList<PlanColumn> columns, IReadOnlyList<string> names)
   {
      SqlSelect select = frame.Select;
      select.Items.Clear();
      for (int i = 0; i < columns.Count; i++) { select.Items.Add(new SqlSelectItem(frame.Columns[columns[i]], names[i])); }
      // A SELECT needs at least one item even when the rows have no columns.
      if (select.Items.Count == 0) { select.Items.Add(Placeholder(frame, "one")); }
      string text = SqlWriter.Write(select, dialect, out IReadOnlyList<SqlParameterSlot> written);
      return new SqlStatement(text, written);
   }

   /// <summary>
   /// The item of a SELECT whose values nobody reads (EXISTS, an empty row): 1, or count(*) for an aggregate of all
   /// rows, which must stay an aggregate to give its one row.
   /// </summary>
   private SqlSelectItem Placeholder(Frame frame, string? alias) =>
      new(frame.Aggregated && frame.Select.GroupBy.Count == 0 ? new SqlAggregate(dialect.AggregateName("count"), null) : new SqlLiteral(1L, ScalarType.Int64), alias);

   /// <summary>A whole (sub)plan as a SELECT whose items are the plan's output columns, named after them.</summary>
   private SqlSelect Finished(PlanNode plan, out List<string> names)
   {
      Frame frame = Build(plan, [.. plan.Output]);
      // Order means nothing in a subquery unless it limits the rows.
      if (!frame.HasPaging) { frame.Select.OrderBy.Clear(); }
      HashSet<string> unique = new(StringComparer.OrdinalIgnoreCase);
      names = plan.Output.Select(c => Unique(unique, c.Name)).ToList();
      frame.Select.Items.Clear();
      for (int i = 0; i < plan.Output.Count; i++) { frame.Select.Items.Add(new SqlSelectItem(frame.Columns[plan.Output[i]], names[i])); }
      if (frame.Select.Items.Count == 0) { frame.Select.Items.Add(Placeholder(frame, "one")); }
      return frame.Select;
   }

   /// <summary>Writes a subquery with the current columns visible to its correlated references.</summary>
   private T Nested<T>(IReadOnlyDictionary<PlanColumn, SqlExpr> columns, Func<T> build)
   {
      outer.Add(columns);
      try
      {
         return build();
      }
      finally
      {
         outer.RemoveAt(outer.Count - 1);
      }
   }

   /// <summary>A SELECT being filled, and the SQL that computes each available plan column in its scope.</summary>
   private sealed class Frame
   {
      public SqlSelect Select { get; } = new();

      public Dictionary<PlanColumn, SqlExpr> Columns { get; set; } = [];

      /// <summary>Set once DISTINCT applies: the columns it applies to, which the select list must be.</summary>
      public List<PlanColumn>? DistinctColumns { get; set; }

      /// <summary>The SELECT groups (or aggregates all rows): conditions go to HAVING, joins need a new SELECT.</summary>
      public bool Aggregated { get; set; }

      public bool HasPaging => Select.Limit != null || Select.Offset != null;
   }

   #region Operators

   private Frame Build(PlanNode node, HashSet<PlanColumn> needed)
   {
      switch (node)
      {
         case ScanNode scan:
            return Scan(scan);
         case MergeTableNode table:
            return MergeTable(table, needed);
         case OneRowNode:
            return new Frame();
         case FilterNode filter:
         {
            HashSet<PlanColumn> below = With(needed, filter.Predicate);
            Frame frame = Build(filter.Input, below);
            // A subquery can't refer to the aggregates of the query around it; they become columns first.
            bool aggregatesInSubquery = frame.Aggregated && PlanRewriter.ContainsSubquery(filter.Predicate);
            if (frame.HasPaging || frame.DistinctColumns != null || aggregatesInSubquery) { frame = Wrap(frame, filter.Input.Output, below); }
            SqlExpr condition = Condition(filter.Predicate, frame.Columns);
            if (frame.Aggregated) { frame.Select.Having = AndAlso(frame.Select.Having, condition); }
            else { frame.Select.Where = AndAlso(frame.Select.Where, condition); }
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
         case AggregateNode aggregate:
            return Aggregate(aggregate, needed);
         case SetOpNode set:
            return SetOp(set);
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
      // A table in a database shared with other sources is named in full.
      string? schema = table.Source.Catalog == null &&
                       (string.IsNullOrEmpty(table.Schema) || string.Equals(table.Schema, options.DefaultSchema ?? table.Source.DefaultSchema, StringComparison.Ordinal))
         ? null
         : table.Schema;
      frame.Select.From = new SqlTable(schema, table.Table, alias) { Catalog = table.Source.Catalog };
      foreach (ScanColumn column in scan.Columns) { frame.Columns[column.Output] = new SqlColumn(alias, column.Column.Name); }
      return frame;
   }

   private Frame MergeTable(MergeTableNode table, HashSet<PlanColumn> needed)
   {
      if (reads != null)
      {
         if (!reads.TryGetValue(table, out HashSet<PlanColumn>? read)) { reads[table] = read = []; }
         read.UnionWith(table.Output.Where(needed.Contains));
      }
      Frame frame = new();
      // A merge table is used once, and its name (f1) reads best as its alias too.
      string alias = usedAliases.Add(table.Table) ? table.Table : Alias(table.Table);
      frame.Select.From = new SqlTable(null, table.Table, alias);
      for (int i = 0; i < table.Output.Count; i++) { frame.Columns[table.Output[i]] = new SqlColumn(alias, table.Names[i]); }
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
      else if (frame.Aggregated && items.Any(i => PlanRewriter.ContainsSubquery(i.Expr)))
      {
         frame = Wrap(frame, project.Input.Output, below);
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
      HashSet<PlanColumn> all = join.Condition == null ? needed : With(needed, join.Condition);
      HashSet<PlanColumn> leftOutput = [.. join.Left.Output];
      HashSet<PlanColumn> leftNeeded = [.. all.Where(leftOutput.Contains)];
      HashSet<PlanColumn> rightOutput = [.. join.Right.Output];
      HashSet<PlanColumn> rightNeeded = [.. all.Where(rightOutput.Contains)];
      if (join.Kind is JoinKind.Inner or JoinKind.Left && PlanAnalysis.FreeColumns(join.Right).Overlaps(leftOutput))
      {
         throw new NotSupportedException("The rows joined depend on each row they are joined to in a way that can't be written as a join condition");
      }

      Frame left = Build(join.Left, leftNeeded);
      if (left.HasPaging || left.DistinctColumns != null || left.Aggregated) { left = Wrap(left, join.Left.Output, leftNeeded); }
      if (join.Kind is JoinKind.Semi or JoinKind.Anti) { return SemiJoin(join, left); }
      Frame right = Build(join.Right, rightNeeded);
      if (!right.HasPaging) { right.Select.OrderBy.Clear(); }
      // Values computed from an outer-joined row must be computed before the join: after it, a missing row's
      // columns are null, and coalesce(city, 'none') would give 'none' where the whole value should be null.
      bool computed = join.Kind == JoinKind.Left && rightNeeded.Any(c => right.Columns[c] is not SqlColumn);
      if (right.Select.From is not SqlTable || right.HasPaging || right.DistinctColumns != null || right.Aggregated || computed)
      {
         right = Wrap(right, join.Right.Output, rightNeeded);
         right.Select.OrderBy.Clear();
      }

      Dictionary<PlanColumn, SqlExpr> columns = new(left.Columns);
      foreach ((PlanColumn column, SqlExpr sql) in right.Columns) { columns[column] = sql; }
      // A table's own filter restricts the rows it joins with: in ON for a left join; for an inner join WHERE says the same.
      SqlExpr? condition = join.Condition == null ? null : Condition(join.Condition, columns);
      if (join.Kind == JoinKind.Left) { condition = AndAlso(condition, right.Select.Where); }
      else { left.Select.Where = AndAlso(left.Select.Where, right.Select.Where); }
      left.Select.From = new SqlJoin(join.Kind == JoinKind.Inner ? SqlJoinKind.Inner : SqlJoinKind.Left, left.Select.From!, right.Select.From!, condition);
      left.Columns = columns;
      return left;
   }

   /// <summary>A semi join is <c>WHERE EXISTS (...)</c>, an anti join <c>WHERE NOT EXISTS (...)</c>, correlated on the condition.</summary>
   private Frame SemiJoin(JoinNode join, Frame left)
   {
      SqlSelect select = Nested(left.Columns, () =>
      {
         HashSet<PlanColumn> rightOutput = [.. join.Right.Output];
         HashSet<PlanColumn> rightNeeded = join.Condition == null ? [] : [.. PlanAnalysis.Columns(join.Condition).Where(rightOutput.Contains)];
         Frame right = Build(join.Right, rightNeeded);
         if (right.HasPaging || right.DistinctColumns != null || right.Aggregated) { right = Wrap(right, join.Right.Output, rightNeeded); }
         right.Select.OrderBy.Clear();
         Dictionary<PlanColumn, SqlExpr> columns = new(left.Columns);
         foreach ((PlanColumn column, SqlExpr sql) in right.Columns) { columns[column] = sql; }
         if (join.Condition != null) { right.Select.Where = AndAlso(right.Select.Where, Condition(join.Condition, columns)); }
         right.Select.Items.Clear();
         right.Select.Items.Add(Placeholder(right, null));
         return right.Select;
      });
      left.Select.Where = AndAlso(left.Select.Where, new SqlExists(select, join.Kind == JoinKind.Anti));
      return left;
   }

   /// <summary>
   /// <c>GROUP BY</c> the keys. Keys that are expressions are computed in a derived table first, so the grouped
   /// expressions and the selected ones are the same columns in every database.
   /// </summary>
   private Frame Aggregate(AggregateNode aggregate, HashSet<PlanColumn> needed)
   {
      List<AggregateItem> aggregates = aggregate.Aggregates.Where(a => needed.Contains(a.Column)).ToList();
      HashSet<PlanColumn> below = With([], aggregate.Keys.Select(k => k.Expr).Concat(aggregates.Select(a => a.Argument).OfType<PlanExpr>()));
      Frame frame = Build(aggregate.Input, below);
      if (frame.HasPaging || frame.DistinctColumns != null || frame.Aggregated) { frame = Wrap(frame, aggregate.Input.Output, below); }
      bool computed = aggregate.Keys.Any(k => k.Expr is not PlanColumnRef reference || !(frame.Columns.TryGetValue(reference.Column, out SqlExpr? sql) && sql is SqlColumn));
      // Some databases (SQL Server) take no subquery inside an aggregate; such arguments are computed first too. The
      // SQL tells: a column of the frame may be a subquery (a projected first().total).
      Dictionary<AggregateItem, SqlExpr> translated = aggregates.Where(a => a.Argument != null).ToDictionary(a => a, a => Value(a.Argument!, frame.Columns));
      Dictionary<AggregateItem, PlanColumn> arguments = translated
         .Where(a => HasSubquery(a.Value))
         .ToDictionary(a => a.Key, a => new PlanColumn(0, a.Key.Column.Name + "_value", a.Key.Argument!.Type, Results.ColumnLineage.Unknown));
      if (computed || arguments.Count > 0)
      {
         // Compute the keys in a derived table, with the aggregated columns passed through.
         List<(PlanColumn, SqlExpr)> extra = aggregate.Keys.Where(k => !k.IsPassThrough).Select(k => (k.Column, Value(k.Expr, frame.Columns))).ToList();
         extra.AddRange(arguments.Select(a => (a.Value, translated[a.Key])));
         // Above, the grouping reads the computed values and what the keys and other arguments pass through.
         HashSet<PlanColumn> kept = With([], aggregate.Keys.Where(k => k.IsPassThrough).Select(k => k.Expr)
            .Concat(aggregates.Where(a => !arguments.ContainsKey(a)).Select(a => a.Argument).OfType<PlanExpr>()));
         frame = Wrap(frame, aggregate.Input.Output, kept, extra);
      }
      Dictionary<PlanColumn, SqlExpr> columns = [];
      foreach (ProjectItem key in aggregate.Keys)
      {
         SqlExpr sql = frame.Columns.TryGetValue(key.Column, out SqlExpr? known) ? known : Value(key.Expr, frame.Columns);
         columns[key.Column] = sql;
         // A constant key doesn't split the rows; databases reject constants in GROUP BY.
         if (ReadsColumns(sql)) { frame.Select.GroupBy.Add(sql); }
      }
      foreach (AggregateItem item in aggregates)
      {
         SqlExpr? argument = item.Argument == null ? null
            : arguments.TryGetValue(item, out PlanColumn? computedArgument) ? frame.Columns[computedArgument]
            : Value(item.Argument, frame.Columns);
         columns[item.Column] = dialect.Aggregate(item.Function, argument, item.Argument?.Type);
      }
      frame.Columns = columns;
      frame.Aggregated = true;
      frame.Select.OrderBy.Clear();
      return frame;
   }

   /// <summary>A set operation of the two sides' SELECTs, as a derived table the operators above select from.</summary>
   private Frame SetOp(SetOpNode set)
   {
      SqlSelect left = Member(set.Left, set.Output);
      SqlSelect right = Member(set.Right, set.Output);
      SqlSetOperator op = set.Operation switch
      {
         SetOperation.Union => SqlSetOperator.Union,
         SetOperation.UnionAll => SqlSetOperator.UnionAll,
         SetOperation.Intersect => SqlSetOperator.Intersect,
         _ => SqlSetOperator.Except,
      };
      string alias = Alias("d");
      Frame frame = new();
      frame.Select.From = new SqlDerivedTable(new SqlCompound(op, left, right), alias);
      for (int i = 0; i < set.Output.Count; i++) { frame.Columns[set.Output[i]] = new SqlColumn(alias, left.Items[i].Alias!); }
      return frame;
   }

   /// <summary>One side of a set operation: a plain SELECT of its columns under the output's names.</summary>
   private SqlSelect Member(PlanNode side, IReadOnlyList<PlanColumn> output)
   {
      Frame frame = Build(side, [.. side.Output]);
      if (frame.HasPaging) { frame = Wrap(frame, side.Output, [.. side.Output]); }
      frame.Select.OrderBy.Clear();
      HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
      frame.Select.Items.Clear();
      for (int i = 0; i < output.Count; i++) { frame.Select.Items.Add(new SqlSelectItem(frame.Columns[side.Output[i]], Unique(names, output[i].Name))); }
      if (frame.Select.Items.Count == 0) { frame.Select.Items.Add(Placeholder(frame, "one")); }
      return frame.Select;
   }

   /// <summary>
   /// Makes the SELECT so far a derived table with the needed columns. Its order is kept: the sort keys are selected
   /// too and the outer query sorts on them, while the inner one keeps ORDER BY only when it limits the rows.
   /// </summary>
   private Frame Wrap(Frame inner, IReadOnlyList<PlanColumn> available, HashSet<PlanColumn> needed,
                      IReadOnlyList<(PlanColumn Column, SqlExpr Sql)>? computed = null)
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
      foreach ((PlanColumn column, SqlExpr sql) in computed ?? [])
      {
         string name = Unique(names, column.Name);
         select.Items.Add(new SqlSelectItem(sql, name));
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
      if (select.Items.Count == 0) { select.Items.Add(Placeholder(inner, "one")); }
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
      for (int count = aliases.GetValueOrDefault(stem) + 1; ; count++)
      {
         string alias = count == 1 ? stem : stem + count.ToString(System.Globalization.CultureInfo.InvariantCulture);
         if (usedAliases.Add(alias))
         {
            aliases[stem] = count;
            return alias;
         }
      }
   }

   private static HashSet<PlanColumn> With(HashSet<PlanColumn> needed, PlanExpr expr) => With(needed, [expr]);

   private static HashSet<PlanColumn> With(HashSet<PlanColumn> needed, IEnumerable<PlanExpr> exprs)
   {
      HashSet<PlanColumn> result = [.. needed];
      foreach (PlanExpr expr in exprs) { AddColumns(expr, result); }
      return result;
   }

   private static void AddColumns(PlanExpr expr, HashSet<PlanColumn> columns) => PlanAnalysis.Columns(expr, columns);

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
            if (columns.TryGetValue(reference.Column, out SqlExpr? column)) { return column; }
            for (int i = outer.Count - 1; i >= 0; i--)
            {
               if (outer[i].TryGetValue(reference.Column, out SqlExpr? correlated)) { return correlated; }
            }
            throw new InvalidOperationException($"The column {reference.Column} is not available here");
         case PlanSubquery subquery:
            return Subquery(subquery, columns);
         case PlanLiteral literal:
            return Constant(literal.Value, literal.Type);
         case PlanParameter parameter:
            return new SqlParameterRef(Slot(parameter.Source, null, parameter.Source is ParameterSource.User or ParameterSource.Runtime ? parameter.Name : null, parameter.Type, null));
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

   private SqlExpr Subquery(PlanSubquery subquery, IReadOnlyDictionary<PlanColumn, SqlExpr> columns)
   {
      switch (subquery.Kind)
      {
         case SubqueryKind.Exists:
         {
            SqlSelect select = Nested(columns, () =>
            {
               Frame frame = Build(subquery.Plan, []);
               if (!frame.HasPaging) { frame.Select.OrderBy.Clear(); }
               frame.Select.Items.Clear();
               frame.Select.Items.Add(Placeholder(frame, null));
               return frame.Select;
            });
            return new SqlExists(select, subquery.Negated);
         }
         case SubqueryKind.Scalar:
            return new SqlScalarSubquery(Nested(columns, () => Finished(subquery.Plan, out _)));
         default:
         {
            SqlExpr operand = Value(subquery.Operand!, columns);
            SqlSelect select = Nested(columns, () => Finished(subquery.Plan, out _));
            SqlExpr test = new SqlInSubquery(operand, select);
            return subquery.Negated ? new SqlUnary(SqlUnaryOp.Not, test) : test;
         }
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

   /// <summary>
   /// Null, booleans and whole numbers are written into the SQL; everything else from the query text is a parameter.
   /// Whole numbers can't carry anything but digits, and written in they keep grouped expressions identical.
   /// </summary>
   private SqlExpr Constant(object? value, ScalarType type) =>
      value is null or bool or long or int or short || options.InlineConstants
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
      if (!slots.TryGetValue(key, out SqlParameterSlot? slot))
      {
         slot = new SqlParameterSlot("p" + parameters.Count, type, source, constant, name, pattern);
         slots[key] = slot;
         parameters.Add(slot);
      }
      return slot;
   }

   #endregion

   private static SqlExpr? AndAlso(SqlExpr? a, SqlExpr? b) => a == null ? b : b == null ? a : new SqlBinary(SqlBinaryOp.And, a, b);

   private static bool HasSubquery(SqlExpr expr) => expr switch
   {
      SqlExists or SqlScalarSubquery or SqlInSubquery => true,
      SqlUnary unary => HasSubquery(unary.Operand),
      SqlBinary binary => HasSubquery(binary.Left) || HasSubquery(binary.Right),
      SqlIsNull isNull => HasSubquery(isNull.Operand),
      SqlIn inList => HasSubquery(inList.Operand) || inList.Items.Any(HasSubquery),
      SqlBetween between => HasSubquery(between.Operand) || HasSubquery(between.Low) || HasSubquery(between.High),
      SqlLike like => HasSubquery(like.Operand) || HasSubquery(like.Pattern),
      SqlCase caseExpr => caseExpr.Whens.Any(w => HasSubquery(w.Condition) || HasSubquery(w.Result)) || (caseExpr.Else != null && HasSubquery(caseExpr.Else)),
      SqlAggregate aggregate => aggregate.Argument != null && HasSubquery(aggregate.Argument),
      SqlCast cast => HasSubquery(cast.Operand),
      SqlFunctionCall call => call.Arguments.Any(HasSubquery),
      SqlTemplate template => template.Arguments.Any(HasSubquery),
      _ => false,
   };

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
      SqlAggregate => true,
      SqlExists or SqlScalarSubquery or SqlInSubquery => true,
      SqlCast cast => ReadsColumns(cast.Operand),
      SqlFunctionCall call => call.Arguments.Any(ReadsColumns),
      SqlTemplate template => template.Arguments.Any(ReadsColumns),
      _ => false,
   };
}
