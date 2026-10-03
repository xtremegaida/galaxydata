using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using GalaxyData.Common.Ast;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Functions;
using GalaxyData.Query.Language;
using GalaxyData.Query.Results;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Planning;

/// <summary>A query's logical plan; the root's output columns line up with the schema's columns.</summary>
public sealed class LogicalPlan
{
   internal LogicalPlan(PlanNode root, ResultSchema schema, bool requiresRow = false)
   {
      Root = root;
      Schema = schema;
      RequiresRow = requiresRow;
   }

   public PlanNode Root { get; }

   public ResultSchema Schema { get; }

   /// <summary>The query's result is <c>first()</c>, so finding no row is an error.</summary>
   public bool RequiresRow { get; }

   internal LogicalPlan WithRoot(PlanNode root) => new(root, Schema, RequiresRow);

   public override string ToString() => PlanPrinter.Print(Root);
}

/// <summary>
/// Turns a bound query into a logical plan. Entity scans read every column; navigations become joins, made once
/// per row and reused; virtual entities and named subtrees are expanded afresh at each use. Lineage, links and edit
/// targets are worked out as columns are created, so they are known before anything runs; the key values links
/// need travel along as hidden columns.
/// </summary>
internal sealed class Lowerer
{
   private readonly PlanIds ids;
   private readonly LinkTable links;
   private readonly string text;
   private readonly bool spans;
   private readonly IReadOnlyList<NavigationDef> pathPrefix;
   private readonly Dictionary<RowVariable, RowValue> rows = [];
   private readonly Dictionary<RowVariable, GroupValue> groups = [];

   /// <summary>The text of the statements before the result (the named subtrees), for queries that drill down.</summary>
   private readonly string definitions;

   private bool requiresRow;

   private Lowerer(PlanIds ids, LinkTable links, BoundProgram program, bool spans, IReadOnlyList<NavigationDef> pathPrefix)
   {
      this.ids = ids;
      this.links = links;
      this.spans = spans;
      this.pathPrefix = pathPrefix;
      text = program.Text;
      int start = program.Result?.Syntax?.Start ?? 0;
      definitions = start > 0 && start <= text.Length ? text[..start] : string.Empty;
   }

   /// <summary>
   /// Lowers a program that bound successfully; <paramref name="offset"/> and <paramref name="limit"/> page the result.
   /// Pages of an entity's rows are sorted by its key after any sort of the query's, so each page is the same every time.
   /// </summary>
   public static LogicalPlan Lower(BoundProgram program, long? offset = null, long? limit = null)
   {
      ArgumentNullException.ThrowIfNull(program);
      Lowerer lowerer = new(new PlanIds(), new LinkTable(), program, spans: true, []);
      (Cursor cursor, ResultSchema schema) = lowerer.Result(program.Result);
      if (offset > 0 || limit != null)
      {
         if (schema.RowIdentity is { } identity) { cursor.Node = Stabilize(cursor.Node, identity.KeyOrdinals.Select(i => cursor.Node.Output[i]).ToList()); }
         cursor.Node = new LimitNode(cursor.Node, limit == null ? null : Int64(limit.Value), offset > 0 ? Int64(offset.Value) : null);
      }
      // first() of no rows fails, but a later page of its one row is just empty.
      return new LogicalPlan(cursor.Node, schema, lowerer.requiresRow && !(offset > 0));
   }

   /// <summary>A one-row plan counting the rows of a program's result; the result's own order doesn't matter to it.</summary>
   public static LogicalPlan LowerCount(BoundProgram program)
   {
      ArgumentNullException.ThrowIfNull(program);
      Lowerer lowerer = new(new PlanIds(), new LinkTable(), program, spans: true, []);
      (Cursor cursor, _) = lowerer.Result(program.Result);
      ColumnLineage lineage = ColumnLineage.Computed([], "count()", LineageKind.Aggregated);
      PlanColumn count = new(lowerer.ids.Next(), "count", ScalarType.Int64.AsNonNullable(), lineage);
      AggregateNode node = new(Unsorted(cursor.Node), [], [new AggregateItem(count, AggregateFunction.CountRows, null)]);
      return new LogicalPlan(node, new ResultSchema([new ResultColumn(0, count.Name, count.Type, lineage)], null));
   }

   /// <summary>The plan without the sorts that only order its result (those not under a limit).</summary>
   private static PlanNode Unsorted(PlanNode node) => node switch
   {
      SortNode sort => Unsorted(sort.Input),
      FilterNode filter => new FilterNode(Unsorted(filter.Input), filter.Predicate),
      ProjectNode project => new ProjectNode(Unsorted(project.Input), project.Items),
      JoinNode { Kind: JoinKind.Inner or JoinKind.Left or JoinKind.Semi or JoinKind.Anti } join =>
         new JoinNode(join.Kind, Unsorted(join.Left), join.Right, join.Condition, join.Navigation),
      _ => node,
   };

   #region Results

   private (Cursor, ResultSchema) Result(BoundNode? result) => result switch
   {
      BoundQuery query => Result(query),
      BoundFirst first => FirstResult(first, []),
      BoundMemberAccess { Type: RecordBoundType } access when FirstChain(access, out BoundFirst? first, out List<ShapeMember>? members) => FirstResult(first, members),
      BoundExpr { IsScalar: true } value => Result(value),
      _ => throw new ArgumentException("The program has no result to plan; bind it without errors first", nameof(result)),
   };

   private (Cursor, ResultSchema) Result(BoundQuery query)
   {
      (Cursor cursor, RowValue row) = Query(query);
      return Result(cursor, row, query.Shape);
   }

   /// <summary>
   /// <c>first()</c> as the result, or a record of it (<c>orders.first().customer</c>): the query's first row; with
   /// <c>first()</c> finding none is an error when it runs.
   /// </summary>
   private (Cursor, ResultSchema) FirstResult(BoundFirst first, List<ShapeMember> members)
   {
      (Cursor cursor, RowValue row) = FirstRow(first);
      requiresRow = !first.OrDefault;
      if (members.Count == 0) { return Result(cursor, row, first.Source.Shape); }
      RowValue record = (RowValue)Members(row, members);
      if (record.Nullable)
      {
         // A record that isn't there (the first order has no ship address) is no row. No rows then no longer tells
         // whether first() found none, so it isn't an error.
         PlanExpr present = record.Indicator != null
            ? new PlanIsNull(record.Indicator, negated: true)
            : AllNull(record.Columns().Select(c => (PlanExpr)new PlanColumnRef(c)), negated: true);
         cursor.Node = new FilterNode(cursor.Node, present);
         requiresRow = false;
      }
      return Result(cursor, record, record.Shape);
   }

   /// <summary>
   /// The result's projection: the shape's columns, records shown by their display column, then hidden columns for
   /// the keys the visible ones' links and edit targets need.
   /// </summary>
   private (Cursor, ResultSchema) Result(Cursor cursor, RowValue row, RowShape shape)
   {
      List<ProjectItem> items = [];
      List<string> names = [];
      foreach (ShapeMember member in shape.Members)
      {
         if (shape.Group is { SyntheticKey: true } && member.Name == KeyName) { continue; }
         PlanColumnRef value;
         PlanColumn column;
         switch (member)
         {
            case ColumnMember scalar:
               value = row.Column(scalar.Name);
               column = Output(member.Name, value, value.Type, value.Column.Lineage);
               break;
            case RecordMember record:
            {
               RowValue recordValue = row.Record(record.Name);
               if (Display(recordValue) is not { } display) { continue; }
               value = display;
               // A column of its own, so that the link is the record's, not the display column's.
               column = new PlanColumn(ids.Next(), member.Name, value.Type, value.Column.Lineage, value.Column.Origin);
               if (RecordLink(recordValue) is { } link) { links.Set(column, link); }
               break;
            }
            default:
               continue;
         }
         items.Add(new ProjectItem(column, value));
         names.Add(member.Name);
      }
      int visible = items.Count;
      links.AddHidden(items, visible, cursor.Node.Output);
      cursor.Node = Project(cursor.Node, items);
      List<PlanColumn> output = items.Select(i => i.Column).ToList();
      List<ResultColumn> columns = [];
      for (int i = 0; i < output.Count; i++)
      {
         PlanColumn column = output[i];
         bool hidden = i >= visible;
         columns.Add(new ResultColumn(i, hidden ? column.Name : names[i], column.Type, column.Lineage)
         {
            IsHidden = hidden,
            Link = hidden ? null : links.Link(column, output),
            EditTarget = hidden ? null : links.EditTarget(column, output),
         });
      }
      return (cursor, new ResultSchema(columns, shape.Entity, Identity(shape.Entity, names)));
   }

   /// <summary>The key of the rows when they are an entity's rows, and the entity's inverse navigations from them.</summary>
   private static RowIdentity? Identity(EntityDef? entity, List<string> names)
   {
      if (entity?.Key is not { } key || ByName(key.Columns, names) is not { } keyOrdinals) { return null; }
      List<ColumnLink> related = [];
      foreach (NavigationDef navigation in entity.Navigations.Concat(entity.InheritedNavigations))
      {
         if (!navigation.IsInverse || navigation.Hidden || ByName(navigation.OwnerColumns, names) is not { } values) { continue; }
         related.Add(navigation.IsCollection
            ? new CollectionLink(navigation, values)
            : new RowLink(navigation.Target, navigation.TargetColumns, values, navigation));
      }
      return new RowIdentity(entity, keyOrdinals, related);
   }

   private static List<int>? ByName(IReadOnlyList<ColumnDef> columns, List<string> names)
   {
      List<int> ordinals = [];
      foreach (ColumnDef column in columns)
      {
         int ordinal = names.IndexOf(column.Name);
         if (ordinal < 0) { return null; }
         ordinals.Add(ordinal);
      }
      return ordinals;
   }

   /// <summary>The link of a record shown by its display column: the row itself, by its entity's key.</summary>
   private static RowLinkSpec? RecordLink(RowValue record)
   {
      if (record.Shape.Entity is not { Key: { } key } entity) { return null; }
      List<PlanColumn> values = key.Columns.Select(c => record.Column(c.Name).Column).ToList();
      return new RowLinkSpec(entity, key.Columns, record.Path.Count > 0 ? record.Path[^1] : null, values);
   }

   /// <summary>
   /// Rows in a total order, for pages and first rows: the keys are added to the sort that orders the rows (under
   /// filters, projections, limits and joins, which keep the order) when they are known there; otherwise the rows are
   /// sorted again on top, by the same sort and then the keys. Rows in no order are sorted by the keys; rows whose
   /// order can't be restated on top (sorted by a value projected away) are left as they are.
   /// </summary>
   private static PlanNode Stabilize(PlanNode root, IReadOnlyList<PlanColumn> keys)
   {
      if (keys.Count == 0) { return root; }
      if (Tiebreak(root, keys) is { } extended) { return extended; }
      if (!OrderOf(root).Sorted) { return new SortNode(root, keys.Select(k => new PlanSortKey(new PlanColumnRef(k), false)).ToList()); }
      return Resort(root, keys) ?? root;
   }

   /// <summary>
   /// Sorts ordered rows again by their order and then the keys, as high up as both can be written: down through
   /// projections that pass the keys on, to where the order's columns are still there (a projection may drop them).
   /// </summary>
   private static PlanNode? Resort(PlanNode node, IReadOnlyList<PlanColumn> keys)
   {
      (bool sorted, List<PlanSortKey>? order) = OrderOf(node);
      if (!sorted) { return null; }
      if (order != null)
      {
         HashSet<PlanColumn> present = [.. order.Select(k => (k.Expr as PlanColumnRef)?.Column).OfType<PlanColumn>()];
         return new SortNode(node, [.. order, .. keys.Where(k => !present.Contains(k)).Select(k => new PlanSortKey(new PlanColumnRef(k), false))]);
      }
      switch (node)
      {
         case ProjectNode project:
         {
            List<PlanColumn> below = [];
            foreach (PlanColumn key in keys)
            {
               if (project.Items.FirstOrDefault(i => ReferenceEquals(i.Column, key))?.Expr is not PlanColumnRef reference) { return null; }
               below.Add(reference.Column);
            }
            return Resort(project.Input, below) is { } input ? new ProjectNode(input, project.Items) : null;
         }
         case FilterNode filter:
            return Resort(filter.Input, keys) is { } filtered ? new FilterNode(filtered, filter.Predicate) : null;
         case LimitNode limit:
            return Resort(limit.Input, keys) is { } limited ? new LimitNode(limited, limit.Count, limit.Offset) : null;
         case JoinNode { Kind: JoinKind.Inner or JoinKind.Left or JoinKind.Semi or JoinKind.Anti } join when keys.All(join.Left.Output.Contains):
            return Resort(join.Left, keys) is { } left ? new JoinNode(join.Kind, left, join.Right, join.Condition, join.Navigation) : null;
         default:
            return null;
      }
   }

   private static (bool Sorted, List<PlanSortKey>? Keys) OrderOf(PlanNode node) => PlanAnalysis.OrderOf(node);

   /// <summary>Columns that tell a query's rows apart: an entity's key, a group's key parts, else every sortable column.</summary>
   private static List<PlanColumn> Distinguishing(RowValue row, RowShape shape, PlanNode plan)
   {
      IEnumerable<PlanColumn> columns = shape.Entity?.Key is { } key
         ? key.Columns.Select(c => row.Column(c.Name).Column)
         : row.Group is { } group
            ? group.Node.Keys.Where(k => !group.Node.DependentKeys.Contains(k.Column)).Select(k => k.Column)
            : row.Columns().Where(c => TypeRules.IsOrderable(c.Type));
      HashSet<PlanColumn> available = [.. plan.Output];
      return columns.Where(available.Contains).Distinct().ToList();
   }

   private static PlanNode? Tiebreak(PlanNode node, IReadOnlyList<PlanColumn> keys)
   {
      switch (node)
      {
         case SortNode sort:
         {
            if (!keys.All(sort.Input.Output.Contains)) { return null; }
            HashSet<PlanColumn> sorted = [.. sort.Keys.Select(k => (k.Expr as PlanColumnRef)?.Column).OfType<PlanColumn>()];
            List<PlanSortKey> added = keys.Where(k => !sorted.Contains(k)).Select(k => new PlanSortKey(new PlanColumnRef(k), false)).ToList();
            return added.Count == 0 ? sort : new SortNode(sort.Input, [.. sort.Keys, .. added]);
         }
         case FilterNode filter:
            return Tiebreak(filter.Input, keys) is { } filtered ? new FilterNode(filtered, filter.Predicate) : null;
         case LimitNode limit:
            return Tiebreak(limit.Input, keys) is { } limited ? new LimitNode(limited, limit.Count, limit.Offset) : null;
         case ProjectNode project:
            return Tiebreak(project.Input, keys) is { } projected ? new ProjectNode(projected, project.Items) : null;
         case JoinNode { Kind: JoinKind.Inner or JoinKind.Left or JoinKind.Semi or JoinKind.Anti } join when keys.All(join.Left.Output.Contains):
            return Tiebreak(join.Left, keys) is { } left ? new JoinNode(join.Kind, left, join.Right, join.Condition, join.Navigation) : null;
         default:
            return null;
      }
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
            // Groups are distinct already, one per key.
            if (row.Group != null) { return (cursor, row); }
            List<ProjectItem> items = row.Columns().Select(c => new ProjectItem(c, new PlanColumnRef(c))).ToList();
            cursor.Node = new DistinctNode(Project(cursor.Node, items));
            return (cursor, row.Detached(cursor));
         }
         case BoundSelect select:
            return Select(select);
         case BoundExtend extend:
            return Extend(extend);
         case BoundGroupBy group:
            return GroupBy(group);
         case BoundNavigationQuery navigation:
            return NavigationQuery(navigation);
         case BoundJoin join:
            return Join(join);
         case BoundSelectMany many:
            return SelectMany(many);
         case BoundSetOperation set:
            return SetOp(set);
         default:
            throw new NotSupportedException($"Lowering {query.GetType().Name} is not supported yet");
      }
   }

   private (Cursor, RowValue) Select(BoundSelect select)
   {
      (Cursor cursor, RowValue input) = Query(select.Input);
      using (Bind(select.Row, input)) { return (cursor, ProjectInto(cursor, select.Shape, select.Items)); }
   }

   /// <summary>Projects the items (bound against rows in scope) as the new rows of the cursor's plan.</summary>
   private RowValue ProjectInto(Cursor cursor, RowShape shape, IReadOnlyList<BoundProjection> items)
   {
      RowValue output = new(cursor, shape, [], nullable: false);
      List<ProjectItem> projected = [];
      foreach (BoundProjection item in items)
      {
         if (FirstRecord(item.Expr, out BoundFirst? first, out List<ShapeMember>? members))
         {
            output.Set(item.Name, FirstRecord(first, members, ((RecordBoundType)item.Expr.Type).Shape, cursor, projected));
            continue;
         }
         switch (Value(item.Expr))
         {
            case PlanExpr expr:
               PlanColumn column = Output(item.Name, expr, item.Expr.Scalar, Lineage(expr, item.Expr.Syntax));
               projected.Add(new ProjectItem(column, expr));
               output.Set(item.Name, new PlanColumnRef(column));
               break;
            case RowValue record:
               output.Set(item.Name, Forward(record, projected, cursor));
               break;
         }
      }
      links.AddHidden(projected, projected.Count, cursor.Node.Output);
      cursor.Node = new ProjectNode(cursor.Node, projected);
      return output;
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
            if (FirstRecord(item.Expr, out BoundFirst? first, out List<ShapeMember>? members))
            {
               output.Set(item.Name, FirstRecord(first, members, ((RecordBoundType)item.Expr.Type).Shape, cursor, added));
               continue;
            }
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
      ProjectNode project = new(cursor.Node, items);
      // Over groups, aggregates used later are added below; they must pass through this projection too.
      input.Group?.PassThrough(project);
      cursor.Node = project;
      return (cursor, output);
   }

   #endregion

   #region Grouping, joins and set operations

   private const string KeyName = "key";

   /// <summary>
   /// An aggregate over the grouped rows. Key parts are its keys; a record key part groups by the record's key (an
   /// outer navigation's foreign key, so no join is needed), and its other columns become keys when they are used.
   /// </summary>
   private (Cursor, RowValue) GroupBy(BoundGroupBy group)
   {
      (Cursor cursor, RowValue input) = Query(group.Input);
      Cursor elements = new(cursor.Node);
      input.Rehome(elements);
      AggregateNode aggregate = new(elements.Node, [], []);
      GroupValue value = new(aggregate, elements, input, ids);
      cursor.Node = aggregate;
      RowValue row = new(cursor, group.Shape, [], nullable: false) { Group = value };
      List<object> parts = [];
      using (Bind(group.Row, input))
      {
         foreach (BoundProjection key in group.Keys)
         {
            object part = key.Expr.IsScalar ? KeyColumn(value, key) : KeyRecord(value, key, cursor);
            parts.Add(part);
            Set(row, key.Name, part);
         }
      }
      if (group.Shape.Group!.SyntheticKey)
      {
         if (parts.Count == 1) { Set(row, KeyName, parts[0]); }
         else
         {
            RecordMember member = (RecordMember)group.Shape.Find(KeyName).Item!;
            RowValue record = new(cursor, member.Shape, [], nullable: false);
            for (int i = 0; i < parts.Count; i++) { Set(record, group.Keys[i].Name, parts[i]); }
            row.Set(KeyName, record);
         }
      }
      aggregate.Input = elements.Node;
      value.DrillDown = DrillDown(group, parts, elements.Node);
      groups[group.Row] = value;
      return (cursor, row);
   }

   /// <summary>
   /// How to list one group's rows: the query before groupBy, filtered on each key part written as it was. Only for
   /// a query that stands on its own (it reads no rows around it) and whose text is known.
   /// </summary>
   private DrillDownSpec? DrillDown(BoundGroupBy group, List<object> parts, PlanNode elements)
   {
      if (group.Syntax is not CallSyntax call || call.Arguments.Length != parts.Count) { return null; }
      if (group.Input.Syntax is not { } input || !InText(input) || PlanAnalysis.IsCorrelated(elements)) { return null; }
      List<DrillDownKeySpec> keys = [];
      List<PlanColumn> values = [];
      for (int i = 0; i < parts.Count; i++)
      {
         SyntaxNode argument = call.Arguments[i] is NamedArgumentSyntax named ? named.Value : call.Arguments[i];
         (string? parameter, SyntaxNode body) = argument is LambdaSyntax { Parameters: [IdentifierSyntax lambdaParameter] } lambda
            ? (lambdaParameter.Name, lambda.Body)
            : ((string?)null, argument);
         if (!InText(body)) { return null; }
         string expression = IsPath(body) ? text[body.Start..body.End] : "(" + text[body.Start..body.End] + ")";
         switch (parts[i])
         {
            case PlanColumnRef column:
               keys.Add(new DrillDownKeySpec(expression, parameter, column.Type));
               values.Add(column.Column);
               break;
            case RowValue record:
               foreach (ColumnDef keyColumn in record.Shape.Entity!.Key!.Columns)
               {
                  PlanColumnRef column = record.Column(keyColumn.Name);
                  keys.Add(new DrillDownKeySpec(QueryText.AppendMember(new StringBuilder(expression), keyColumn.Name).ToString(), parameter, column.Type));
                  values.Add(column.Column);
               }
               break;
         }
      }
      return new DrillDownSpec(definitions + text[input.Start..input.End], keys, values);
   }

   /// <summary>A name, member path or call, which needs no parentheses in front of <c>==</c>.</summary>
   private static bool IsPath(SyntaxNode node) => node is IdentifierSyntax or BinarySyntax { Op: "." } or CallSyntax;

   private bool InText(SyntaxNode node) => node.Start >= 0 && node.End > node.Start && node.End <= text.Length;

   private static void Set(RowValue row, string name, object value)
   {
      if (value is RowValue record) { row.Set(name, record); }
      else { row.Set(name, (PlanColumnRef)value); }
   }

   private PlanColumnRef KeyColumn(GroupValue group, BoundProjection key)
   {
      PlanExpr expr = Scalar(key.Expr);
      return group.Key(expr, key.Name, key.Expr.Scalar, Lineage(expr, key.Expr.Syntax));
   }

   private RowValue KeyRecord(GroupValue group, BoundProjection key, Cursor cursor)
   {
      RecordBoundType type = (RecordBoundType)key.Expr.Type;
      EntityDef entity = type.Shape.Entity!;
      Func<string, PlanColumnRef> source;
      IReadOnlyList<NavigationDef> path;
      // The columns that make the groups: the foreign key's values through a navigation (a key the target may not
      // have, or a unique key rather than the primary one), else the record's key.
      List<string> identity;
      if (key.Expr is BoundMemberAccess { Member: NavigationMember { Navigation: { IsCollection: false } navigation } } access)
      {
         RowValue owner = (RowValue)Value(access.Target);
         Dictionary<string, PlanColumnRef> foreignKey = new(StringComparer.Ordinal);
         for (int i = 0; i < navigation.TargetColumns.Count; i++) { foreignKey[navigation.TargetColumns[i].Name] = owner.Column(navigation.OwnerColumns[i].Name); }
         source = name => foreignKey.TryGetValue(name, out PlanColumnRef? column) ? column : Navigate(owner, navigation).Column(name);
         path = [.. owner.Path, navigation];
         identity = [.. navigation.TargetColumns.Select(c => c.Name)];
      }
      else
      {
         RowValue record = (RowValue)Value(key.Expr);
         source = record.Column;
         path = record.Path;
         identity = [.. entity.Key!.Columns.Select(c => c.Name)];
      }
      // The identity columns group; the record's other columns follow from them, and are keys only for being read.
      PlanColumnRef KeyOf(string name)
      {
         PlanColumnRef column = source(name);
         ScalarType columnType = column.Type.WithNullable(column.Type.Nullable || type.IsNullable);
         bool dependent = !identity.Contains(name, StringComparer.Ordinal);
         return group.Key(column, name, columnType, column.Column.Lineage, dependent);
      }
      RowValue value = new(cursor, type.Shape, path, type.IsNullable) { Lazy = KeyOf };
      foreach (string name in identity) { value.Column(name); }
      foreach (ColumnDef column in entity.Key!.Columns) { value.Column(column.Name); }
      value.Indicator = value.Column(entity.Key.Columns[0].Name);
      return value;
   }

   private PlanExpr GroupAggregate(BoundGroupAggregate aggregate)
   {
      if (!groups.TryGetValue(aggregate.Element, out GroupValue? group))
      {
         throw new InvalidOperationException("An aggregate is used outside the group it belongs to");
      }
      PlanExpr? argument = null;
      if (aggregate.Argument != null)
      {
         using (Bind(aggregate.Element, group.Element))
         {
            argument = Value(aggregate.Argument) switch
            {
               PlanExpr expr => expr,
               RowValue record => record.Indicator ?? new PlanColumnRef(record.Columns()[0]),
               _ => throw new InvalidOperationException("Unexpected aggregate argument"),
            };
         }
      }
      string name = AggregateName(aggregate.Kind);
      ColumnLineage lineage = Lineage(argument, aggregate.Syntax, LineageKind.Aggregated);
      switch (aggregate.Kind)
      {
         case AggregateKind.Any or AggregateKind.All:
         {
            // any(p) is max(p ? 1 : 0) = 1, all(p) is min(p ? 1 : 0) = 1; a null condition counts as not holding.
            PlanExpr flag = new PlanConditional(argument!, Int64(1), Int64(0), ScalarType.Int64.AsNonNullable());
            PlanColumnRef reduced = group.Aggregate(aggregate.Kind == AggregateKind.Any ? AggregateFunction.Max : AggregateFunction.Min,
               flag, name, ScalarType.Int64.AsNonNullable(), lineage);
            return new PlanBinary(BinaryOp.Equal, reduced, Int64(1), ScalarType.Boolean.AsNonNullable());
         }
         default:
         {
            PlanColumnRef result = group.Aggregate(Function(aggregate.Kind, argument), argument, name, aggregate.Scalar, lineage);
            if (group.DrillDown != null) { links.Set(result.Column, group.DrillDown); }
            return result;
         }
      }
   }

   private static AggregateFunction Function(AggregateKind kind, PlanExpr? argument) => kind switch
   {
      AggregateKind.Count => argument == null ? AggregateFunction.CountRows : AggregateFunction.Count,
      AggregateKind.CountDistinct => AggregateFunction.CountDistinct,
      AggregateKind.Sum => AggregateFunction.Sum,
      AggregateKind.Avg => AggregateFunction.Avg,
      AggregateKind.Min => AggregateFunction.Min,
      AggregateKind.Max => AggregateFunction.Max,
      _ => throw new InvalidOperationException($"{kind} is not an aggregate function"),
   };

   private static string AggregateName(AggregateKind kind) => kind == AggregateKind.CountDistinct ? "countDistinct" : kind.ToString().ToLowerInvariant();

   /// <summary>An aggregate over a query's rows, as a single-row plan: a scalar subquery.</summary>
   private PlanExpr QueryAggregate(BoundQueryAggregate aggregate)
   {
      (Cursor cursor, RowValue row) = Query(aggregate.Source);
      PlanExpr? argument = null;
      if (aggregate.Argument != null)
      {
         using (Bind(aggregate.Row, row))
         {
            argument = Value(aggregate.Argument) switch
            {
               PlanExpr expr => expr,
               RowValue record => record.Indicator ?? new PlanColumnRef(record.Columns()[0]),
               _ => throw new InvalidOperationException("Unexpected aggregate argument"),
            };
         }
      }
      PlanColumn column = new(ids.Next(), AggregateName(aggregate.Kind), aggregate.Scalar,
         Lineage(argument, aggregate.Syntax, LineageKind.Aggregated));
      AggregateNode node = new(cursor.Node, [], [new AggregateItem(column, Function(aggregate.Kind, argument), argument)]);
      PlanSubquery subquery = new(SubqueryKind.Scalar, node, null, negated: false, aggregate.Scalar);
      LinkCollection(subquery, aggregate.Source);
      return subquery;
   }

   /// <summary>An aggregate or test of a whole collection (<c>orders.count()</c>, <c>orders.any()</c>) links to its rows.</summary>
   private void LinkCollection(PlanSubquery subquery, BoundQuery source)
   {
      if (source is not BoundNavigationQuery navigation || FirstRecord(navigation.Owner, out _, out _)) { return; }
      RowValue owner = (RowValue)Value(navigation.Owner);
      links.Set(subquery, new CollectionLinkSpec(navigation.Navigation, navigation.Navigation.OwnerColumns.Select(c => owner.Column(c.Name).Column).ToList()));
   }

   private PlanExpr InQuery(BoundInQuery inQuery)
   {
      PlanExpr operand = Scalar(inQuery.Operand);
      (Cursor cursor, RowValue row) = Query(inQuery.Source);
      PlanColumnRef value = row.Column(inQuery.Source.Shape.Columns.First().Name);
      PlanNode plan = Project(cursor.Node, [new ProjectItem(value.Column, value)]);
      return new PlanSubquery(SubqueryKind.In, plan, operand, negated: false, inQuery.Scalar);
   }

   /// <summary>The rows a collection navigation leads to: the target, filtered on the owner's key (correlated).</summary>
   private (Cursor, RowValue) NavigationQuery(BoundNavigationQuery query)
   {
      NavigationDef navigation = query.Navigation;
      // From a query's first row (orders.first().order_lines), the owner's values are read from that row.
      Func<ColumnDef, PlanExpr> ownerValue;
      IReadOnlyList<NavigationDef> path;
      if (FirstRecord(query.Owner, out BoundFirst? first, out List<ShapeMember>? members))
      {
         RowShape shape = ((RecordBoundType)query.Owner.Type).Shape;
         ownerValue = c => FirstValue(first, [.. members, shape.Find(c.Name).Item!], c.Type);
         path = [];
      }
      else
      {
         RowValue owner = (RowValue)Value(query.Owner);
         ownerValue = c => owner.Column(c.Name);
         path = owner.Path;
      }
      (Cursor cursor, RowValue target) = Scan(navigation.Target, [.. path, navigation], nullable: false);
      PlanExpr? condition = null;
      for (int i = 0; i < navigation.OwnerColumns.Count; i++)
      {
         PlanColumnRef inner = target.Column(navigation.TargetColumns[i].Name);
         PlanExpr outer = ownerValue(navigation.OwnerColumns[i]);
         PlanExpr equal = new PlanBinary(BinaryOp.Equal, inner, outer, ScalarType.Boolean.WithNullable(inner.Type.Nullable || outer.Type.Nullable));
         condition = condition == null ? equal : And(condition, equal);
      }
      cursor.Node = new FilterNode(cursor.Node, condition!);
      return (cursor, target);
   }

   private (Cursor, RowValue) Join(BoundJoin join)
   {
      (Cursor cursor, RowValue left) = Query(join.Left);
      (Cursor rightCursor, RowValue right) = Query(join.Right);
      if (join.Kind == BoundJoinKind.Left) { right = right.AsNullable(rightCursor); }
      using (Bind(join.LeftRow, left))
      using (Bind(join.RightRow, right))
      {
         PlanExpr condition = Scalar(join.Condition);
         cursor.Node = new JoinNode(join.Kind == BoundJoinKind.Left ? JoinKind.Left : JoinKind.Inner, cursor.Node, rightCursor.Node, condition, null);
         right.Rehome(cursor);
         return (cursor, ProjectInto(cursor, join.Shape, join.Items));
      }
   }

   /// <summary>
   /// Each row joined with its collection's elements. The collection is lowered with the row in scope, so its plan
   /// refers to the row's columns; the optimizer turns those references into the join condition.
   /// </summary>
   private (Cursor, RowValue) SelectMany(BoundSelectMany many)
   {
      (Cursor cursor, RowValue row) = Query(many.Input);
      using (Bind(many.Row, row))
      {
         (Cursor elementCursor, RowValue element) = Query(many.Collection);
         cursor.Node = new JoinNode(JoinKind.Inner, cursor.Node, elementCursor.Node, null, null);
         element.Rehome(cursor);
         if (many.Items == null) { return (cursor, element); }
         using (Bind(many.ElementRow, element)) { return (cursor, ProjectInto(cursor, many.Shape, many.Items)); }
      }
   }

   /// <summary>A set operation over both sides' columns, the right side's put in the left side's order by name.</summary>
   private (Cursor, RowValue) SetOp(BoundSetOperation set)
   {
      (Cursor leftCursor, RowValue left) = Query(set.Left);
      (Cursor rightCursor, RowValue right) = Query(set.Right);
      List<ColumnMember> members = set.Shape.Columns.ToList();
      List<PlanColumnRef> leftColumns = members.Select(m => left.Column(m.Name)).ToList();
      List<PlanColumnRef> rightColumns = members.Select(m => right.Column(set.Right.Shape.Find(m.Name).Item!.Name)).ToList();
      PlanNode leftPlan = Project(leftCursor.Node, leftColumns.Select(c => new ProjectItem(c.Column, c)).ToList());
      PlanNode rightPlan = Project(rightCursor.Node, rightColumns.Select(c => new ProjectItem(c.Column, c)).ToList());
      List<PlanColumn> output = [];
      for (int i = 0; i < members.Count; i++)
      {
         ColumnLineage lineage = ColumnLineage.Computed([.. leftColumns[i].Column.Lineage.Sources, .. rightColumns[i].Column.Lineage.Sources], null, LineageKind.Union);
         output.Add(new PlanColumn(ids.Next(), members[i].Name, members[i].Type, lineage));
      }
      SetOperation operation = set.Kind switch
      {
         SetOperationKind.Union => SetOperation.Union,
         SetOperationKind.UnionAll => SetOperation.UnionAll,
         SetOperationKind.Intersect => SetOperation.Intersect,
         _ => SetOperation.Except,
      };
      Cursor cursor = new(new SetOpNode(operation, leftPlan, rightPlan, output));
      RowValue row = new(cursor, set.Shape, [], nullable: false);
      for (int i = 0; i < members.Count; i++) { row.Set(members[i].Name, new PlanColumnRef(output[i])); }
      return (cursor, row);
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

   /// <summary>
   /// The column a projection item produces: the input column itself when name and type are unchanged. A renamed
   /// column still stands for the row it was read from, and keeps its link.
   /// </summary>
   private PlanColumn Output(string name, PlanExpr expr, ScalarType type, ColumnLineage lineage)
   {
      if (expr is PlanColumnRef reference && string.Equals(reference.Column.Name, name, StringComparison.Ordinal) && reference.Column.Type == type)
      {
         return reference.Column;
      }
      PlanColumn column = new(ids.Next(), name, type, lineage, (expr as PlanColumnRef)?.Column.Origin);
      links.Inherit(column, expr);
      return column;
   }

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
            RowOrigin origin = new(table);
            List<ScanColumn> columns = [];
            foreach (ColumnDef c in table.Columns)
            {
               PlanColumn output = new(ids.Next(), c.Name, c.Type.WithNullable(c.Type.Nullable || nullable), ColumnLineage.Direct(new ColumnSource(c, path)), origin);
               origin.Add(c, output);
               columns.Add(new ScanColumn(c, output));
            }
            Cursor cursor = new(new ScanNode(table, columns));
            RowValue row = new(cursor, RowShape.ForEntity(table), path, nullable);
            foreach (ScanColumn column in columns) { row.Set(column.Column.Name, new PlanColumnRef(column.Output)); }
            return (cursor, row);
         }
         case VirtualEntity { Definition: { Query: { } definition } program } virtualEntity:
         {
            Lowerer nested = new(ids, links, program, spans: false, path);
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
         case BoundMemberAccess access when FirstChain(access, out BoundFirst? first, out List<ShapeMember>? members):
            return access.IsScalar ? FirstValue(first, members, access.Scalar) : throw WholeFirstRow();
         case BoundFirst:
            throw WholeFirstRow();
         case BoundMemberAccess access:
            return Member((RowValue)Value(access.Target), access.Member, access.IsScalar ? access.Scalar : null);
         default:
            return Scalar(expr);
      }
   }

   private object Member(RowValue target, ShapeMember member, ScalarType? type = null) => member switch
   {
      ColumnMember column => type is { } scalar ? new PlanColumnRef(target.Column(column.Name).Column, scalar) : target.Column(column.Name),
      NavigationMember navigation => Navigate(target, navigation.Navigation),
      RecordMember record => target.Record(record.Name),
      _ => throw new InvalidOperationException($"Unexpected member {member}"),
   };

   private object Members(RowValue row, IReadOnlyList<ShapeMember> members)
   {
      object value = row;
      foreach (ShapeMember member in members) { value = Member((RowValue)value, member); }
      return value;
   }

   private static NotSupportedException WholeFirstRow() =>
      new("a whole row from first() can be selected or be the result, but not used here; use one of its values, as in first().total");

   #endregion

   #region First rows

   /// <summary>Whether the expression is a query's first row or a member path from one: <c>q.first()</c>, <c>q.first().customer</c>.</summary>
   private static bool FirstChain(BoundExpr expr, [NotNullWhen(true)] out BoundFirst? first, [NotNullWhen(true)] out List<ShapeMember>? members)
   {
      List<ShapeMember> path = [];
      BoundExpr current = expr;
      while (current is BoundMemberAccess access)
      {
         path.Add(access.Member);
         current = access.Target;
      }
      if (current is BoundFirst found)
      {
         path.Reverse();
         first = found;
         members = path;
         return true;
      }
      first = null;
      members = null;
      return false;
   }

   /// <summary>A record read from a query's first row (rather than a scalar of it).</summary>
   private static bool FirstRecord(BoundExpr expr, [NotNullWhen(true)] out BoundFirst? first, [NotNullWhen(true)] out List<ShapeMember>? members)
   {
      if (expr.Type is RecordBoundType && FirstChain(expr, out first, out members)) { return true; }
      first = null;
      members = null;
      return false;
   }

   /// <summary>
   /// A value of a query's first row: a scalar subquery over the query's rows, taking one and reading the member
   /// path from it (navigations join inside the subquery). Null when there is no row, for first() as well.
   /// </summary>
   private PlanExpr FirstValue(BoundFirst first, IReadOnlyList<ShapeMember> members, ScalarType type)
   {
      (Cursor cursor, RowValue row) = FirstRow(first);
      PlanColumnRef value = (PlanColumnRef)Members(row, members);
      PlanNode plan = new ProjectNode(cursor.Node, [new ProjectItem(value.Column, value)]);
      return new PlanSubquery(SubqueryKind.Scalar, plan, null, negated: false, type.AsNullable());
   }

   /// <summary>
   /// The query's first row: its rows in a total order (the query's sort, then what tells rows apart), taking one. Each
   /// value read from a first row lowers this afresh, so the order must pick the same row every time.
   /// </summary>
   private (Cursor, RowValue) FirstRow(BoundFirst first)
   {
      (Cursor cursor, RowValue row) = Query(first.Source);
      cursor.Node = Take(Stabilize(cursor.Node, Distinguishing(row, first.Source.Shape, cursor.Node)), Int64(1));
      return (cursor, row);
   }

   /// <summary>
   /// A record of a query's first row, as columns of the projection being built: one scalar subquery per column (those
   /// nothing uses are pruned later), so the record can be shown, linked and passed on like any other.
   /// </summary>
   private RowValue FirstRecord(BoundFirst first, List<ShapeMember> members, RowShape shape, Cursor cursor, List<ProjectItem> projected)
   {
      RowValue record = new(cursor, shape, [], nullable: true);
      foreach (ShapeMember member in shape.Members)
      {
         switch (member)
         {
            case ColumnMember column:
            {
               PlanExpr value = FirstValue(first, [.. members, member], column.Type);
               PlanColumn output = new(ids.Next(), column.Name, value.Type, Lineage(value, null));
               projected.Add(new ProjectItem(output, value));
               record.Set(column.Name, new PlanColumnRef(output));
               break;
            }
            case RecordMember nested:
               record.Set(nested.Name, FirstRecord(first, [.. members, member], nested.Shape, cursor, projected));
               break;
         }
      }
      if (shape.Entity?.Key is { } key)
      {
         // A row that exists has its key.
         record.Indicator = record.Column(key.Columns[0].Name);
      }
      else
      {
         // Without a key, a value that is 1 when there is a first row and null when there is none.
         (Cursor cursor2, _) = FirstRow(first);
         PlanColumn one = new(ids.Next(), "present", ScalarType.Int64.AsNonNullable(), ColumnLineage.Constant(null));
         PlanSubquery exists = new(SubqueryKind.Scalar, new ProjectNode(cursor2.Node, [new ProjectItem(one, Int64(1))]), null, negated: false, ScalarType.Int64.AsNullable());
         PlanColumn present = new(ids.Next(), "present", exists.Type, ColumnLineage.Constant(null));
         projected.Add(new ProjectItem(present, exists));
         record.Indicator = new PlanColumnRef(present);
      }
      return record;
   }

   #endregion

   #region Scalars

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
         case BoundGroupAggregate aggregate:
            return GroupAggregate(aggregate);
         case BoundQueryAggregate aggregate:
            return QueryAggregate(aggregate);
         case BoundExists exists:
         {
            PlanSubquery subquery = new(SubqueryKind.Exists, Query(exists.Source).Item1.Node, null, exists.Negated, exists.Scalar);
            if (!exists.Negated) { LinkCollection(subquery, exists.Source); }
            return subquery;
         }
         case BoundInQuery inQuery:
            return InQuery(inQuery);
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
      if (operand is BoundFirst first)
      {
         // q.first() == null: q has no rows.
         return new PlanSubquery(SubqueryKind.Exists, Query(first.Source).Item1.Node, null, !negated, ScalarType.Boolean.AsNonNullable());
      }
      if (FirstChain(operand, out BoundFirst? from, out List<ShapeMember>? members))
      {
         RowShape shape = ((RecordBoundType)operand.Type).Shape;
         IEnumerable<ColumnMember> present = shape.Entity?.Key is { } key ? [(ColumnMember)shape.Find(key.Columns[0].Name).Item!] : shape.Columns;
         return AllNull(present.Select(c => FirstValue(from, [.. members, c], c.Type)), negated);
      }
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
      if (expr is PlanSubquery { Kind: SubqueryKind.Scalar } scalar) { return scalar.Plan.Output[0].Lineage; }
      List<ColumnSource> sources = [];
      bool readsColumns = false;
      Collect(expr, sources, ref readsColumns);
      string? expressionText = ExpressionText(syntax);
      return readsColumns ? ColumnLineage.Computed(sources, expressionText) : ColumnLineage.Constant(expressionText);
   }

   /// <summary>The lineage of an aggregate: the columns its argument reads (none for count()).</summary>
   private ColumnLineage Lineage(PlanExpr? argument, SyntaxNode? syntax, LineageKind kind)
   {
      List<ColumnSource> sources = [];
      bool readsColumns = false;
      if (argument != null) { Collect(argument, sources, ref readsColumns); }
      return ColumnLineage.Computed(sources, ExpressionText(syntax), kind);
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
         case PlanSubquery subquery:
            readsColumns = true;
            if (subquery.Operand != null) { Collect(subquery.Operand, sources, ref readsColumns); }
            if (subquery.Kind != SubqueryKind.Exists) { sources.AddRange(subquery.Plan.Output[0].Lineage.Sources); }
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

/// <summary>
/// The ids of a plan's columns. A plan may make at most <see cref="Limit"/>: named subtrees and virtual entities are
/// planned afresh at each use, so a few that each use the one before twice would make a plan too large to hold.
/// </summary>
internal sealed class PlanIds
{
   public const int Limit = 100_000;

   private int next;

   public int Next() => ++next <= Limit ? next : throw new PlanTooLargeException();
}

/// <summary>A query whose plan would be larger than plans may be (<see cref="PlanIds.Limit"/> columns).</summary>
internal sealed class PlanTooLargeException()
   : NotSupportedException($"This query is too large to plan: it would make more than {PlanIds.Limit.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} columns. " +
               "Named subtrees and virtual entities are planned afresh at each use, so ones that each use another more than once grow quickly");
