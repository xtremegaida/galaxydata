using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Common.Ast;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Binding;

/// <summary>
/// Grouping. After groupBy(...) the rows are groups: their members are the key parts (and <c>key</c>), the members
/// of the grouped rows are collections (one value per element), and aggregates such as <c>sum(total)</c> reduce the
/// elements. A collection used as a single value must be a key part, or a member of one.
/// </summary>
internal sealed partial class BinderRun
{
   private const string KeyName = "key";

   private static readonly HashSet<string> AggregateNames = new(StringComparer.OrdinalIgnoreCase)
   {
      "count", "countDistinct", "sum", "avg", "min", "max", "any", "all",
   };

   private BoundQuery BindGroupBy(BoundQuery input, CallSyntax call, Scope scope)
   {
      if (input.Shape.Group != null)
      {
         throw Error(call, DiagnosticCodes.UnsupportedSyntax, "The rows are groups already; select(...) what you need from them, then group that");
      }
      RowVariable row = RowFor(input.Shape, call.Arguments);
      List<BoundProjection> keys = [];
      NameTable<BoundProjection> names = new();
      foreach (SyntaxNode argument in call.Arguments)
      {
         (string name, SyntaxNode valueNode) = argument is NamedArgumentSyntax named ? (named.Name, named.Value) : (DefaultName(Body(argument)), argument);
         BoundExpr key = BindRowArgument(valueNode, row, scope);
         SyntaxNode body = Body(valueNode);
         switch (key.Type)
         {
            case ScalarBoundType scalar when scalar.Scalar.Kind is ScalarKind.Unknown or ScalarKind.Json:
               throw Error(body, DiagnosticCodes.TypeMismatch, $"Rows can't be grouped by {TypeRules.Describe(scalar.Scalar)}; convert it first, e.g. toString(...)");
            case ScalarBoundType:
               break;
            case RecordBoundType { Shape.Entity.Key: not null }:
               break;
            case RecordBoundType:
               throw Error(body, DiagnosticCodes.UnsupportedSyntax, $"'{SourceText(body)}' is a row without a key, so it can't be a group key; group by its columns");
            default:
               throw NotScalar(key, body);
         }
         if (names.Contains(name))
         {
            throw Error(argument, DiagnosticCodes.DuplicateColumn, $"There is already a key part '{name}'; name this one, as in other_{name}: ...");
         }
         BoundProjection projection = new(name, key);
         names.Add(name, projection);
         keys.Add(projection);
      }
      List<ShapeMember> members = keys.Select(Output).ToList();
      bool syntheticKey = keys.Count > 0 && !names.Contains(KeyName);
      if (syntheticKey)
      {
         members.Add(keys.Count == 1
            ? Output(new BoundProjection(KeyName, keys[0].Expr))
            : new RecordMember(KeyName, new RowShape(keys.Select(Output)), nullable: false));
      }
      GroupInfo group = new(row, keys) { SyntheticKey = syntheticKey };
      return new BoundGroupBy(input, row, keys, new RowShape(members, null, group), call);
   }

   /// <summary>A member of the grouped rows, seen from a group: a collection of its values.</summary>
   private BoundNode? GroupElementMember(BoundExpr groupRow, string name, SyntaxNode node)
   {
      RowShape shape = ((RecordBoundType)groupRow.Type).Shape;
      GroupInfo group = shape.Group!;
      NameMatch<ShapeMember> member = group.ElementRow.Shape.Find(name);
      if (member.Status == MatchStatus.Ambiguous) { throw AmbiguousMember(node, name, member); }
      if (!member.IsFound) { return null; }
      if (member.Item is NavigationMember { Navigation.IsCollection: true })
      {
         throw Error(node, DiagnosticCodes.UnsupportedSyntax,
            $"'{name}' is a collection in each grouped row, so from a group it would be a collection of collections; aggregate it before grouping");
      }
      RowVariable groupVariable = ((BoundRowRef)groupRow).Row;
      return new BoundGroupCollection(groupVariable, Member(new BoundRowRef(group.ElementRow, null), member.Item!, node), node);
   }

   /// <summary><c>cust.name</c> where <c>cust</c> is a group collection: the collection of each element's member.</summary>
   private BoundExpr GroupCollectionMember(BoundGroupCollection collection, string name, SyntaxNode node)
   {
      if (collection.Projection.Type is not RecordBoundType record)
      {
         throw Error(node, DiagnosticCodes.NotARecord, $"'{SourceText(collection.Syntax ?? node)}' is a collection of values, which have no members");
      }
      NameMatch<ShapeMember> member = record.Shape.Find(name);
      if (member.Status == MatchStatus.Ambiguous) { throw AmbiguousMember(node, name, member); }
      if (!member.IsFound)
      {
         throw Error(node, DiagnosticCodes.UnknownName,
            $"{record.Shape.Entity?.DisplayName ?? "The rows"} has no '{name}'{Suggestion(name, record.Shape.Members.Select(m => m.Name))}{MemberList(record.Shape)}");
      }
      if (member.Item is NavigationMember { Navigation.IsCollection: true })
      {
         throw Error(node, DiagnosticCodes.UnsupportedSyntax, $"'{name}' is a collection in each grouped row; aggregate it before grouping");
      }
      return new BoundGroupCollection(collection.GroupRow, Member(collection.Projection, member.Item!, node), node);
   }

   /// <summary>
   /// A group collection used as one value: it must be a key part (<c>customer_id</c> after groupBy(customer_id)) or a
   /// member of a record key part (<c>customer.name</c> after groupBy(customer)); other values are left as they are.
   /// </summary>
   private BoundExpr Collapse(BoundExpr expr, SyntaxNode node)
   {
      if (expr is not BoundGroupCollection collection) { return expr; }
      if (KeyPath(collection.GroupRow, collection.Projection, node) is { } key) { return key; }
      string text = SourceText(node);
      string example = collection.Projection.IsScalar && collection.Projection.Scalar.IsNumeric ? $"sum({text}) or max({text})" : $"max({text}) or count()";
      throw Error(node, DiagnosticCodes.NotGroupKey,
         $"'{text}' has one value per grouped row, but it isn't part of the group key; aggregate it, e.g. {example}, or add it to groupBy(...)");
   }

   private BoundExpr? KeyPath(RowVariable groupRow, BoundExpr projection, SyntaxNode node)
   {
      GroupInfo group = groupRow.Shape.Group!;
      foreach (BoundProjection key in group.Keys)
      {
         if (Same(projection, key.Expr)) { return Member(new BoundRowRef(groupRow, null), groupRow.Shape.Find(key.Name).Item!, node); }
      }
      if (projection is BoundMemberAccess access && KeyPath(groupRow, access.Target, node) is { Type: RecordBoundType } record)
      {
         return Member(record, access.Member, node);
      }
      return null;
   }

   /// <summary>
   /// In a group's scope, an expression written exactly like a key part is that part: <c>year(order_date)</c> after
   /// groupBy(year(order_date)). Member paths are matched by <see cref="Collapse"/> instead.
   /// </summary>
   private BoundExpr? KeyByText(SyntaxNode node, Scope scope)
   {
      if (scope is not RowScope { Row.Shape.Group: { } group } row || node is BinarySyntax { Op: "." } or IdentifierSyntax or LiteralSyntax) { return null; }
      string text = SourceText(node);
      foreach (BoundProjection key in group.Keys)
      {
         if (key.Expr.Syntax is { } syntax && SourceText(syntax) == text)
         {
            return Member(new BoundRowRef(row.Row, null), row.Row.Shape.Find(key.Name).Item!, node);
         }
      }
      return null;
   }

   /// <summary>Whether two bound expressions compute the same value from the same rows.</summary>
   private static bool Same(BoundExpr a, BoundExpr b) => (a, b) switch
   {
      (BoundRowRef x, BoundRowRef y) => ReferenceEquals(x.Row, y.Row),
      (BoundMemberAccess x, BoundMemberAccess y) => string.Equals(x.Member.Name, y.Member.Name, StringComparison.Ordinal) && Same(x.Target, y.Target),
      (BoundLiteral x, BoundLiteral y) => Equals(x.Value, y.Value) && x.Scalar.Kind == y.Scalar.Kind,
      (BoundParameter x, BoundParameter y) => x.Name == y.Name,
      (BoundLetValue x, BoundLetValue y) => ReferenceEquals(x.Let, y.Let),
      (BoundUnary x, BoundUnary y) => x.Op == y.Op && Same(x.Operand, y.Operand),
      (BoundBinary x, BoundBinary y) => x.Op == y.Op && Same(x.Left, y.Left) && Same(x.Right, y.Right),
      (BoundIsNull x, BoundIsNull y) => x.Negated == y.Negated && Same(x.Operand, y.Operand),
      (BoundConditional x, BoundConditional y) => Same(x.Condition, y.Condition) && Same(x.WhenTrue, y.WhenTrue) && Same(x.WhenFalse, y.WhenFalse),
      (BoundFunctionCall x, BoundFunctionCall y) => ReferenceEquals(x.Function, y.Function) && x.Arguments.Count == y.Arguments.Count &&
                                                    x.Arguments.Zip(y.Arguments).All(p => Same(p.First, p.Second)),
      (BoundInList x, BoundInList y) => x.Negated == y.Negated && Same(x.Operand, y.Operand) && x.Items.Count == y.Items.Count &&
                                        x.Items.Zip(y.Items).All(p => Same(p.First, p.Second)),
      _ => false,
   };

   /// <summary>
   /// The group whose scope this is: the innermost row in scope must be a group. Inside a nested query's arguments
   /// the rows are that query's, so an aggregate there doesn't reach the group outside.
   /// </summary>
   private static RowVariable? GroupRowOf(Scope scope)
   {
      for (Scope? current = scope; current != null; current = current.Parent)
      {
         switch (current)
         {
            case RowScope row:
               return row.Row.Shape.Group != null ? row.Row : null;
            case LambdaScope { Value: BoundRowRef reference }:
               return reference.Row.Shape.Group != null ? reference.Row : null;
            case LambdaScope or RecordScope or JoinScope:
               return null;
         }
      }
      return null;
   }

   /// <summary>Why an aggregate call has no group here: inside another aggregate, inside a nested query, or not after groupBy.</summary>
   private static string MisplacedAggregate(string name, Scope scope)
   {
      RowVariable? innermost = null;
      bool outerGroup = false;
      for (Scope? current = scope; current != null; current = current.Parent)
      {
         RowVariable? row = current switch
         {
            RowScope r => r.Row,
            LambdaScope { Value: BoundRowRef reference } => reference.Row,
            _ => null,
         };
         if (row == null) { continue; }
         if (innermost == null) { innermost = row; }
         else if (row.Shape.Group is { } group)
         {
            if (ReferenceEquals(group.ElementRow, innermost)) { return "Aggregates can't be nested: the argument of an aggregate is a value of each grouped row"; }
            outerGroup = true;
         }
      }
      return outerGroup
         ? $"{name}(...) here would aggregate the rows of the query it is in, which aren't groups; to use a value of the group, name it first, e.g. extend(n: {name}()), and use n"
         : $"{name}(...) aggregates the rows of a group, so it only works after groupBy(...); to aggregate a query's rows, call it as the query's method, as in orders.{name}(...)";
   }

   /// <summary>Whether a bound expression refers to the row, or holds a group aggregate.</summary>
   private static bool Mentions(BoundExpr expr, RowVariable row) => expr switch
   {
      BoundRowRef reference => ReferenceEquals(reference.Row, row),
      BoundGroupAggregate => true,
      BoundGroupCollection => true,
      BoundMemberAccess access => Mentions(access.Target, row),
      BoundUnary unary => Mentions(unary.Operand, row),
      BoundBinary binary => Mentions(binary.Left, row) || Mentions(binary.Right, row),
      BoundIsNull isNull => Mentions(isNull.Operand, row),
      BoundInList inList => Mentions(inList.Operand, row) || inList.Items.Any(i => Mentions(i, row)),
      BoundConditional conditional => Mentions(conditional.Condition, row) || Mentions(conditional.WhenTrue, row) || Mentions(conditional.WhenFalse, row),
      BoundFunctionCall call => call.Arguments.Any(a => Mentions(a, row)),
      BoundInQuery inQuery => Mentions(inQuery.Operand, row),
      _ => false,
   };

   #region Aggregates

   /// <summary><c>count()</c>, <c>sum(total)</c> ... in a group's scope: the argument is bound against the grouped rows.</summary>
   private BoundExpr BindGroupAggregate(string name, CallSyntax call, RowVariable groupRow, Scope scope)
   {
      GroupInfo group = groupRow.Shape.Group!;
      AggregateKind kind = AggregateKindOf(name);
      SyntaxNode? argument = call.Arguments.Length switch
      {
         0 => null,
         1 => call.Arguments[0],
         _ => throw Error(call, DiagnosticCodes.WrongArgumentCount, $"{name}(...) takes {(kind == AggregateKind.Count ? "nothing, or a condition" : "one value")}"),
      };
      BoundExpr? bound = argument == null ? null : BindRowArgument(argument, group.ElementRow, scope);
      return GroupAggregate(kind, name, groupRow, bound, argument == null ? null : Body(argument), call);
   }

   /// <summary>An aggregate over a group's collection: <c>orders.sum(total)</c>, <c>orders.count()</c>, <c>total.max()</c>.</summary>
   private BoundExpr BindGroupCollectionMethod(BoundGroupCollection collection, IdentifierSyntax method, CallSyntax call, Scope scope)
   {
      string name = method.Name;
      BoundExpr projection = collection.Projection;
      if (string.Equals(name, "contains", StringComparison.OrdinalIgnoreCase))
      {
         if (call.Arguments.Length != 1 || !projection.IsScalar)
         {
            throw Error(call, DiagnosticCodes.WrongArgumentCount, "contains(value) looks for one value in a collection of values");
         }
         (BoundExpr item, BoundExpr value) = AdoptEachOther(projection, BindScalar(call.Arguments[0], scope));
         RequireComparable(item, value, call.Arguments[0], ordering: false);
         BoundExpr equal = new BoundBinary(BinaryOp.Equal, item, value, ScalarType.Boolean.WithNullable(item.Scalar.Nullable || value.Scalar.Nullable), call);
         return GroupAggregate(AggregateKind.Any, "contains", collection.GroupRow, equal, call.Arguments[0], call);
      }
      if (!AggregateNames.Contains(name))
      {
         throw Error(method, DiagnosticCodes.UnknownMethod,
            $"A group's collection has no method '{name}'{Suggestion(name, AggregateNames.Append("contains"))}; it can be aggregated with count(), sum(...), min(...), max(...), avg(...), any(...) or all(...)");
      }
      AggregateKind kind = AggregateKindOf(name);
      if (call.Arguments.Length > 1) { throw Error(call, DiagnosticCodes.WrongArgumentCount, $"{name}(...) takes at most one argument"); }
      SyntaxNode? argument = call.Arguments.Length == 1 ? call.Arguments[0] : null;
      BoundExpr? bound;
      if (projection.Type is RecordBoundType)
      {
         // The argument sees the record: orders.sum(total) sums each element's orders.total.
         bound = argument switch
         {
            null => null,
            LambdaSyntax { Parameters: [IdentifierSyntax parameter] } lambda => BindExpr(lambda.Body, new LambdaScope(scope, parameter.Name, projection)),
            LambdaSyntax lambda => throw Error(lambda, DiagnosticCodes.LambdaParameters, "This function gets one element, so it takes one parameter"),
            _ => BindExpr(argument, new RecordScope(scope, projection)),
         };
         if (bound == null && kind == AggregateKind.Count && projection.Type.Nullable) { bound = projection; }
      }
      else
      {
         if (argument != null && kind is not (AggregateKind.Any or AggregateKind.All or AggregateKind.Count))
         {
            throw Error(argument, DiagnosticCodes.WrongArgumentCount, $"'{SourceText(collection.Syntax ?? method)}' is a collection of values; {name}() takes no argument here");
         }
         bound = argument == null ? projection : BindExpr(argument, new RecordScope(scope, projection));
      }
      return GroupAggregate(kind, name, collection.GroupRow, bound, argument == null ? null : Body(argument), call);
   }

   private BoundExpr GroupAggregate(AggregateKind kind, string name, RowVariable groupRow, BoundExpr? argument, SyntaxNode? argumentNode, SyntaxNode call)
   {
      RowVariable element = groupRow.Shape.Group!.ElementRow;
      if (argument != null && Mentions(argument, groupRow))
      {
         throw Error(argumentNode ?? call, DiagnosticCodes.UnsupportedSyntax, ContainsAggregate(argument)
            ? "Aggregates can't be nested"
            : $"{name}(...) aggregates a value of each grouped row; '{SourceText(argumentNode ?? call)}' belongs to the group itself (a key part, or a value computed from the group)");
      }
      if (kind is AggregateKind.Any or AggregateKind.All)
      {
         if (argument == null)
         {
            if (kind == AggregateKind.All) { throw Error(call, DiagnosticCodes.WrongArgumentCount, "all(...) needs a condition, as in all(total > 0)"); }
            // A group always has rows, so any() is true.
            return new BoundLiteral(true, ScalarType.Boolean.AsNonNullable(), call);
         }
         argument = RequireCondition(argument, argumentNode!, name);
      }
      if (kind == AggregateKind.Count && argument is { IsScalar: true, Scalar.Kind: ScalarKind.Boolean } condition)
      {
         // count(condition) counts the rows where it holds: count(case when condition then 1 end).
         argument = new BoundConditional(condition, new BoundLiteral(1L, ScalarType.Int64.AsNonNullable(), null),
            new BoundLiteral(null, ScalarType.Int64, null), ScalarType.Int64, argumentNode);
      }
      ScalarType type = AggregateType(kind, name, argument, argumentNode ?? call);
      return new BoundGroupAggregate(kind, element, argument, type, call);
   }

   /// <summary>An aggregate or quantifier over a query's rows: a single value, correlated when the query refers to outer rows.</summary>
   private BoundExpr BindQueryAggregate(BoundQuery source, IdentifierSyntax method, CallSyntax call, Scope scope)
   {
      string name = method.Name;
      if (source.Shape.Group != null)
      {
         throw Error(method, DiagnosticCodes.UnsupportedSyntax, $"The rows are groups; select(...) values from them before {name}(...)");
      }
      RowVariable row = RowFor(source.Shape, call.Arguments);
      if (call.Arguments.Length > 1) { throw Error(call, DiagnosticCodes.WrongArgumentCount, $"{name}(...) takes at most one argument"); }
      SyntaxNode? argument = call.Arguments.Length == 1 ? call.Arguments[0] : null;
      if (string.Equals(name, "contains", StringComparison.OrdinalIgnoreCase))
      {
         if (argument == null) { throw Error(call, DiagnosticCodes.WrongArgumentCount, "contains(value) needs the value to look for"); }
         return InQuery(BindScalar(argument, scope), argument, source, call, call);
      }
      AggregateKind kind = AggregateKindOf(name);
      BoundExpr? bound = argument == null ? null : BindRowArgument(argument, row, scope);
      switch (kind)
      {
         case AggregateKind.Any:
            return new BoundExists(bound == null ? source : new BoundWhere(source, row, RequireCondition(bound, Body(argument!), name), argument), false, call);
         case AggregateKind.All:
         {
            if (bound == null) { throw Error(call, DiagnosticCodes.WrongArgumentCount, "all(...) needs a condition, as in all(total > 0)"); }
            // all(p) holds when no row fails p; a row where p is null fails it.
            BoundExpr holds = RequireCondition(bound, Body(argument!), name);
            BoundExpr fails = new BoundUnary(UnaryOp.Not, NotNull(holds), ScalarType.Boolean.AsNonNullable(), argument);
            return new BoundExists(new BoundWhere(source, row, fails, argument), true, call);
         }
         case AggregateKind.Count when bound is { IsScalar: true, Scalar.Kind: ScalarKind.Boolean }:
            return new BoundQueryAggregate(AggregateKind.Count, new BoundWhere(source, row, bound, argument), row, null, ScalarType.Int64.AsNonNullable(), call);
         default:
            if (bound == null && kind is not AggregateKind.Count)
            {
               bound = SingleColumn(source, row, method, name);
            }
            return new BoundQueryAggregate(kind, source, row, bound, AggregateType(kind, name, bound, argument == null ? call : Body(argument)), call);
      }
   }

   /// <summary><c>value in query</c>: the query must have one column, whose type the value is compared with.</summary>
   private BoundExpr InQuery(BoundExpr operand, SyntaxNode operandNode, BoundQuery source, SyntaxNode sourceNode, SyntaxNode syntax)
   {
      List<ColumnMember> columns = source.Shape.Columns.ToList();
      if (columns.Count != 1 || source.Shape.Members.OfType<RecordMember>().Any())
      {
         throw Error(sourceNode, DiagnosticCodes.NotAScalar,
            $"Looking for a value in a query needs a query of one column; select it first, e.g. .select({(columns.Count > 0 ? FormatName(columns[0].Name) : "id")})");
      }
      RowVariable row = new(ItKeyword, source.Shape);
      BoundExpr column = Member(new BoundRowRef(row, null), columns[0], null);
      BoundExpr value = Adopt(operand, column.Scalar);
      RequireComparable(value, column, operandNode, ordering: false);
      return new BoundInQuery(value, source, syntax);
   }

   private BoundExpr SingleColumn(BoundQuery source, RowVariable row, SyntaxNode node, string name)
   {
      List<ColumnMember> columns = source.Shape.Columns.ToList();
      if (columns.Count != 1)
      {
         throw Error(node, DiagnosticCodes.WrongArgumentCount,
            $"{name}(...) needs the value to aggregate, as in {name}({(columns.FirstOrDefault(c => c.Type.IsNumeric && c.Column?.IsKey != true)?.Name ?? "total")})");
      }
      return Member(new BoundRowRef(row, null), columns[0], null);
   }

   private BoundExpr RequireCondition(BoundExpr expr, SyntaxNode node, string name)
   {
      if (!expr.IsScalar) { throw NotScalar(expr, node); }
      expr = Adopt(expr, ScalarType.Boolean);
      return RequireBoolean(expr, node, name + "(...)");
   }

   /// <summary><c>coalesce(condition, false)</c>: a condition that is null counts as not holding.</summary>
   private BoundExpr NotNull(BoundExpr condition)
   {
      if (!condition.Scalar.Nullable) { return condition; }
      context.Functions.TryGet("coalesce", out Functions.FunctionDef coalesce);
      return new BoundFunctionCall(coalesce, [condition, new BoundLiteral(false, ScalarType.Boolean.AsNonNullable(), null)], ScalarType.Boolean.AsNonNullable(), condition.Syntax);
   }

   private static bool ContainsAggregate(BoundExpr expr) => expr switch
   {
      BoundGroupAggregate or BoundGroupCollection => true,
      BoundUnary unary => ContainsAggregate(unary.Operand),
      BoundBinary binary => ContainsAggregate(binary.Left) || ContainsAggregate(binary.Right),
      BoundConditional conditional => ContainsAggregate(conditional.Condition) || ContainsAggregate(conditional.WhenTrue) || ContainsAggregate(conditional.WhenFalse),
      BoundFunctionCall call => call.Arguments.Any(ContainsAggregate),
      _ => false,
   };

   private static AggregateKind AggregateKindOf(string name) => name.ToLowerInvariant() switch
   {
      "count" => AggregateKind.Count,
      "countdistinct" => AggregateKind.CountDistinct,
      "sum" => AggregateKind.Sum,
      "avg" => AggregateKind.Avg,
      "min" => AggregateKind.Min,
      "max" => AggregateKind.Max,
      "any" => AggregateKind.Any,
      "all" => AggregateKind.All,
      _ => throw new InvalidOperationException($"'{name}' is not an aggregate"),
   };

   /// <summary>
   /// The type an aggregate gives: counts are int64; sums of whole numbers int64 and of decimals decimals with the same
   /// scale, never null (0 for no rows); averages of whole numbers doubles; min and max the argument's type, nullable.
   /// </summary>
   private ScalarType AggregateType(AggregateKind kind, string name, BoundExpr? argument, SyntaxNode node)
   {
      switch (kind)
      {
         case AggregateKind.Count:
            return ScalarType.Int64.AsNonNullable();
         case AggregateKind.Any or AggregateKind.All:
            return ScalarType.Boolean.AsNonNullable();
      }
      if (argument == null) { throw Error(node, DiagnosticCodes.WrongArgumentCount, $"{name}(...) needs the value to aggregate, as in {name}(total)"); }
      if (!argument.IsScalar) { throw NotScalar(argument, node); }
      ScalarType type = argument.Scalar;
      switch (kind)
      {
         case AggregateKind.CountDistinct:
            if (type.Kind is ScalarKind.Unknown or ScalarKind.Json)
            {
               throw Error(node, DiagnosticCodes.TypeMismatch, $"Values of {TypeRules.Describe(type)} can't be told apart reliably; convert them first");
            }
            return ScalarType.Int64.AsNonNullable();
         case AggregateKind.Sum or AggregateKind.Avg:
            if (!type.IsNumeric)
            {
               throw Error(node, DiagnosticCodes.TypeMismatch, $"{name}(...) needs numbers, but '{SourceText(node)}' is {TypeRules.Describe(type)}");
            }
            if (kind == AggregateKind.Sum)
            {
               return (type.Kind switch
               {
                  ScalarKind.Int16 or ScalarKind.Int32 or ScalarKind.Int64 => ScalarType.Int64,
                  ScalarKind.Decimal => type.Precision > 0 ? ScalarType.Decimal(38, type.Scale) : ScalarType.Decimal(),
                  _ => ScalarType.Double,
               }).AsNonNullable();
            }
            return type.Kind == ScalarKind.Decimal ? ScalarType.Decimal() : ScalarType.Double;
         default:
            if (!TypeRules.IsOrderable(type) || type.Kind == ScalarKind.Boolean)
            {
               throw Error(node, DiagnosticCodes.NotSortable, type.Kind == ScalarKind.Boolean
                  ? $"{name}(...) of true/false values: use any(...) or all(...)"
                  : $"{Capitalize(TypeRules.KindName(type))} values have no order, so they have no {name}");
            }
            return type.AsNullable();
      }
   }

   #endregion
}
