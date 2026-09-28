using System.Linq;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.DuckDb;
using GalaxyData.Query.Introspection;
using GalaxyData.Testing;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.IntegrationTests.Introspection;

public sealed class DuckDbIntrospectionTests
{
   private static async Task<SourceSchema> IntrospectShopAsync(IntrospectionOptions? options = null)
   {
      await using DuckDBConnection connection = await Fixtures.OpenDuckDbShopAsync();
      return await new DuckDbSchemaIntrospector().IntrospectAsync(connection, options ?? IntrospectionOptions.Default, TestContext.Current.CancellationToken);
   }

   [Fact]
   public async Task IntrospectsTheShopFixture()
   {
      SourceSchema schema = await IntrospectShopAsync();
      schema.ServerVersion.ShouldNotBeNullOrEmpty();
      Golden.Match(IntrospectionJson.Serialize(schema with { ServerVersion = "(version)" }, indented: true), "json");
   }

   [Fact]
   public async Task SchemasCanBeFiltered()
   {
      SourceSchema onlyCrm = await IntrospectShopAsync(new IntrospectionOptions { IncludeSchemas = ["crm"] });
      onlyCrm.Tables.Select(t => t.Name).ShouldBe(["contacts"]);
      SourceSchema noCrm = await IntrospectShopAsync(new IntrospectionOptions { ExcludeSchemas = ["CRM"] });
      noCrm.Tables.ShouldNotContain(t => t.Schema == "crm");
   }

   [Fact]
   public async Task BuildsANavigableCatalogWithAnOverlayRelation()
   {
      SourceSchema schema = await IntrospectShopAsync();
      CatalogOverlay overlay = new()
      {
         Relations = [new OverlayRelation("wh.crm.contacts", ["customer_ref"], "wh.customers", ["id"])],
      };
      QueryCatalog catalog = new CatalogBuilder()
         .AddSource(new SourceInfo("wh", DuckDbSchemaIntrospector.ProviderKind, schema.DefaultSchema), schema)
         .WithOverlay(overlay)
         .Build();

      catalog.Diagnostics.ShouldBeEmpty();
      EntityDef orders = catalog.FindEntity("wh.orders").ShouldNotBeNull();
      orders.Navigations[0].Name.ShouldBe("customer");
      orders.Navigations[0].Multiplicity.ShouldBe(Multiplicity.One, "DuckDB enforces foreign keys and customer_id is not null");
      catalog.FindEntity("wh.crm.contacts")!.Navigations.Single().Name.ShouldBe("customer");
      catalog.FindEntity("wh.customers")!.Navigations.Select(n => n.Name).ShouldContain("contacts");
      catalog.FindEntity("wh.crm.contacts")!.DisplayColumn!.Name.ShouldBe("email");
   }

   [Theory]
   [InlineData("[status]", new[] { "status" })]
   [InlineData("[status, total]", new[] { "status", "total" })]
   [InlineData("[\"Display Name\", \"say \"\"hi\"\"\"]", new[] { "Display Name", "say \"hi\"" })]
   [InlineData("[lower(name), id]", new[] { "lower(name)", "id" })]
   [InlineData("[coalesce(a, b)]", new[] { "coalesce(a, b)" })]
   public void SplitsIndexExpressions(string expressions, string[] expected)
   {
      DuckDbSchemaIntrospector.IndexColumns(expressions).ShouldBe(expected);
   }
}
