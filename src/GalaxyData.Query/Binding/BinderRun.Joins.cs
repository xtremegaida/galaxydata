using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Common.Ast;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Binding;

/// <summary>Explicit joins, flattening collections, and set operations.</summary>
internal sealed partial class BinderRun
{
   /// <summary>
   /// <c>join(other, condition, name: outer, name: inner)</c>. The condition uses <c>outer.</c> and <c>inner.</c>, or is a
   /// two-parameter lambda; the items say what the joined rows hold, and default to <c>outer</c> and <c>inner</c>.
   /// </summary>
   private BoundQuery BindJoin(BoundQuery left, CallSyntax call, Scope scope, bool leftJoin)
   {
      string method = leftJoin ? "leftJoin" : "join";
      if (left.Shape.Group != null)
      {
         throw Error(call, DiagnosticCodes.UnsupportedSyntax, $"The rows are groups; select(...) what you need from them before {method}(...)");
      }
      if (call.Arguments.Length < 2)
      {
         throw Error(call, DiagnosticCodes.WrongArgumentCount,
            $"{method}(...) takes the rows to join and a condition, as in {method}(shop.customers, outer.customer_id == inner.id)");
      }
      SyntaxNode sourceNode = call.Arguments[0];
      BoundQuery right = BindNode(sourceNode, scope) as BoundQuery
         ?? throw Error(sourceNode, DiagnosticCodes.NotAValue, $"{method}(...) joins rows, so its first argument must be a query such as shop.customers");
      RowVariable outer = new(OuterKeyword, left.Shape);
      RowVariable inner = new(InnerKeyword, right.Shape, nullable: leftJoin);
      JoinScope joinScope = new(scope, outer, inner);

      SyntaxNode conditionNode = call.Arguments[1];
      Scope conditionScope = conditionNode switch
      {
         LambdaSyntax { Parameters: [IdentifierSyntax o, IdentifierSyntax i] } => new LambdaScope(new LambdaScope(scope, o.Name, outer), i.Name, inner),
         LambdaSyntax lambda => throw Error(lambda, DiagnosticCodes.LambdaParameters, $"A join condition gets both rows, so a lambda takes two parameters, as in (o, c) => o.customer_id == c.id"),
         NamedArgumentSyntax named => throw Error(named, DiagnosticCodes.NamedArgumentNotAllowed, $"The second argument of {method}(...) is the condition; named items come after it"),
         _ => joinScope,
      };
      SyntaxNode conditionBody = Body(conditionNode);
      BoundExpr condition = RequireCondition(BindExpr(conditionBody, conditionScope), conditionBody, method);

      List<BoundProjection> items = call.Arguments.Length > 2
         ? BindItems(call.Arguments.Skip(2).ToList(), joinScope, method, null)
         : [new BoundProjection(OuterKeyword, new BoundRowRef(outer, null)), new BoundProjection(InnerKeyword, new BoundRowRef(inner, null))];
      return new BoundJoin(leftJoin ? BoundJoinKind.Left : BoundJoinKind.Inner, left, outer, right, inner, condition, items,
         new RowShape(items.Select(Output)), call);
   }

   /// <summary><c>selectMany(orders)</c>: each row's elements; with items, bound against the row (<c>outer</c>) and the element (<c>inner</c>).</summary>
   private BoundQuery BindSelectMany(BoundQuery input, CallSyntax call, Scope scope)
   {
      if (call.Arguments.Length == 0)
      {
         throw Error(call, DiagnosticCodes.WrongArgumentCount, "selectMany(...) takes the collection to flatten, as in selectMany(orders)");
      }
      RowVariable row = RowFor(input.Shape, call.Arguments.Take(1).ToList());
      SyntaxNode argument = call.Arguments[0];
      BoundNode collection = argument switch
      {
         LambdaSyntax { Parameters: [IdentifierSyntax parameter] } lambda => BindNode(lambda.Body, new LambdaScope(scope, parameter.Name, row)),
         LambdaSyntax lambda => throw Error(lambda, DiagnosticCodes.LambdaParameters, "This function gets one row, so it takes one parameter"),
         _ => BindNode(argument, new RowScope(scope, row)),
      };
      if (collection is not BoundQuery elements)
      {
         throw Error(Body(argument), DiagnosticCodes.NotAValue,
            $"selectMany(...) flattens a collection, such as a collection navigation; '{SourceText(Body(argument))}' is not one");
      }
      RowVariable element = new(InnerKeyword, elements.Shape);
      if (call.Arguments.Length == 1)
      {
         return new BoundSelectMany(input, row, elements, element, null, elements.Shape, call);
      }
      // The items see the row the collection was bound against as outer, and the element as inner.
      List<BoundProjection> items = BindItems(call.Arguments.Skip(1).ToList(), new JoinScope(scope, row, element), "selectMany", null);
      return new BoundSelectMany(input, row, elements, element, items, new RowShape(items.Select(Output)), call);
   }

   /// <summary><c>union</c>, <c>concat</c>, <c>intersect</c>, <c>except</c>: the right side's columns are matched to the left's by name.</summary>
   private BoundQuery BindSetOperation(BoundQuery left, CallSyntax call, Scope scope, SetOperationKind kind, string method)
   {
      if (call.Arguments.Length != 1)
      {
         throw Error(call, DiagnosticCodes.WrongArgumentCount, $"{method}(...) takes the other rows, as in {method}(shop.archived_orders)");
      }
      SyntaxNode argument = call.Arguments[0];
      BoundQuery right = BindNode(argument, scope) as BoundQuery
         ?? throw Error(argument, DiagnosticCodes.NotAValue, $"{method}(...) combines rows, so it takes a query");
      RequirePlainColumns(left, call, method);
      RequirePlainColumns(right, argument, method);

      List<ColumnMember> columns = [];
      foreach (ColumnMember column in left.Shape.Columns)
      {
         NameMatch<ShapeMember> match = right.Shape.Find(column.Name);
         if (match.Item is not ColumnMember other)
         {
            throw Error(argument, DiagnosticCodes.SetOperationColumns,
               $"{method}(...) matches columns by name, and the other rows have no '{column.Name}'; they have {string.Join(", ", right.Shape.Columns.Select(c => c.Name))}");
         }
         ScalarType? type = TypeRules.Unify(column.Type, other.Type);
         if (type == null)
         {
            throw Error(argument, DiagnosticCodes.SetOperationColumns,
               $"'{column.Name}' is {TypeRules.Describe(column.Type)} on one side and {TypeRules.Describe(other.Type)} on the other");
         }
         columns.Add(new ColumnMember(column.Name, type.Value, column.Column));
      }
      List<string> extra = right.Shape.Columns.Where(c => !left.Shape.Contains(c.Name)).Select(c => c.Name).ToList();
      if (extra.Count > 0)
      {
         throw Error(argument, DiagnosticCodes.SetOperationColumns,
            $"{method}(...) needs the same columns on both sides; the other rows also have {string.Join(", ", extra)}");
      }
      bool sameEntity = left.Shape.Entity != null && ReferenceEquals(left.Shape.Entity, right.Shape.Entity);
      RowShape shape = sameEntity ? left.Shape : new RowShape(columns);
      return new BoundSetOperation(kind, left, right, shape, call);
   }

   private void RequirePlainColumns(BoundQuery query, SyntaxNode node, string method)
   {
      if (query.Shape.Group != null)
      {
         throw Error(node, DiagnosticCodes.UnsupportedSyntax, $"The rows are groups; select(...) values from them before {method}(...)");
      }
      if (query.Shape.Members.OfType<RecordMember>().FirstOrDefault() is { } record)
      {
         throw Error(node, DiagnosticCodes.SetOperationColumns,
            $"{method}(...) combines columns, but '{record.Name}' is a row; select its columns instead, e.g. {record.Name}.*");
      }
   }

   /// <summary>The items of select(...), extend(...) and join(...): named values, bare names and paths, and spreads.</summary>
   private List<BoundProjection> BindItems(IReadOnlyList<SyntaxNode> arguments, Scope itemScope, string method, RowShape? extending)
   {
      List<BoundProjection> items = [];
      NameTable<BoundProjection> names = new();

      void Add(string name, BoundExpr value, SyntaxNode node)
      {
         if (names.Contains(name))
         {
            throw Error(node, DiagnosticCodes.DuplicateColumn, $"There is already a column '{name}' in this {method}; give this one another name, as in other_{name}: ...");
         }
         if (extending != null && extending.Contains(name))
         {
            throw Error(node, DiagnosticCodes.DuplicateColumn, $"The rows already have '{name}'; extend(...) only adds new members, so pick another name or use select(...)");
         }
         BoundProjection projection = new(name, value);
         names.Add(name, projection);
         items.Add(projection);
      }

      foreach (SyntaxNode argument in arguments)
      {
         switch (argument)
         {
            case LambdaSyntax lambda:
               throw Error(lambda, DiagnosticCodes.UnsupportedSyntax, $"{method}(...) takes columns rather than a function; write {method}(id, total) or {method}(name: expression)");
            case UnarySyntax { Kind: SyntaxKind.Postfix, Op: ".*" } spread:
            {
               BoundExpr target = Collapse(BindExpr(spread.Operand, itemScope), spread.Operand);
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
               Add(named.Name, Projectable(BindExpr(named.Value, itemScope), named.Value), named);
               continue;
            default:
               Add(DefaultName(argument), Projectable(BindExpr(argument, itemScope), argument), argument);
               continue;
         }
      }
      return items;
   }
}
