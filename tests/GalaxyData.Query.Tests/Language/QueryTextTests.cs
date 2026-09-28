using GalaxyData.Query.Language;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.Tests.Language;

public sealed class QueryTextTests
{
   [Theory]
   [InlineData("orders", true)]
   [InlineData("_private", true)]
   [InlineData("$param", true)]
   [InlineData("Ünïcode", true)]
   [InlineData("a1", true)]
   [InlineData("1a", false)]
   [InlineData("", false)]
   [InlineData("has space", false)]
   [InlineData("dash-ed", false)]
   [InlineData("a::b", false)]
   [InlineData("true", false)]
   [InlineData("null", false)]
   [InlineData("and", false)]
   [InlineData("in", false)]
   [InlineData("not", false)]
   [InlineData("inner", true)]
   [InlineData("True", true)]
   public void BareIdentifiers(string name, bool expected)
   {
      QueryText.IsBareIdentifier(name).ShouldBe(expected);
   }

   [Theory]
   [InlineData("plain", "'plain'")]
   [InlineData("O'Brien", "'O\\'Brien'")]
   [InlineData("back\\slash", "'back\\\\slash'")]
   [InlineData("two\nlines", "'two\\nlines'")]
   public void QuotesStrings(string value, string expected)
   {
      QueryText.QuoteString(value).ShouldBe(expected);
   }

   [Fact]
   public void FormatsPathsWithIndexersForAwkwardNames()
   {
      QueryText.FormatPath(["xl", "Budget 2024", "Sheet 1"]).ShouldBe("xl['Budget 2024']['Sheet 1']");
      QueryText.FormatPath(["shop", "main", "orders"]).ShouldBe("shop.main.orders");
      QueryText.FormatPath(["shop", "in"]).ShouldBe("shop['in']");
   }
}
