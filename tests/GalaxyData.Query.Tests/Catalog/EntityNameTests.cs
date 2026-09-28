using GalaxyData.Query.Catalog;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.Tests.Catalog;

public sealed class EntityNameTests
{
   [Theory]
   [InlineData("shop", new[] { "shop" })]
   [InlineData("shop.orders", new[] { "shop", "orders" })]
   [InlineData(" shop . main . orders ", new[] { "shop", "main", "orders" })]
   [InlineData("xl[\"Budget 2024\"]['Sheet 1']", new[] { "xl", "Budget 2024", "Sheet 1" })]
   [InlineData("shop['in']", new[] { "shop", "in" })]
   public void ParsesPaths(string text, string[] parts)
   {
      EntityName.Parse(text).Parts.ShouldBe(parts);
   }

   [Theory]
   [InlineData("")]
   [InlineData("shop.orders.where(x)")]
   [InlineData("a + b")]
   [InlineData("shop[1]")]
   [InlineData("shop['']")]
   [InlineData("shop.")]
   public void RejectsNonPaths(string text)
   {
      EntityName.TryParse(text, out _).ShouldBeFalse();
   }

   [Fact]
   public void FormatsAndReparses()
   {
      EntityName name = new("xl", "Budget 2024", "Sheet 1");
      name.ToString().ShouldBe("xl['Budget 2024']['Sheet 1']");
      EntityName.Parse(name.ToString()).ShouldBe(name);
   }

   [Fact]
   public void EqualityIsExact()
   {
      new EntityName("shop", "orders").ShouldBe(new EntityName("shop", "orders"));
      new EntityName("shop", "orders").ShouldNotBe(new EntityName("shop", "Orders"));
   }
}
