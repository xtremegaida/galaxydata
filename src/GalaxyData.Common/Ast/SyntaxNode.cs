using System;
using System.Collections.Generic;

namespace GalaxyData.Common.Ast;

public enum SyntaxKind : byte
{
   Literal,
   Identifier,
   Prefix,
   Postfix,
   Binary,
   Ternary,
   Call,
   Index,
   NamedArgument,
   Block,
   Object,
   Array,
   Lambda,
   Let,
   For,
   If,
   Break,
   Continue,
}

public abstract class SyntaxNode
{
   protected SyntaxNode(int at) : this(at, 1) { }

   protected SyntaxNode(int at, int height)
   {
      ArgumentOutOfRangeException.ThrowIfNegative(at);
      At = at;
      Height = height;
      Start = at;
      End = at;
   }

   public abstract SyntaxKind Kind { get; }

   /// <summary>The anchor of the node: the operator, the opening bracket or the token itself.</summary>
   public int At { get; }

   public int Height { get; }

   /// <summary>Offset of the first character of the whole node, as set by the parser.</summary>
   public int Start { get; private set; }

   /// <summary>Offset one past the last character of the whole node, as set by the parser.</summary>
   public int End { get; private set; }

   public int Length => End - Start;

   internal void SetSpan(int start, int end)
   {
      Start = start;
      End = Math.Max(start, end);
   }

   protected static int Above(SyntaxNode? child) => child == null ? 1 : child.Height + 1;

   protected static int Above(IReadOnlyList<SyntaxNode?>? children)
   {
      var tallest = 0;
      if (children != null)
      {
         foreach (var child in children)
         {
            if (child != null && child.Height > tallest) { tallest = child.Height; }
         }
      }
      return tallest + 1;
   }

   public override string ToString() => $"{Kind} @{At} [{Start}..{End})";
}

public sealed class LiteralSyntax : SyntaxNode
{
   public LiteralSyntax(DynamicNode value, int at) : base(at) { Value = value; }

   public override SyntaxKind Kind => SyntaxKind.Literal;

   public DynamicNode Value { get; }
}

public sealed class IdentifierSyntax : SyntaxNode
{
   public IdentifierSyntax(string name, int at) : base(at)
   {
      ArgumentNullException.ThrowIfNull(name);
      Name = name;
   }

   public override SyntaxKind Kind => SyntaxKind.Identifier;

   public string Name { get; }
}

public sealed class UnarySyntax : SyntaxNode
{
   public UnarySyntax(SyntaxKind kind, string op, SyntaxNode operand, int at) : base(at, Above(operand))
   {
      if (kind is not (SyntaxKind.Prefix or SyntaxKind.Postfix))
      {
         throw new ArgumentOutOfRangeException(nameof(kind), kind, "A unary node is a prefix or a postfix.");
      }
      ArgumentNullException.ThrowIfNull(op);
      ArgumentNullException.ThrowIfNull(operand);
      Kind = kind;
      Op = op;
      Operand = operand;
   }

   public override SyntaxKind Kind { get; }

   public string Op { get; }

   public SyntaxNode Operand { get; }
}

public sealed class BinarySyntax : SyntaxNode
{
   public BinarySyntax(string op, SyntaxNode left, SyntaxNode right, int at)
      : base(at, Math.Max(Above(left), Above(right)))
   {
      ArgumentNullException.ThrowIfNull(op);
      ArgumentNullException.ThrowIfNull(left);
      ArgumentNullException.ThrowIfNull(right);
      Op = op;
      Left = left;
      Right = right;
   }

   public override SyntaxKind Kind => SyntaxKind.Binary;

   public string Op { get; }
   public SyntaxNode Left { get; }
   public SyntaxNode Right { get; }
}

public sealed class TernarySyntax : SyntaxNode
{
   public TernarySyntax(SyntaxNode condition, SyntaxNode then, SyntaxNode @else, int at)
      : base(at, Math.Max(Above(condition), Math.Max(Above(then), Above(@else))))
   {
      ArgumentNullException.ThrowIfNull(condition);
      ArgumentNullException.ThrowIfNull(then);
      ArgumentNullException.ThrowIfNull(@else);
      Condition = condition;
      Then = then;
      Else = @else;
   }

   public override SyntaxKind Kind => SyntaxKind.Ternary;

   public SyntaxNode Condition { get; }
   public SyntaxNode Then { get; }
   public SyntaxNode Else { get; }
}

public sealed class CallSyntax : SyntaxNode
{
   public CallSyntax(SyntaxKind kind, SyntaxNode target, SyntaxNode[] arguments, int at)
      : base(at, Math.Max(Above(target), Above(arguments)))
   {
      if (kind is not (SyntaxKind.Call or SyntaxKind.Index))
      {
         throw new ArgumentOutOfRangeException(nameof(kind), kind, "A call node is a call or an index.");
      }
      ArgumentNullException.ThrowIfNull(target);
      ArgumentNullException.ThrowIfNull(arguments);
      Kind = kind;
      Target = target;
      Arguments = arguments;
   }

   public override SyntaxKind Kind { get; }

   public SyntaxNode Target { get; }

   public SyntaxNode[] Arguments { get; }
}

public sealed class NamedArgumentSyntax : SyntaxNode
{
   public NamedArgumentSyntax(string name, SyntaxNode value, int at) : base(at, Above(value))
   {
      ArgumentNullException.ThrowIfNull(name);
      ArgumentNullException.ThrowIfNull(value);
      Name = name;
      Value = value;
   }

   public override SyntaxKind Kind => SyntaxKind.NamedArgument;

   public string Name { get; }
   public SyntaxNode Value { get; }
}

public sealed class BlockSyntax : SyntaxNode
{
   public BlockSyntax(SyntaxNode[] statements, int at) : base(at, Above(statements))
   {
      ArgumentNullException.ThrowIfNull(statements);
      Statements = statements;
   }

   public override SyntaxKind Kind => SyntaxKind.Block;

   public SyntaxNode[] Statements { get; }
}

public sealed class ObjectSyntax : SyntaxNode
{
   public ObjectSyntax(NamedArgumentSyntax[] members, int at) : base(at, Above(members))
   {
      ArgumentNullException.ThrowIfNull(members);
      Members = members;
   }

   public override SyntaxKind Kind => SyntaxKind.Object;

   public NamedArgumentSyntax[] Members { get; }
}

public sealed class ArraySyntax : SyntaxNode
{
   public ArraySyntax(SyntaxNode[] elements, int at) : base(at, Above(elements))
   {
      ArgumentNullException.ThrowIfNull(elements);
      Elements = elements;
   }

   public override SyntaxKind Kind => SyntaxKind.Array;

   public SyntaxNode[] Elements { get; }
}

public sealed class LambdaSyntax : SyntaxNode
{
   public LambdaSyntax(IdentifierSyntax[] parameters, SyntaxNode body, int at) : base(at, Above(body))
   {
      ArgumentNullException.ThrowIfNull(parameters);
      ArgumentNullException.ThrowIfNull(body);
      Parameters = parameters;
      Body = body;
   }

   public override SyntaxKind Kind => SyntaxKind.Lambda;

   public IdentifierSyntax[] Parameters { get; }

   public SyntaxNode Body { get; }
}

public sealed class LetSyntax : SyntaxNode
{
   public LetSyntax(string name, SyntaxNode value, int at) : base(at, Above(value))
   {
      ArgumentNullException.ThrowIfNull(name);
      ArgumentNullException.ThrowIfNull(value);
      Name = name;
      Value = value;
   }

   public override SyntaxKind Kind => SyntaxKind.Let;

   public string Name { get; }
   public SyntaxNode Value { get; }
}

public sealed class ForSyntax : SyntaxNode
{
   public ForSyntax(SyntaxNode? init, SyntaxNode condition, SyntaxNode? step, SyntaxNode body, int at)
      : base(at, Math.Max(Math.Max(Above(init), Above(condition)), Math.Max(Above(step), Above(body))))
   {
      ArgumentNullException.ThrowIfNull(condition);
      ArgumentNullException.ThrowIfNull(body);
      Init = init;
      Condition = condition;
      Step = step;
      Body = body;
   }

   public override SyntaxKind Kind => SyntaxKind.For;

   public SyntaxNode? Init { get; }

   public SyntaxNode Condition { get; }

   public SyntaxNode? Step { get; }

   public SyntaxNode Body { get; }
}

public sealed class IfSyntax : SyntaxNode
{
   public IfSyntax(SyntaxNode condition, SyntaxNode then, SyntaxNode? @else, int at)
      : base(at, Math.Max(Above(condition), Math.Max(Above(then), Above(@else))))
   {
      ArgumentNullException.ThrowIfNull(condition);
      ArgumentNullException.ThrowIfNull(then);
      Condition = condition;
      Then = then;
      Else = @else;
   }

   public override SyntaxKind Kind => SyntaxKind.If;

   public SyntaxNode Condition { get; }
   public SyntaxNode Then { get; }

   public SyntaxNode? Else { get; }
}

public sealed class BreakSyntax : SyntaxNode
{
   public BreakSyntax(SyntaxNode? value, int at) : base(at, Above(value)) { Value = value; }

   public override SyntaxKind Kind => SyntaxKind.Break;

   public SyntaxNode? Value { get; }
}

public sealed class ContinueSyntax : SyntaxNode
{
   public ContinueSyntax(int at) : base(at) { }

   public override SyntaxKind Kind => SyntaxKind.Continue;
}
