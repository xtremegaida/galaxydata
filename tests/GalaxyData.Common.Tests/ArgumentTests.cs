using GalaxyData.Common.Ast;
using Shouldly;
using Xunit;

namespace GalaxyData.Common.Tests;

public sealed class ArgumentTests
{
   private static readonly ExpressionParser Parser = new();

   private static string Print(string source) => SyntaxPrinter.Print(Parser.Parse(source));

   [Theory]
   [InlineData("f(a: 1, b)", "(call f (a: 1) b)")]
   [InlineData("f(\"Total Spend\": 1)", "(call f ('Total Spend': 1))")]
   [InlineData("f('Total Spend': sum(x))", "(call f ('Total Spend': (call sum x)))")]
   [InlineData("f('a' == b ? 1 : 2)", "(call f (?: (== 'a' b) 1 2))")]
   [InlineData("f(x ? 'a' : 'b')", "(call f (?: x 'a' 'b'))")]
   [InlineData("a['b c']", "(index a 'b c')")]
   public void ArgumentsParse(string source, string expected)
   {
      Print(source).ShouldBe(expected);
   }

   [Fact]
   public void StringNamedArgumentKeepsTheUnescapedName()
   {
      CallSyntax call = Parser.Parse("f('it\\'s': 1)").ShouldBeOfType<CallSyntax>();
      call.Arguments[0].ShouldBeOfType<NamedArgumentSyntax>().Name.ShouldBe("it's");
   }

   [Fact]
   public void StringNamesAreNotAllowedInsideIndexers()
   {
      Should.Throw<SyntaxErrorException>(() => Parser.Parse("a['b': 1]"));
   }
}
