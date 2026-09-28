using GalaxyData.Query.Types;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.Tests.Types;

public sealed class ScalarTypeTests
{
   [Theory]
   [InlineData("int64")]
   [InlineData("int64?")]
   [InlineData("decimal")]
   [InlineData("decimal(10,2)")]
   [InlineData("decimal(38,0)?")]
   [InlineData("string")]
   [InlineData("string(50)")]
   [InlineData("string(50,ansi)?")]
   [InlineData("string(max,ansi)")]
   [InlineData("binary(16)?")]
   [InlineData("datetimeoffset?")]
   [InlineData("unknown")]
   public void TextFormRoundTrips(string text)
   {
      ScalarType.Parse(text).ToString().ShouldBe(text);
   }

   [Fact]
   public void ParsingIgnoresCaseAndSpaces()
   {
      ScalarType.Parse(" Decimal( 10 , 2 )? ").ShouldBe(ScalarType.Decimal(10, 2).AsNullable());
   }

   [Theory]
   [InlineData("")]
   [InlineData("int")]
   [InlineData("decimal(10)")]
   [InlineData("int64(4)")]
   [InlineData("string(50,utf8)")]
   [InlineData("decimal(10,2")]
   public void InvalidTextIsRejected(string text)
   {
      ScalarType.TryParse(text, out _).ShouldBeFalse();
   }

   [Fact]
   public void FactoryTypesAreNullable()
   {
      ScalarType.Int64.Nullable.ShouldBeTrue();
      ScalarType.Int64.AsNonNullable().ToString().ShouldBe("int64");
   }
}
