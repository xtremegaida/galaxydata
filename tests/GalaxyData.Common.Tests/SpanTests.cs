using GalaxyData.Common.Ast;
using Shouldly;
using Xunit;

namespace GalaxyData.Common.Tests;

public sealed class SpanTests
{
   private static readonly ExpressionParser Parser = new();

   private static string Text(string source, SyntaxNode node) => source[node.Start..node.End];

   [Theory]
   [InlineData("a + b * c", "a + b * c")]
   [InlineData("  sales.orders.where(x > 1)  ", "sales.orders.where(x > 1)")]
   [InlineData("(a + b) * c", "(a + b) * c")]
   [InlineData("-a.b", "-a.b")]
   [InlineData("customer.*", "customer.*")]
   [InlineData("[1, 2]", "[1, 2]")]
   [InlineData("{a: 1}", "{a: 1}")]
   [InlineData("c ? t : e", "c ? t : e")]
   [InlineData("a[\"x y\"]", "a[\"x y\"]")]
   [InlineData("x => x.id == 1", "x => x.id == 1")]
   [InlineData("(a, b) => a + b", "(a, b) => a + b")]
   [InlineData("'str'", "'str'")]
   [InlineData("42 /* trailing */", "42")]
   [InlineData("let x = 1; x", "let x = 1; x")]
   public void RootSpanCoversTheWholeExpression(string source, string expected)
   {
      Text(source, Parser.Parse(source)).ShouldBe(expected);
   }

   [Fact]
   public void BinaryOperandsHaveTheirOwnSpans()
   {
      const string source = "a + b * c";
      BinarySyntax root = Parser.Parse(source).ShouldBeOfType<BinarySyntax>();
      Text(source, root.Left).ShouldBe("a");
      Text(source, root.Right).ShouldBe("b * c");
      root.At.ShouldBe(2);
   }

   [Fact]
   public void MethodChainPartsHaveSpans()
   {
      const string source = "sales.orders.where(x > 1)";
      CallSyntax call = Parser.Parse(source).ShouldBeOfType<CallSyntax>();
      Text(source, call.Target).ShouldBe("sales.orders.where");
      BinarySyntax member = call.Target.ShouldBeOfType<BinarySyntax>();
      Text(source, member.Left).ShouldBe("sales.orders");
      Text(source, member.Right).ShouldBe("where");
      Text(source, call.Arguments[0]).ShouldBe("x > 1");
   }

   [Fact]
   public void NamedArgumentSpanIncludesTheName()
   {
      const string source = "f(who: customer.name)";
      CallSyntax call = Parser.Parse(source).ShouldBeOfType<CallSyntax>();
      NamedArgumentSyntax named = call.Arguments[0].ShouldBeOfType<NamedArgumentSyntax>();
      Text(source, named).ShouldBe("who: customer.name");
      Text(source, named.Value).ShouldBe("customer.name");
   }

   [Fact]
   public void GroupedOperandSpanExcludesTheParentheses()
   {
      const string source = "(a + b) * c";
      BinarySyntax root = Parser.Parse(source).ShouldBeOfType<BinarySyntax>();
      Text(source, root.Left).ShouldBe("a + b");
   }

   [Fact]
   public void LambdaParametersAndBodyHaveSpans()
   {
      const string source = "(a, b) => a + b";
      LambdaSyntax lambda = Parser.Parse(source).ShouldBeOfType<LambdaSyntax>();
      Text(source, lambda.Parameters[1]).ShouldBe("b");
      Text(source, lambda.Body).ShouldBe("a + b");
   }

   [Fact]
   public void IfWithoutElseDoesNotSwallowTheSemicolon()
   {
      const string source = "if (a) b; c";
      BlockSyntax block = Parser.Parse(source).ShouldBeOfType<BlockSyntax>();
      Text(source, block.Statements[0]).ShouldBe("if (a) b");
      Text(source, block.Statements[1]).ShouldBe("c");
   }

   [Fact]
   public void LetSpanStartsAtTheKeyword()
   {
      const string source = "let x = 1; x";
      BlockSyntax block = Parser.Parse(source).ShouldBeOfType<BlockSyntax>();
      Text(source, block.Statements[0]).ShouldBe("let x = 1");
   }

   [Fact]
   public void HandBuiltNodesDefaultToAnEmptySpanAtTheAnchor()
   {
      IdentifierSyntax node = new("x", 5);
      node.Start.ShouldBe(5);
      node.End.ShouldBe(5);
      node.Length.ShouldBe(0);
   }
}
