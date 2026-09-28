using GalaxyData.Query.Catalog;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.Tests.Catalog;

public sealed class NavigationNamingTests
{
   [Theory]
   [InlineData("customer_id", "customer")]
   [InlineData("Customer_ID", "Customer")]
   [InlineData("ship_address_id", "ship_address")]
   [InlineData("parent_key", "parent")]
   [InlineData("owner_fk", "owner")]
   [InlineData("customer__id", "customer")]
   [InlineData("customerId", "customer")]
   [InlineData("CustomerID", "Customer")]
   [InlineData("region2Id", "region2")]
   [InlineData("parentKey", "parent")]
   [InlineData("customer_ref", "customer")]
   [InlineData("customerRef", "customer")]
   [InlineData("product_code", "product")]
   [InlineData("countryCode", "country")]
   [InlineData("id", null)]
   [InlineData("_id", null)]
   [InlineData("ID", null)]
   [InlineData("paid", null)]
   [InlineData("customerid", null)]
   [InlineData("CUSTOMERID", null)]
   [InlineData("customer", null)]
   public void StripsKeySuffixes(string column, string? expected)
   {
      NavigationNaming.StripKeySuffix(column, NavigationNamingOptions.Default).ShouldBe(expected);
   }
}
