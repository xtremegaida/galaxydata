using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Common.Ast;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Binding;

internal sealed partial class BinderRun
{
   private static readonly HashSet<string> SupportedMethods = new(StringComparer.OrdinalIgnoreCase)
   {
      "where", "select", "extend", "orderBy", "orderByDescending", "orderByDesc", "thenBy", "thenByDescending", "thenByDesc",
      "take", "skip", "distinct",
   };

   /// <summary>Methods the language will have but this version can't bind yet.</summary>
   private static readonly HashSet<string> LaterMethods = new(StringComparer.OrdinalIgnoreCase)
   {
      "groupBy", "join", "leftJoin", "selectMany", "union", "concat", "intersect", "except", "count", "countDistinct",
      "sum", "avg", "min", "max", "any", "all", "contains", "first", "firstOrDefault",
   };

   private static bool IsQueryMethod(string name) => SupportedMethods.Contains(name) || LaterMethods.Contains(name);

   private BoundNode BindCall(CallSyntax call, Scope scope)
   {
      switch (call.Target)
      {
         case BinarySyntax { Op: ".", Right: IdentifierSyntax method } member:
         {
            BoundNode receiver = BindNode(member.Left, scope);
            return receiver switch
            {
               BoundQuery query => BindQueryMethod(query, method, call, scope),
               BoundExpr { IsScalar: true } value => BindFunction(method.Name, call, call.Arguments, value, scope),
               BoundExpr { Type: CollectionBoundType } collection => throw Error(call, DiagnosticCodes.NotSupportedYet,
                  $"Methods on collections such as '{SourceText(member.Left)}' are not supported yet"),
               BoundExpr => throw Error(method, DiagnosticCodes.UnknownMethod,
                  $"'{SourceText(member.Left)}' is a row, which has no methods; call {method.Name}(...) on one of its columns"),
               BoundNamespace ns => throw Error(method, DiagnosticCodes.UnknownMethod,
                  $"'{Describe(ns.Namespace)}' is a namespace, which has no methods; call {method.Name}(...) on one of its entities, e.g. {Example(ns.Namespace)}.{method.Name}(...)"),
               _ => throw new InvalidOperationException("Unexpected receiver"),
            };
         }
         case IdentifierSyntax function:
            if (function.Name is "desc" or "asc")
            {
               throw Error(call, DiagnosticCodes.UnsupportedSyntax, $"{function.Name}(...) sets the direction of a sort key and only works inside orderBy(...) or thenBy(...)");
            }
            return BindFunction(function.Name, call, call.Arguments, null, scope);
         default:
            throw Error(call, DiagnosticCodes.UnsupportedSyntax, "Only functions and methods can be called");
      }
   }

   private BoundQuery BindQueryMethod(BoundQuery input, IdentifierSyntax method, CallSyntax call, Scope scope)
   {
      string name = method.Name;
      if (!SupportedMethods.Contains(name))
      {
         if (LaterMethods.Contains(name))
         {
            throw Error(method, DiagnosticCodes.NotSupportedYet, $"{name}(...) is not supported yet");
         }
         throw Error(method, DiagnosticCodes.UnknownMethod,
            $"A query has no method '{name}'{Suggestion(name, SupportedMethods.Concat(LaterMethods))}");
      }
      switch (name.ToLowerInvariant())
      {
         case "where":
            return BindWhere(input, call, scope);
         case "select":
            return BindSelect(input, call, scope, extend: false);
         case "extend":
            return BindSelect(input, call, scope, extend: true);
         case "orderby":
            return BindOrderBy(input, call, scope, descending: false, thenBy: false);
         case "orderbydescending" or "orderbydesc":
            return BindOrderBy(input, call, scope, descending: true, thenBy: false);
         case "thenby":
            return BindOrderBy(input, call, scope, descending: false, thenBy: true);
         case "thenbydescending" or "thenbydesc":
            return BindOrderBy(input, call, scope, descending: true, thenBy: true);
         case "take":
            return new BoundTake(input, BindCount(call, new RowScope(scope, new RowVariable(ItKeyword, input.Shape)), "take"), call);
         case "skip":
            return new BoundSkip(input, BindCount(call, new RowScope(scope, new RowVariable(ItKeyword, input.Shape)), "skip"), call);
         case "distinct":
            if (call.Arguments.Length > 0)
            {
               throw Error(call, DiagnosticCodes.WrongArgumentCount, "distinct() takes no arguments; to keep distinct values of some columns, select them first: .select(a, b).distinct()");
            }
            return new BoundDistinct(input, call);
         default:
            throw new InvalidOperationException($"Unhandled method {name}");
      }
   }

   /// <summary>A row variable for a method's arguments, named after the first lambda parameter if there is one.</summary>
   private static RowVariable RowFor(RowShape shape, IReadOnlyList<SyntaxNode> arguments)
   {
      foreach (SyntaxNode argument in arguments)
      {
         if (argument is LambdaSyntax { Parameters: [IdentifierSyntax parameter] }) { return new RowVariable(parameter.Name, shape); }
      }
      return new RowVariable(ItKeyword, shape);
   }

   /// <summary>Binds a method argument against the row: implicitly (<c>total &gt; 3</c>) or through a lambda (<c>o =&gt; o.total &gt; 3</c>).</summary>
   private BoundExpr BindRowArgument(SyntaxNode argument, RowVariable row, Scope outer)
   {
      if (argument is LambdaSyntax lambda)
      {
         if (lambda.Parameters.Length != 1)
         {
            throw Error(lambda, DiagnosticCodes.LambdaParameters, $"This function gets one row, so it takes one parameter, as in o => ..., not {lambda.Parameters.Length}");
         }
         return BindExpr(lambda.Body, new LambdaScope(outer, lambda.Parameters[0].Name, row));
      }
      return BindExpr(argument, new RowScope(outer, row));
   }

   private BoundQuery BindWhere(BoundQuery input, CallSyntax call, Scope scope)
   {
      if (call.Arguments.Length != 1)
      {
         throw Error(call, DiagnosticCodes.WrongArgumentCount, call.Arguments.Length == 0
            ? "where(...) needs a condition, as in where(status == 'open')"
            : "where(...) takes one condition; combine several with 'and'");
      }
      SyntaxNode argument = call.Arguments[0];
      if (argument is NamedArgumentSyntax named)
      {
         throw Error(named, DiagnosticCodes.NamedArgumentNotAllowed, "where(...) takes a condition, not a named value; did you mean '==' instead of ':'?");
      }
      RowVariable row = RowFor(input.Shape, call.Arguments);
      BoundExpr predicate = BindRowArgument(argument, row, scope);
      if (!predicate.IsScalar) { throw NotScalar(predicate, Body(argument)); }
      predicate = Adopt(predicate, ScalarType.Boolean);
      if (predicate.Scalar.Kind != ScalarKind.Boolean)
      {
         throw Error(Body(argument), DiagnosticCodes.NotBoolean,
            $"where(...) needs a condition that is true or false, but '{SourceText(Body(argument))}' is {TypeRules.Describe(predicate.Scalar)}");
      }
      return new BoundWhere(input, row, predicate, call);
   }

   private static SyntaxNode Body(SyntaxNode argument) => argument is LambdaSyntax lambda ? lambda.Body : argument;

   private BoundQuery BindSelect(BoundQuery input, CallSyntax call, Scope scope, bool extend)
   {
      string method = extend ? "extend" : "select";
      if (call.Arguments.Length == 0)
      {
         throw Error(call, DiagnosticCodes.WrongArgumentCount, $"{method}(...) needs at least one column, as in {method}(id, total: price * qty)");
      }
      RowVariable row = new(ItKeyword, input.Shape);
      RowScope rowScope = new(scope, row);
      List<BoundProjection> items = [];
      NameTable<BoundProjection> names = new();

      void Add(string name, BoundExpr value, SyntaxNode node)
      {
         if (names.Contains(name))
         {
            throw Error(node, DiagnosticCodes.DuplicateColumn, $"There is already a column '{name}' in this {method}; give this one another name, as in other_{name}: ...");
         }
         if (extend && input.Shape.Contains(name))
         {
            throw Error(node, DiagnosticCodes.DuplicateColumn, $"The rows already have '{name}'; extend(...) only adds new members, so pick another name or use select(...)");
         }
         BoundProjection projection = new(name, value);
         names.Add(name, projection);
         items.Add(projection);
      }

      foreach (SyntaxNode argument in call.Arguments)
      {
         switch (argument)
         {
            case LambdaSyntax lambda:
               throw Error(lambda, DiagnosticCodes.UnsupportedSyntax, $"{method}(...) takes columns rather than a function; write {method}(id, total) or {method}(name: expression)");
            case UnarySyntax { Kind: SyntaxKind.Postfix, Op: ".*" } spread:
            {
               BoundExpr target = BindExpr(spread.Operand, rowScope);
               if (target.Type is not RecordBoundType record)
               {
                  throw Error(spread, DiagnosticCodes.SpreadNotAllowed, $"'.*' spreads the columns of a row, but '{SourceText(spread.Operand)}' is not a row");
               }
               foreach (ColumnMember column in record.Shape.Columns)
               {
                  if (column.Column?.Hidden == true) { continue; }
                  Add(column.Name, Member(target, column, null), spread);
               }
               continue;
            }
            case NamedArgumentSyntax named:
               if (named.Value is LambdaSyntax)
               {
                  throw Error(named.Value, DiagnosticCodes.UnsupportedSyntax, "A column is an expression, not a function; drop the 'x =>'");
               }
               Add(named.Name, Projectable(BindExpr(named.Value, rowScope), named.Value), named);
               continue;
            default:
               Add(DefaultName(argument), Projectable(BindExpr(argument, rowScope), argument), argument);
               continue;
         }
      }

      List<ShapeMember> members = items.Select(Output).ToList();
      return extend
         ? new BoundExtend(input, row, items, input.Shape.Extend(members), call)
         : new BoundSelect(input, row, items, new RowShape(members), call);
   }

   private BoundExpr Projectable(BoundExpr value, SyntaxNode node) => value.Type switch
   {
      CollectionBoundType => throw NotScalar(value, node),
      _ => value,
   };

   private static ShapeMember Output(BoundProjection projection) => projection.Expr.Type switch
   {
      ScalarBoundType scalar => new ColumnMember(projection.Name, scalar.Scalar,
         projection.Expr is BoundMemberAccess { Member: ColumnMember { Column: { } column } } ? column : null),
      RecordBoundType record => new RecordMember(projection.Name, record.Shape, record.IsNullable),
      _ => throw new InvalidOperationException("Only scalars and records are projected"),
   };

   /// <summary>The name a projected expression gets without <c>name:</c>: the last name in a path, else its text.</summary>
   private string DefaultName(SyntaxNode node) => node switch
   {
      IdentifierSyntax identifier => identifier.Name.StartsWith('$') ? identifier.Name[1..] : identifier.Name,
      BinarySyntax { Op: ".", Right: IdentifierSyntax member } => member.Name,
      CallSyntax { Kind: SyntaxKind.Index, Arguments: [LiteralSyntax literal] } when literal.Value.GetString() is { Length: > 0 } name => name,
      _ => SourceText(node),
   };

   private BoundQuery BindOrderBy(BoundQuery input, CallSyntax call, Scope scope, bool descending, bool thenBy)
   {
      string method = thenBy ? "thenBy" : "orderBy";
      if (call.Arguments.Length == 0)
      {
         throw Error(call, DiagnosticCodes.WrongArgumentCount, $"{method}(...) needs at least one key, as in {method}(total) or {method}(desc(total), id)");
      }
      BoundOrderBy? previous = null;
      if (thenBy)
      {
         previous = input as BoundOrderBy ?? throw Error(call, DiagnosticCodes.ThenByWithoutOrderBy,
            "thenBy(...) adds keys to the sort right before it, so it must follow orderBy(...) or another thenBy(...)");
      }
      RowVariable row = previous?.Row ?? RowFor(input.Shape, call.Arguments);
      List<BoundSortKey> keys = previous == null ? [] : [.. previous.Keys];
      foreach (SyntaxNode argument in call.Arguments)
      {
         SyntaxNode keyNode = argument;
         bool keyDescending = descending;
         if (argument is CallSyntax { Kind: SyntaxKind.Call, Target: IdentifierSyntax { Name: "desc" or "asc" } direction } wrapped)
         {
            if (wrapped.Arguments.Length != 1)
            {
               throw Error(wrapped, DiagnosticCodes.WrongArgumentCount, $"{direction.Name}(...) wraps one sort key, as in {direction.Name}(total)");
            }
            keyNode = wrapped.Arguments[0];
            keyDescending = direction.Name == "desc";
         }
         if (keyNode is NamedArgumentSyntax named)
         {
            throw Error(named, DiagnosticCodes.NamedArgumentNotAllowed, $"{method}(...) takes sort keys, not named values");
         }
         BoundExpr key = BindRowArgument(keyNode, row, scope);
         if (!key.IsScalar)
         {
            throw key.Type is RecordBoundType
               ? Error(Body(keyNode), DiagnosticCodes.NotSortable, $"'{SourceText(Body(keyNode))}' is a row; sort by one of its columns, e.g. {SourceText(Body(keyNode))}{MemberText(Pick(((RecordBoundType)key.Type).Shape))}")
               : NotScalar(key, Body(keyNode));
         }
         if (!TypeRules.IsOrderable(key.Scalar))
         {
            throw Error(Body(keyNode), DiagnosticCodes.NotSortable, $"{Capitalize(TypeRules.KindName(key.Scalar))} values can't be sorted: there is no order every database agrees on");
         }
         keys.Add(new BoundSortKey(key, keyDescending));
      }
      return new BoundOrderBy(previous?.Input ?? input, row, keys, call);
   }

   private BoundExpr BindCount(CallSyntax call, Scope scope, string method)
   {
      if (call.Arguments.Length != 1)
      {
         throw Error(call, DiagnosticCodes.WrongArgumentCount, $"{method}(...) takes one count, as in {method}(10)");
      }
      SyntaxNode argument = call.Arguments[0];
      BoundExpr count = Adopt(BindScalar(argument, scope), ScalarType.Int64);
      switch (count)
      {
         case BoundLiteral { Value: long value } when value >= 0:
            return count;
         case BoundLiteral { Value: long }:
            throw Error(argument, DiagnosticCodes.InvalidCount, $"{method}(...) needs a count of zero or more");
         case BoundParameter parameter when parameter.Scalar.IsInteger:
            return parameter;
         default:
            throw Error(argument, DiagnosticCodes.InvalidCount,
               $"{method}(...) takes a whole number written in the query or given as a parameter, such as {method}(10) or {method}($pageSize); it can't depend on the rows");
      }
   }
}
