using System.Linq;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Tests.Binding;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.Tests.Catalog;

public sealed class VirtualEntityTests
{
   private static QueryCatalog Build(params OverlayVirtualEntity[] virtualEntities) =>
      TestCatalogs.Sales(TestCatalogs.SalesOverlay(virtualEntities));

   private static QueryCatalog Build(CatalogOverlay overlay) => TestCatalogs.Sales(overlay);

   [Fact]
   public void FilteringDefinitionsKeepTheKeyAndNavigations()
   {
      QueryCatalog catalog = Build(new OverlayVirtualEntity("reports.big_orders", "sales.orders.where(total > 100)"));
      catalog.Diagnostics.ShouldBeEmpty();
      VirtualEntity big = catalog.FindEntity("reports.big_orders").ShouldBeOfType<VirtualEntity>();
      big.Kind.ShouldBe(EntityKind.Virtual);
      big.BaseEntity.ShouldBeSameAs(catalog.FindEntity("sales.orders"));
      big.Columns.Select(c => c.Name).ShouldBe(["id", "customer_id", "status", "total", "order_date", "shipped_at", "ship_customer_id", "attachment"]);
      big.Key!.Columns.Single().Name.ShouldBe("id");
      big.InheritedNavigations.Select(n => n.Name).ShouldBe(["customer", "ship_customer", "order_lines"]);

      BoundProgram program = Binder.Bind("reports.big_orders.select(id, who: customer.name)", catalog);
      program.Success.ShouldBeTrue();
      program.Query!.Shape.ToString().ShouldBe("[id int64, who string(100)]");
   }

   [Fact]
   public void ProjectingDefinitionsHaveTheirOwnColumnsOnly()
   {
      QueryCatalog catalog = Build(new OverlayVirtualEntity("reports.cities", "sales.customers.select(id, name, town: city ?? '?')"));
      VirtualEntity cities = catalog.FindEntity("reports.cities").ShouldBeOfType<VirtualEntity>();
      cities.Columns.Select(c => $"{c.Name} {c.Type}").ShouldBe(["id int32", "name string(100)", "town string"]);
      cities.Key.ShouldBeNull();
      cities.InheritedNavigations.ShouldBeEmpty();
      cities.DisplayColumn!.Name.ShouldBe("name");
   }

   [Fact]
   public void DeclaredKeysAndRelationsMakeVirtualEntitiesNavigable()
   {
      CatalogOverlay overlay = TestCatalogs.SalesOverlay(new OverlayVirtualEntity("reports.cities", "sales.customers.select(id, name, city)") { Key = ["id"] }) with
      {
         Relations =
         [
            .. TestCatalogs.SalesOverlay().Relations,
            new OverlayRelation("crm.contacts", ["customer_ref"], "reports.cities", ["id"]) { Name = "home" },
         ],
      };
      QueryCatalog catalog = Build(overlay);
      catalog.Diagnostics.ShouldBeEmpty();
      catalog.FindEntity("reports.cities")!.Navigations.Select(n => n.Name).ShouldBe(["contacts"]);
      BoundProgram program = Binder.Bind("crm.contacts.select(email, town: home.city)", catalog);
      program.Success.ShouldBeTrue();
      program.Query!.Shape.ToString().ShouldBe("[email string, town string?]");
   }

   [Fact]
   public void VirtualEntitiesCanUseEachOtherInAnyOrder()
   {
      QueryCatalog catalog = Build(
         new OverlayVirtualEntity("reports.biggest", "reports.big_orders.where(total > 1000)"),
         new OverlayVirtualEntity("reports.big_orders", "sales.orders.where(total > 100)"));
      catalog.Diagnostics.ShouldBeEmpty();
      VirtualEntity biggest = catalog.FindEntity("reports.biggest").ShouldBeOfType<VirtualEntity>();
      biggest.BaseEntity.ShouldBeSameAs(catalog.FindEntity("reports.big_orders"));
      biggest.InheritedNavigations.Select(n => n.Name).ShouldContain("customer");
      biggest.Key!.Columns.Single().Name.ShouldBe("id");
   }

   [Fact]
   public void CyclesBreakEveryEntityInThem()
   {
      QueryCatalog catalog = Build(
         new OverlayVirtualEntity("reports.a", "reports.b.where(total > 1)"),
         new OverlayVirtualEntity("reports.b", "reports.a.where(total > 1)"));
      catalog.Diagnostics.Select(d => d.Code).ShouldBe([DiagnosticCodes.BrokenVirtualEntity, DiagnosticCodes.BrokenVirtualEntity]);
      catalog.FindEntity("reports.a").ShouldBeOfType<VirtualEntity>().Problem.ShouldNotBeNull();
      catalog.FindEntity("reports.b").ShouldBeOfType<VirtualEntity>().Problem!.ShouldContain("in terms of itself");
   }

   [Fact]
   public void BrokenDefinitionsAreReportedAndRefuseToBind()
   {
      QueryCatalog catalog = Build(new OverlayVirtualEntity("reports.bad", "sales.orders.where(nope > 1)"));
      CatalogDiagnostic diagnostic = catalog.Diagnostics.ShouldHaveSingleItem();
      diagnostic.Code.ShouldBe(DiagnosticCodes.BrokenVirtualEntity);
      diagnostic.Message.ShouldContain("There is no 'nope' here");

      BoundProgram program = Binder.Bind("reports.bad.take(1)", catalog);
      program.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.BrokenVirtualEntity);
   }

   [Fact]
   public void DefinitionsMustProduceColumns()
   {
      QueryCatalog catalog = Build(
         new OverlayVirtualEntity("reports.rows", "sales.orders.select(id, customer)"),
         new OverlayVirtualEntity("reports.value", "1 + 1"));
      catalog.Diagnostics.Count.ShouldBe(2);
      catalog.FindEntity("reports.rows").ShouldBeOfType<VirtualEntity>().Problem!.ShouldContain("whole row");
      catalog.FindEntity("reports.value").ShouldBeOfType<VirtualEntity>().Problem!.ShouldContain("not a single value");
   }

   [Fact]
   public void PathsNeedANamespaceAndMustBeFree()
   {
      QueryCatalog catalog = Build(
         new OverlayVirtualEntity("lonely", "sales.orders"),
         new OverlayVirtualEntity("sales.main.orders", "sales.orders"),
         new OverlayVirtualEntity("sales.orders.x", "sales.orders"));
      catalog.Diagnostics.Select(d => d.Code).ShouldBe([DiagnosticCodes.InvalidEntityPath, DiagnosticCodes.InvalidEntityPath, DiagnosticCodes.InvalidEntityPath]);
   }

   [Fact]
   public void VirtualEntitiesCanLiveUnderASource()
   {
      QueryCatalog catalog = Build(new OverlayVirtualEntity("sales.open_orders", "sales.orders.where(status == 'open')"));
      catalog.Diagnostics.ShouldBeEmpty();
      catalog.FindEntity("sales.open_orders").ShouldBeOfType<VirtualEntity>().DisplayName.ShouldBe("sales.open_orders");
   }

   [Fact]
   public void SettingsAndRenamesApplyToVirtualEntities()
   {
      CatalogOverlay overlay = TestCatalogs.SalesOverlay(new OverlayVirtualEntity("reports.big_orders", "sales.orders.where(total > 100)")) with
      {
         Entities = [new OverlayEntitySettings("reports.big_orders") { DisplayColumn = "total", Columns = [new OverlayColumn("status") { Hidden = true }] }],
      };
      QueryCatalog catalog = Build(overlay);
      catalog.Diagnostics.ShouldBeEmpty();
      EntityDef big = catalog.FindEntity("reports.big_orders")!;
      big.DisplayColumn!.Name.ShouldBe("total");
      big.FindColumn("status").Item!.Hidden.ShouldBeTrue();
   }
}
