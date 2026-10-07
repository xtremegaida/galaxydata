using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Introspection;
using GalaxyData.Testing;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.ContainerTests;

public sealed class IntrospectionTests(Servers servers)
{
   private static async Task<SourceSchema> IntrospectAsync(ServerDatabase database, IntrospectionOptions? options = null)
   {
      await using DbConnection connection = database.Open();
      return await database.Provider.Introspector.IntrospectAsync(connection, options ?? IntrospectionOptions.Default, TestContext.Current.CancellationToken);
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   [InlineData(ServerKind.ClickHouse)]
   public async Task IntrospectsTheShopFixture(ServerKind server)
   {
      ServerDatabase shop = await servers.ShopAsync(server);
      SourceSchema schema = await IntrospectAsync(shop);
      schema.ServerVersion.ShouldNotBeNullOrEmpty();
      // ClickHouse's schemas are its databases: the test's own has a name of its own each time.
      string json = IntrospectionJson.Serialize(schema with { ServerVersion = "(version)" }, indented: true).Replace(shop.Name, "(database)", System.StringComparison.Ordinal);
      Golden.Match(json, "json", server.ToString());
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   [InlineData(ServerKind.ClickHouse)]
   public async Task IntrospectsAColumnOfEachType(ServerKind server)
   {
      SourceSchema schema = await IntrospectAsync(await servers.KindsAsync(server));
      TableSchema kinds = schema.Tables.Single(t => t.Name == "kinds");
      Golden.Match(string.Join("\n", kinds.Columns.Select(c => $"{c.Name}: {c.NativeType} -> {c.Type}{(c.IsRowVersion ? " (row version)" : "")}")), suffix: server.ToString());
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task SchemasCanBeFiltered(ServerKind server)
   {
      ServerDatabase shop = await servers.ShopAsync(server);
      (await IntrospectAsync(shop, new IntrospectionOptions { IncludeSchemas = ["CRM"] })).Tables.Select(t => t.Name).ShouldBe(["contacts"]);
      (await IntrospectAsync(shop, new IntrospectionOptions { ExcludeSchemas = ["crm"] })).Tables.ShouldNotContain(t => t.Schema == "crm");
      (await IntrospectAsync(shop, new IntrospectionOptions { IncludeViews = false })).Tables.ShouldNotContain(t => t.Name == "open_orders");
   }

   /// <summary>Enforced foreign keys make navigations that always have a row; unchecked ones (NOT VALID, WITH NOCHECK) don't.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   public async Task BuildsANavigableCatalog(ServerKind server)
   {
      ServerDatabase shop = await servers.ShopAsync(server);
      SourceSchema schema = await IntrospectAsync(shop);
      QueryCatalog catalog = new CatalogBuilder().AddSource(new SourceInfo("shop", shop.Provider.ProviderKind, schema.DefaultSchema), schema).Build();
      catalog.Diagnostics.ShouldBeEmpty();
      EntityDef orders = catalog.FindEntity("shop.orders").ShouldNotBeNull();
      orders.Navigations.Single(n => n.Name == "customer").Multiplicity.ShouldBe(Multiplicity.One);
      orders.Navigations.Single(n => n.Name == "ship_address").Multiplicity.ShouldBe(Multiplicity.ZeroOrOne);
      catalog.FindEntity("shop.employees")!.Navigations.Select(n => n.Name).ShouldContain("manager");
      schema.Tables.Single(t => t.Name == "employees").ForeignKeys.ShouldHaveSingleItem().IsEnforced.ShouldBeFalse();
      catalog.FindEntity("shop.crm.contacts").ShouldNotBeNull().DisplayColumn!.Name.ShouldBe("email");
   }
}
