using GalaxyData.Common.Ast;
using Shouldly;
using Xunit;

namespace GalaxyData.Common.Tests;

public sealed class LiteralTests
{
   private static readonly ExpressionParser Doubles = new();

   private static readonly ExpressionParser Decimals =
      new(OperatorTable.CreateDefault(), new ExpressionParserOptions { FractionalLiteralsAsDecimal = true });

   private static DynamicNode Literal(ExpressionParser parser, string source) =>
      parser.Parse(source).ShouldBeOfType<LiteralSyntax>().Value;

   [Theory]
   [InlineData("0xff", 255)]
   [InlineData("0XFF", 255)]
   [InlineData("0x1A", 26)]
   [InlineData("0x0", 0)]
   public void HexLiteralsAreIntegers(string source, long expected)
   {
      DynamicNode value = Literal(Doubles, source);
      value.Type.ShouldBe(DynamicNodeType.Integer);
      value.GetInt64().ShouldBe(expected);
   }

   [Theory]
   [InlineData("0x")]
   [InlineData("0xg")]
   [InlineData("0x1g")]
   [InlineData("0x123456789")]
   public void MalformedHexLiteralsAreSyntaxErrors(string source)
   {
      Should.Throw<SyntaxErrorException>(() => Doubles.Parse(source));
   }

   [Fact]
   public void UnicodeEscapesDecode()
   {
      Literal(Doubles, "'\\u0041\\u00e9'").GetString().ShouldBe("Aé");
   }

   [Fact]
   public void FractionalLiteralsAreDoublesByDefault()
   {
      Literal(Doubles, "3.5").Type.ShouldBe(DynamicNodeType.Number);
   }

   [Theory]
   [InlineData("3.5", "3.5")]
   [InlineData("0.1", "0.1")]
   [InlineData("99999999999999999999", "99999999999999999999")]
   public void DecimalOptionReadsExactNumbers(string source, string expected)
   {
      DynamicNode value = Literal(Decimals, source);
      value.Type.ShouldBe(DynamicNodeType.Decimal);
      value.GetDecimal().ShouldBe(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
   }

   [Theory]
   [InlineData("3.5e0")]
   [InlineData("1e3")]
   [InlineData("2E-2")]
   public void DecimalOptionKeepsExponentLiteralsAsDoubles(string source)
   {
      Literal(Decimals, source).Type.ShouldBe(DynamicNodeType.Number);
   }

   [Fact]
   public void DecimalOptionKeepsIntegersAsIntegers()
   {
      Literal(Decimals, "42").Type.ShouldBe(DynamicNodeType.Integer);
   }
}
