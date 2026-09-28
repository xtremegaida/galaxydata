using System;
using System.Globalization;
using System.Text;

namespace GalaxyData.Common.Ast;

/// <summary>
/// Renders a syntax tree as a compact S-expression, e.g. <c>(call (. (. sales orders) where) (== status 'open'))</c>.
/// Unary operators print as <c>(pre:- x)</c> and <c>(post:.* x)</c>. Integers print bare, doubles with a <c>d</c>
/// suffix, decimals with <c>m</c>, strings in single quotes.
/// </summary>
public static class SyntaxPrinter
{
   public static string Print(SyntaxNode node)
   {
      ArgumentNullException.ThrowIfNull(node);
      StringBuilder text = new();
      Write(text, node);
      return text.ToString();
   }

   private static void Write(StringBuilder text, SyntaxNode? node)
   {
      switch (node)
      {
         case null:
            text.Append('_');
            break;
         case LiteralSyntax literal:
            WriteLiteral(text, literal.Value);
            break;
         case IdentifierSyntax identifier:
            text.Append(identifier.Name);
            break;
         case UnarySyntax unary:
            text.Append('(').Append(unary.Kind == SyntaxKind.Prefix ? "pre:" : "post:").Append(unary.Op).Append(' ');
            Write(text, unary.Operand);
            text.Append(')');
            break;
         case BinarySyntax binary:
            text.Append('(').Append(binary.Op).Append(' ');
            Write(text, binary.Left);
            text.Append(' ');
            Write(text, binary.Right);
            text.Append(')');
            break;
         case TernarySyntax ternary:
            List(text, "?:", ternary.Condition, ternary.Then, ternary.Else);
            break;
         case CallSyntax call:
            text.Append('(').Append(call.Kind == SyntaxKind.Call ? "call" : "index").Append(' ');
            Write(text, call.Target);
            foreach (SyntaxNode argument in call.Arguments) { text.Append(' '); Write(text, argument); }
            text.Append(')');
            break;
         case NamedArgumentSyntax named:
            text.Append('(').Append(Name(named.Name)).Append(": ");
            Write(text, named.Value);
            text.Append(')');
            break;
         case BlockSyntax block:
            List(text, "block", block.Statements);
            break;
         case ObjectSyntax obj:
            text.Append('{');
            for (int i = 0; i < obj.Members.Length; i++)
            {
               if (i > 0) { text.Append(' '); }
               Write(text, obj.Members[i]);
            }
            text.Append('}');
            break;
         case ArraySyntax array:
            text.Append('[');
            for (int i = 0; i < array.Elements.Length; i++)
            {
               if (i > 0) { text.Append(' '); }
               Write(text, array.Elements[i]);
            }
            text.Append(']');
            break;
         case LambdaSyntax lambda:
            text.Append("(=> (");
            for (int i = 0; i < lambda.Parameters.Length; i++)
            {
               if (i > 0) { text.Append(' '); }
               text.Append(lambda.Parameters[i].Name);
            }
            text.Append(") ");
            Write(text, lambda.Body);
            text.Append(')');
            break;
         case LetSyntax let:
            text.Append("(let ").Append(let.Name).Append(' ');
            Write(text, let.Value);
            text.Append(')');
            break;
         case ForSyntax loop:
            List(text, "for", loop.Init, loop.Condition, loop.Step, loop.Body);
            break;
         case IfSyntax branch:
            List(text, "if", branch.Condition, branch.Then, branch.Else);
            break;
         case BreakSyntax brk:
            List(text, "break", brk.Value);
            break;
         case ContinueSyntax:
            text.Append("(continue)");
            break;
         default:
            text.Append('<').Append(node.Kind).Append('>');
            break;
      }
   }

   private static void List(StringBuilder text, string head, params SyntaxNode?[] items)
   {
      text.Append('(').Append(head);
      foreach (SyntaxNode? item in items) { text.Append(' '); Write(text, item); }
      text.Append(')');
   }

   private static string Name(string name)
   {
      foreach (char c in name)
      {
         if (!(char.IsLetterOrDigit(c) || c == '_' || c == '$')) { return "'" + name.Replace("'", "\\'", StringComparison.Ordinal) + "'"; }
      }
      return name;
   }

   private static void WriteLiteral(StringBuilder text, DynamicNode value)
   {
      switch (value.Type)
      {
         case DynamicNodeType.String:
            text.Append('\'').Append(value.GetString()!.Replace("'", "\\'", StringComparison.Ordinal)).Append('\'');
            break;
         case DynamicNodeType.Number:
            text.Append(value.GetFlt64()!.Value.ToString("R", CultureInfo.InvariantCulture)).Append('d');
            break;
         case DynamicNodeType.Decimal:
            text.Append(value.GetDecimal()!.Value.ToString(CultureInfo.InvariantCulture)).Append('m');
            break;
         default:
            text.Append(value.ToString());
            break;
      }
   }
}
