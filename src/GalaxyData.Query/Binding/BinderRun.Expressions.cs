using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GalaxyData.Common;
using GalaxyData.Common.Ast;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Functions;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Binding;

internal sealed partial class BinderRun
{
   /// <summary>Binds any syntax to a query, an expression or (inside a path) a namespace.</summary>
   private BoundNode BindNode(SyntaxNode node, Scope scope)
   {
      switch (node)
      {
         case LiteralSyntax literal:
            return Literal(literal);
         case IdentifierSyntax identifier:
            return ResolveName(identifier, scope);
         case BinarySyntax { Op: "." } member:
            if (member.Right is not IdentifierSyntax name)
            {
               throw Error(member.Right, DiagnosticCodes.UnsupportedSyntax, "Expected a name after '.'");
            }
            return BindMember(BindNode(member.Left, scope), name.Name, member);
         case BinarySyntax binary:
            return BindBinary(binary, scope);
         case UnarySyntax { Kind: SyntaxKind.Postfix, Op: ".*" }:
            throw Error(node, DiagnosticCodes.SpreadNotAllowed, "'.*' spreads a row's columns and only works inside select(...) or extend(...)");
         case UnarySyntax unary:
            return BindUnary(unary, scope);
         case TernarySyntax ternary:
            return BindConditional(ternary, scope);
         case CallSyntax { Kind: SyntaxKind.Call } call:
            return BindCall(call, scope);
         case CallSyntax index:
            return BindIndex(index, scope);
         case NamedArgumentSyntax named:
            throw Error(named, DiagnosticCodes.NamedArgumentNotAllowed, $"'{named.Name}: ...' names a column and only works inside select(...) or extend(...)");
         case LambdaSyntax lambda:
            throw Error(lambda, DiagnosticCodes.UnsupportedSyntax, "A function ('x => ...') is only allowed as the argument of a method such as where(...)");
         case ArraySyntax array:
            throw Error(array, DiagnosticCodes.UnsupportedSyntax, "A list [...] is only allowed after 'in', as in status in ['open', 'shipped']");
         case ObjectSyntax obj:
            throw Error(obj, DiagnosticCodes.UnsupportedSyntax, "Object literals {...} are not supported; name columns in select(name: value)");
         case LetSyntax let:
            throw Error(let, DiagnosticCodes.UnsupportedSyntax, "'let' names a subtree and must start a statement");
         case BlockSyntax block:
            throw Error(block, DiagnosticCodes.UnsupportedSyntax, "Blocks {...} are not supported in queries");
         case ForSyntax or IfSyntax or BreakSyntax or ContinueSyntax:
            throw Error(node, DiagnosticCodes.UnsupportedSyntax,
               $"'{node.Kind.ToString().ToLowerInvariant()}' statements are not part of the query language; for a condition use iif(c, a, b) or c ? a : b");
         default:
            throw Error(node, DiagnosticCodes.UnsupportedSyntax, $"Unexpected {node.Kind}");
      }
   }

   private BoundExpr BindExpr(SyntaxNode node, Scope scope) => BindNode(node, scope) switch
   {
      BoundExpr expr => expr,
      BoundQuery => throw Error(node, DiagnosticCodes.NotSupportedYet,
         "A query can't be used as a value here; turning a query into a value (count(), any(), ...) is not supported yet"),
      BoundNamespace ns => throw Error(node, DiagnosticCodes.NotAValue, $"'{Describe(ns.Namespace)}' is a namespace, not a value"),
      _ => throw new InvalidOperationException("Unexpected bound node"),
   };

   private BoundExpr BindScalar(SyntaxNode node, Scope scope)
   {
      BoundExpr expr = BindExpr(node, scope);
      return expr.IsScalar ? expr : throw NotScalar(expr, node);
   }

   private BindException NotScalar(BoundExpr expr, SyntaxNode node) => expr.Type switch
   {
      RecordBoundType { Shape.Entity: { } entity } record => Error(node, DiagnosticCodes.NotAScalar,
         $"'{SourceText(node)}' is a row of {entity.DisplayName}, not a single value; pick one of its columns, e.g. {SourceText(node)}{MemberText(Pick(record.Shape))}"),
      RecordBoundType => Error(node, DiagnosticCodes.NotAScalar, $"'{SourceText(node)}' is a row, not a single value; pick one of its members"),
      CollectionBoundType collection => Error(node, DiagnosticCodes.NotSupportedYet,
         $"'{SourceText(node)}' is a collection of {collection.Element.Entity?.DisplayName ?? "rows"}; aggregating collections (count(), sum(...), any(...)) is not supported yet"),
      _ => Error(node, DiagnosticCodes.NotAScalar, "Expected a single value"),
   };

   private static string Pick(RowShape shape) =>
      shape.Entity?.DisplayColumn?.Name ?? shape.Columns.FirstOrDefault()?.Name ?? "name";

   private BoundNode BindIndex(CallSyntax index, Scope scope)
   {
      if (index.Arguments is not [LiteralSyntax literal] || literal.Value.Type != DynamicNodeType.String || literal.Value.GetString() is not { Length: > 0 } name)
      {
         throw Error(index, DiagnosticCodes.UnsupportedSyntax, "Brackets take a quoted name, as in it[\"Line Item\"]; there is no indexing by position");
      }
      return BindMember(BindNode(index.Target, scope), name, index);
   }

   #region Literals and constants

   private static BoundLiteral Literal(LiteralSyntax literal)
   {
      DynamicNode value = literal.Value;
      return value.Type switch
      {
         DynamicNodeType.Integer => new BoundLiteral(value.GetInt64()!.Value, ScalarType.Int64.AsNonNullable(), literal),
         DynamicNodeType.Number => new BoundLiteral(value.GetFlt64()!.Value, ScalarType.Double.AsNonNullable(), literal),
         DynamicNodeType.Decimal => DecimalLiteral(value.GetDecimal()!.Value, literal),
         DynamicNodeType.String => new BoundLiteral(value.GetString(), ScalarType.Text().AsNonNullable(), literal),
         DynamicNodeType.True => new BoundLiteral(true, ScalarType.Boolean.AsNonNullable(), literal),
         DynamicNodeType.False => new BoundLiteral(false, ScalarType.Boolean.AsNonNullable(), literal),
         _ => new BoundLiteral(null, ScalarType.Unknown, literal),
      };
   }

   private static BoundLiteral DecimalLiteral(decimal value, SyntaxNode syntax)
   {
      int scale = value.Scale;
      string digits = Math.Abs(value).ToString(CultureInfo.InvariantCulture).Replace(".", string.Empty, StringComparison.Ordinal).TrimStart('0');
      int precision = Math.Clamp(Math.Max(digits.Length, scale + 1), 1, 38);
      return new BoundLiteral(value, ScalarType.Decimal(precision, Math.Min(scale, precision)).AsNonNullable(), syntax);
   }

   private static bool IsConstant(BoundExpr expr) => expr is BoundLiteral or BoundParameter;

   /// <summary>Converts a constant to the type it meets, e.g. '2026-01-05' compared with a date column.</summary>
   private BoundExpr Adopt(BoundExpr expr, ScalarType target)
   {
      switch (expr)
      {
         case BoundLiteral literal:
            if (TypeRules.TryConvertConstant(literal.Value, literal.Scalar, target, out object? value, out ScalarType type, out string? error))
            {
               return new BoundLiteral(value, type, literal.Syntax);
            }
            if (error != null) { throw Error(literal.Syntax, DiagnosticCodes.InvalidLiteral, error); }
            return literal;
         case BoundParameter parameter:
            bool textual = parameter.Scalar.Kind == ScalarKind.String &&
               target.Kind is ScalarKind.String or ScalarKind.Date or ScalarKind.DateTime or ScalarKind.DateTimeOffset or ScalarKind.Time or ScalarKind.Guid;
            // Whole numbers take the width they meet (a count of days is an int), checked against the value when the query runs.
            bool whole = parameter.Scalar.IsInteger && target.IsInteger;
            if (parameter.Scalar.Kind == ScalarKind.Unknown || textual || whole)
            {
               return new BoundParameter(parameter.Name, target.WithNullable(parameter.Scalar.Nullable), parameter.Syntax);
            }
            return parameter;
         default:
            return expr;
      }
   }

   private (BoundExpr Left, BoundExpr Right) AdoptEachOther(BoundExpr left, BoundExpr right)
   {
      bool leftConstant = IsConstant(left);
      bool rightConstant = IsConstant(right);
      if (leftConstant && !rightConstant) { return (Adopt(left, right.Scalar), right); }
      if (rightConstant && !leftConstant) { return (left, Adopt(right, left.Scalar)); }
      if (left.Scalar.Kind == ScalarKind.Unknown) { return (Adopt(left, right.Scalar), right); }
      if (right.Scalar.Kind == ScalarKind.Unknown) { return (left, Adopt(right, left.Scalar)); }
      return (left, right);
   }

   #endregion

   #region Operators

   private BoundExpr BindBinary(BinarySyntax binary, Scope scope)
   {
      switch (binary.Op)
      {
         case "==":
            return BindEquality(binary, scope, negated: false);
         case "!=" or "<>":
            return BindEquality(binary, scope, negated: true);
         case "<":
            return BindComparison(binary, BinaryOp.Less, scope);
         case "<=":
            return BindComparison(binary, BinaryOp.LessOrEqual, scope);
         case ">":
            return BindComparison(binary, BinaryOp.Greater, scope);
         case ">=":
            return BindComparison(binary, BinaryOp.GreaterOrEqual, scope);
         case "&&" or "and":
            return BindLogical(binary, BinaryOp.And, scope);
         case "||" or "or":
            return BindLogical(binary, BinaryOp.Or, scope);
         case "+":
            return BindAdd(binary, scope);
         case "-":
            return BindArithmetic(binary, BinaryOp.Subtract, scope);
         case "*":
            return BindArithmetic(binary, BinaryOp.Multiply, scope);
         case "/":
            return BindArithmetic(binary, BinaryOp.Divide, scope);
         case "%":
            return BindArithmetic(binary, BinaryOp.Modulo, scope);
         case "**":
            return BindFunction("power", binary, [binary.Left, binary.Right], null, scope);
         case "??":
            return BindFunction("coalesce", binary, [binary.Left, binary.Right], null, scope);
         case "in":
            return BindIn(binary, scope);
         case "=":
            throw Error(binary, DiagnosticCodes.AssignmentInExpression, "'=' is not a comparison; compare with '==', or name a subtree with 'x := ...;'");
         case ":=":
            throw Error(binary, DiagnosticCodes.AssignmentInExpression, "'x := ...' names a subtree and must be a statement of its own, ending with ';'");
         case "&" or "|" or "^" or "<<" or ">>":
            throw Error(binary, DiagnosticCodes.UnsupportedSyntax, $"The bitwise operator '{binary.Op}' is not supported; for conditions use 'and' and 'or'");
         default:
            throw Error(binary, DiagnosticCodes.UnsupportedSyntax, $"The operator '{binary.Op}' is not supported");
      }
   }

   private static bool IsNullLiteral(SyntaxNode node) => node is LiteralSyntax { Value.Type: DynamicNodeType.Null };

   private BoundExpr BindEquality(BinarySyntax binary, Scope scope, bool negated)
   {
      if (IsNullLiteral(binary.Left) || IsNullLiteral(binary.Right))
      {
         SyntaxNode operand = IsNullLiteral(binary.Left) ? binary.Right : binary.Left;
         BoundExpr tested = BindExpr(operand, scope);
         if (tested.Type is CollectionBoundType) { throw NotScalar(tested, operand); }
         return new BoundIsNull(tested, negated, binary);
      }
      (BoundExpr left, BoundExpr right) = AdoptEachOther(BindScalar(binary.Left, scope), BindScalar(binary.Right, scope));
      RequireComparable(left, right, binary, ordering: false);
      return new BoundBinary(negated ? BinaryOp.NotEqual : BinaryOp.Equal, left, right,
         ScalarType.Boolean.WithNullable(left.Scalar.Nullable || right.Scalar.Nullable), binary);
   }

   private BoundExpr BindComparison(BinarySyntax binary, BinaryOp op, Scope scope)
   {
      (BoundExpr left, BoundExpr right) = AdoptEachOther(BindScalar(binary.Left, scope), BindScalar(binary.Right, scope));
      RequireComparable(left, right, binary, ordering: true);
      return new BoundBinary(op, left, right, ScalarType.Boolean.WithNullable(left.Scalar.Nullable || right.Scalar.Nullable), binary);
   }

   private void RequireComparable(BoundExpr left, BoundExpr right, SyntaxNode node, bool ordering)
   {
      if (!TypeRules.AreComparable(left.Scalar, right.Scalar, ordering, out string? reason))
      {
         throw Error(node, DiagnosticCodes.TypeMismatch, Capitalize(reason!));
      }
   }

   private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

   private BoundExpr BindLogical(BinarySyntax binary, BinaryOp op, Scope scope)
   {
      BoundExpr left = RequireBoolean(Adopt(BindScalar(binary.Left, scope), ScalarType.Boolean), binary.Left, op == BinaryOp.And ? "and" : "or");
      BoundExpr right = RequireBoolean(Adopt(BindScalar(binary.Right, scope), ScalarType.Boolean), binary.Right, op == BinaryOp.And ? "and" : "or");
      return new BoundBinary(op, left, right, ScalarType.Boolean.WithNullable(left.Scalar.Nullable || right.Scalar.Nullable), binary);
   }

   private BoundExpr RequireBoolean(BoundExpr expr, SyntaxNode node, string context)
   {
      if (expr.Scalar.Kind == ScalarKind.Boolean) { return expr; }
      throw Error(node, DiagnosticCodes.NotBoolean,
         $"{context} needs a condition that is true or false, but '{SourceText(node)}' is {TypeRules.Describe(expr.Scalar)}");
   }

   private BoundExpr BindAdd(BinarySyntax binary, Scope scope)
   {
      (BoundExpr left, BoundExpr right) = AdoptEachOther(BindScalar(binary.Left, scope), BindScalar(binary.Right, scope));
      ScalarKind l = left.Scalar.Kind;
      ScalarKind r = right.Scalar.Kind;
      if (l == ScalarKind.String || r == ScalarKind.String)
      {
         if (l != r)
         {
            BoundExpr other = l == ScalarKind.String ? right : left;
            throw Error(binary, DiagnosticCodes.TypeMismatch,
               $"'+' joins text only with text, but '{SourceText(other.Syntax ?? binary)}' is {TypeRules.Describe(other.Scalar)}; convert it with toString(...)");
         }
         return new BoundBinary(BinaryOp.Concat, left, right, ScalarType.Text().WithNullable(left.Scalar.Nullable || right.Scalar.Nullable), binary);
      }
      return Arithmetic(BinaryOp.Add, left, right, binary);
   }

   private BoundExpr BindArithmetic(BinarySyntax binary, BinaryOp op, Scope scope)
   {
      (BoundExpr left, BoundExpr right) = AdoptEachOther(BindScalar(binary.Left, scope), BindScalar(binary.Right, scope));
      return Arithmetic(op, left, right, binary);
   }

   private BoundExpr Arithmetic(BinaryOp op, BoundExpr left, BoundExpr right, BinarySyntax binary)
   {
      ScalarType? result = TypeRules.PromoteNumeric(left.Scalar, right.Scalar);
      if (result == null)
      {
         string hint = TypeRules.IsDateLike(left.Scalar.Kind) || TypeRules.IsDateLike(right.Scalar.Kind)
            ? "; for dates use addDays(...), addMonths(...) or daysBetween(...)"
            : string.Empty;
         throw Error(binary, DiagnosticCodes.TypeMismatch,
            $"'{binary.Op}' needs numbers on both sides, but this is {TypeRules.Describe(left.Scalar)} {binary.Op} {TypeRules.Describe(right.Scalar)}{hint}");
      }
      ScalarType type = result.Value;
      if (op == BinaryOp.Divide && left.Scalar.IsInteger && right.Scalar.IsInteger)
      {
         type = ScalarType.Double.WithNullable(type.Nullable);
      }
      else if (type.Kind == ScalarKind.Decimal && op is BinaryOp.Multiply or BinaryOp.Divide)
      {
         type = ScalarType.Decimal().WithNullable(type.Nullable);
      }
      return new BoundBinary(op, left, right, type, binary);
   }

   private BoundExpr BindUnary(UnarySyntax unary, Scope scope)
   {
      switch (unary.Op)
      {
         case "!" or "not":
         {
            BoundExpr operand = RequireBoolean(Adopt(BindScalar(unary.Operand, scope), ScalarType.Boolean), unary.Operand, "not");
            return new BoundUnary(UnaryOp.Not, operand, operand.Scalar, unary);
         }
         case "-" or "+":
         {
            BoundExpr operand = BindScalar(unary.Operand, scope);
            if (!operand.Scalar.IsNumeric)
            {
               throw Error(unary, DiagnosticCodes.TypeMismatch, $"'{unary.Op}' needs a number, but this is {TypeRules.Describe(operand.Scalar)}");
            }
            if (unary.Op == "+") { return operand; }
            if (operand is BoundLiteral literal) { return Negate(literal, unary); }
            return new BoundUnary(UnaryOp.Negate, operand, operand.Scalar, unary);
         }
         default:
            throw Error(unary, DiagnosticCodes.UnsupportedSyntax, $"The operator '{unary.Op}' is not supported");
      }
   }

   private static BoundLiteral Negate(BoundLiteral literal, SyntaxNode syntax) => literal.Value switch
   {
      long value => new BoundLiteral(-value, literal.Scalar, syntax),
      decimal value => new BoundLiteral(-value, literal.Scalar, syntax),
      double value => new BoundLiteral(-value, literal.Scalar, syntax),
      _ => literal,
   };

   private BoundExpr BindConditional(TernarySyntax ternary, Scope scope)
   {
      BoundExpr condition = RequireBoolean(Adopt(BindScalar(ternary.Condition, scope), ScalarType.Boolean), ternary.Condition, "'? :'");
      (BoundExpr whenTrue, BoundExpr whenFalse) = AdoptEachOther(BindScalar(ternary.Then, scope), BindScalar(ternary.Else, scope));
      ScalarType? type = TypeRules.Unify(whenTrue.Scalar, whenFalse.Scalar);
      if (type == null)
      {
         throw Error(ternary, DiagnosticCodes.TypeMismatch,
            $"The two branches must give the same kind of value, but one is {TypeRules.Describe(whenTrue.Scalar)} and the other {TypeRules.Describe(whenFalse.Scalar)}");
      }
      return new BoundConditional(condition, Adopt(whenTrue, type.Value), Adopt(whenFalse, type.Value), type.Value, ternary);
   }

   private BoundExpr BindIn(BinarySyntax binary, Scope scope)
   {
      if (binary.Right is not ArraySyntax list)
      {
         throw Error(binary.Right, DiagnosticCodes.NotSupportedYet, "'in' takes a list such as ['a', 'b']; 'in' over a query is not supported yet");
      }
      BoundExpr operand = BindScalar(binary.Left, scope);
      if (list.Elements.Length == 0) { return new BoundLiteral(false, ScalarType.Boolean.AsNonNullable(), binary); }
      List<BoundExpr> items = new(list.Elements.Length);
      foreach (SyntaxNode element in list.Elements)
      {
         BoundExpr item = Adopt(BindScalar(element, scope), operand.Scalar);
         if (IsNullLiteral(element))
         {
            throw Error(element, DiagnosticCodes.TypeMismatch, "null never matches in a list; test for it separately with '== null'");
         }
         RequireComparable(operand, item, element, ordering: false);
         items.Add(item);
      }
      return new BoundInList(operand, items, negated: false, binary);
   }

   #endregion

   #region Functions

   /// <summary>Binds a function call; <paramref name="receiver"/> is the value before the dot in <c>x.f(...)</c>.</summary>
   private BoundExpr BindFunction(string name, SyntaxNode call, IReadOnlyList<SyntaxNode> arguments, BoundExpr? receiver, Scope scope)
   {
      if (!context.Functions.TryGet(name, out FunctionDef function))
      {
         string hint = IsQueryMethod(name)
            ? $". {name}(...) is a method of a query; write it after one, as in shop.orders.{name}(...)"
            : Suggestion(name, context.Functions.Functions.Select(f => f.Name));
         throw Error(call, DiagnosticCodes.UnknownFunction, $"There is no function '{name}'{hint}");
      }
      int count = arguments.Count + (receiver == null ? 0 : 1);
      if (count < function.MinArguments || (function.MaxArguments >= 0 && count > function.MaxArguments))
      {
         throw Error(call, DiagnosticCodes.WrongArgumentCount, $"{function.Name} takes {Arity(function)}: {function.Signature}");
      }
      List<BoundExpr> bound = new(count);
      List<SyntaxNode> nodes = new(count);
      if (receiver != null)
      {
         bound.Add(receiver);
         nodes.Add(receiver.Syntax ?? call);
      }
      foreach (SyntaxNode argument in arguments)
      {
         if (argument is NamedArgumentSyntax named)
         {
            throw Error(named, DiagnosticCodes.NamedArgumentNotAllowed, $"{function.Name} doesn't take named arguments: {function.Signature}");
         }
         bound.Add(BindScalar(argument, scope));
         nodes.Add(argument);
      }
      CallArguments checker = new(this, bound, nodes, call);
      ScalarType type = function.Check(checker);
      return new BoundFunctionCall(function, checker.Arguments, type, call);
   }

   private static string Arity(FunctionDef function)
   {
      if (function.MaxArguments < 0) { return $"at least {function.MinArguments} arguments"; }
      if (function.MinArguments == function.MaxArguments)
      {
         return function.MinArguments switch { 0 => "no arguments", 1 => "1 argument", _ => $"{function.MinArguments} arguments" };
      }
      return $"{function.MinArguments} to {function.MaxArguments} arguments";
   }

   private sealed class CallArguments(BinderRun binder, List<BoundExpr> arguments, List<SyntaxNode> nodes, SyntaxNode call) : FunctionArguments
   {
      public IReadOnlyList<BoundExpr> Arguments => arguments;

      public override int Count => arguments.Count;

      public override ScalarType this[int index] => arguments[index].Scalar;

      public override BoundExpr Expr(int index) => arguments[index];

      public override void Adopt(int index, ScalarType target) => arguments[index] = binder.Adopt(arguments[index], target);

      public override Exception Fail(int index, string message) => binder.Error(nodes[index], DiagnosticCodes.TypeMismatch, message);

      public override Exception Fail(string message) => binder.Error(call, DiagnosticCodes.TypeMismatch, message);
   }

   #endregion
}
