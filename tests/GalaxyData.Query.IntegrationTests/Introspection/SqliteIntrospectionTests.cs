using System.Linq;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Sqlite;
using Microsoft.Data.Sqlite;
using GalaxyData.Testing;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Introspection;

public sealed class SqliteIntrospectionTests
{
   private static async Task<SourceSchema> IntrospectShopAsync(IntrospectionOptions? options = null)
   {
      await using SqliteConnection connection = await Fixtures.OpenSqliteShopAsync();
      return await new SqliteSchemaIntrospector().IntrospectAsync(connection, options ?? IntrospectionOptions.Default, TestContext.Current.CancellationToken);
   }

   [Fact]
   public async Task IntrospectsTheShopFixture()
   {
      SourceSchema schema = await IntrospectShopAsync();
      schema.ServerVersion.ShouldNotBeNullOrEmpty();
      Golden.Match(IntrospectionJson.Serialize(schema with { ServerVersion = "(version)" }, indented: true), "json");
   }

   [Fact]
   public async Task RoundTripsThroughJsonWithAStableHash()
   {
      SourceSchema schema = await IntrospectShopAsync();
      string json = IntrospectionJson.Serialize(schema);
      SourceSchema back = IntrospectionJson.Deserialize(json);
      IntrospectionJson.Serialize(back).ShouldBe(json);
      IntrospectionJson.Hash(back).ShouldBe(IntrospectionJson.Hash(await IntrospectShopAsync()));
   }

   [Fact]
   public async Task ExcludingMainLeavesNothing()
   {
      SourceSchema schema = await IntrospectShopAsync(new IntrospectionOptions { ExcludeSchemas = ["main"] });
      schema.Tables.ShouldBeEmpty();
   }

   [Fact]
   public async Task ViewsCanBeLeftOut()
   {
      SourceSchema schema = await IntrospectShopAsync(new IntrospectionOptions { IncludeViews = false });
      schema.Tables.ShouldAllBe(t => t.Kind == TableKind.Table);
   }

   [Fact]
   public async Task BuildsANavigableCatalog()
   {
      SourceSchema schema = await IntrospectShopAsync();
      QueryCatalog catalog = new CatalogBuilder().AddSource(new SourceInfo("shop", SqliteSchemaIntrospector.ProviderKind, schema.DefaultSchema), schema).Build();

      catalog.Diagnostics.ShouldBeEmpty();
      EntityDef orders = catalog.FindEntity("shop.orders").ShouldNotBeNull();
      orders.Navigations.Select(n => n.Name).ShouldBe(["customer", "ship_address", "bill_address", "order_lines"]);
      orders.Navigations[0].Multiplicity.ShouldBe(Multiplicity.ZeroOrOne, "SQLite foreign keys are not enforced");
      ((TableEntity)orders).HasTriggers.ShouldBeTrue();
      ((TableEntity)orders).IsWritable.ShouldBeTrue();

      EntityDef employees = catalog.FindEntity("shop.employees").ShouldNotBeNull();
      employees.Navigations.Select(n => n.Name).ShouldBe(["manager", "employees_by_manager"]);

      EntityDef customers = catalog.FindEntity("shop.customers").ShouldNotBeNull();
      customers.UniqueKeys.Single().Columns.Single().Name.ShouldBe("name");
      customers.DisplayColumn!.Name.ShouldBe("name");

      ((TableEntity)catalog.FindEntity("shop.audit_log")!).IsWritable.ShouldBeFalse();
      ((TableEntity)catalog.FindEntity("shop.open_orders")!).Kind.ShouldBe(EntityKind.View);
   }
}
