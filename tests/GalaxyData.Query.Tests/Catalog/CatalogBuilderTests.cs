using System.Linq;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Types;
using Shouldly;
using Xunit;
using static GalaxyData.Query.Tests.Catalog.Schemas;

namespace GalaxyData.Query.Tests.Catalog;

public sealed class CatalogBuilderTests
{
   [Fact]
   public void DefaultSchemaEntitiesAreReachableByShortcut()
   {
      QueryCatalog catalog = Build(("shop", Shop()));
      EntityDef orders = catalog.Entity("shop.orders");
      catalog.Entity("shop.main.orders").ShouldBeSameAs(orders);
      orders.DisplayName.ShouldBe("shop.orders");
      orders.QualifiedName.ToString().ShouldBe("shop.main.orders");
      catalog.Diagnostics.ShouldBeEmpty();
   }

   [Fact]
   public void OtherSchemasNeedTheirSchemaName()
   {
      QueryCatalog catalog = Build(("wh", Source(Table("stock", Col("id")), InSchema("crm", "contacts", Col("id")))));
      catalog.Entity("wh.crm.contacts").DisplayName.ShouldBe("wh.crm.contacts");
      catalog.FindEntity(EntityName.Parse("wh.contacts")).ShouldBeNull();
   }

   [Fact]
   public void ShortcutIsSuppressedWhenATableIsNamedLikeASchema()
   {
      QueryCatalog catalog = Build(("wh", Source(Table("crm", Col("id")), InSchema("crm", "contacts", Col("id")))));
      catalog.Resolve(EntityName.Parse("wh.crm")).Item.ShouldBeOfType<CatalogNamespace>();
      catalog.Entity("wh.main.crm").DisplayName.ShouldBe("wh.main.crm");
      CatalogDiagnostic diagnostic = catalog.Diagnostics.ShouldHaveSingleItem();
      diagnostic.Code.ShouldBe(DiagnosticCodes.ShortcutSuppressed);
   }

   [Fact]
   public void LookupIgnoresCaseButPrefersAnExactMatch()
   {
      QueryCatalog catalog = Build(("pg", Source(Table("Orders", Col("id")), Table("orders", Col("id")), Table("lines", Col("id")))));
      catalog.Entity("PG.LINES").Name.ShouldBe("lines");
      catalog.Entity("pg.Orders").Name.ShouldBe("Orders");
      catalog.Entity("pg.orders").Name.ShouldBe("orders");
      NameMatch<CatalogItem> ambiguous = catalog.Resolve(EntityName.Parse("pg.ORDERS"));
      ambiguous.Status.ShouldBe(MatchStatus.Ambiguous);
      ambiguous.Candidates.Count.ShouldBe(2);
   }

   [Fact]
   public void ForeignKeysBecomeForwardAndInverseNavigations()
   {
      QueryCatalog catalog = Build(("shop", Shop()));
      EntityDef orders = catalog.Entity("shop.orders");
      EntityDef customers = catalog.Entity("shop.customers");

      NavigationDef customer = orders.Nav("customer");
      customer.Target.ShouldBeSameAs(customers);
      customer.Multiplicity.ShouldBe(Multiplicity.One);
      customer.OwnerColumns.Single().Name.ShouldBe("customer_id");
      customer.TargetColumns.Single().Name.ShouldBe("id");

      NavigationDef inverse = customers.Nav("orders");
      inverse.IsCollection.ShouldBeTrue();
      inverse.Opposite.ShouldBeSameAs(customer);
      customers.NavNames().ShouldBe(["addresses", "orders"]);
   }

   [Fact]
   public void SeveralRelationsFromOneTableGetQualifiedInverseNames()
   {
      QueryCatalog catalog = Build(("shop", Shop()));
      catalog.Entity("shop.orders").NavNames().ShouldBe(["customer", "ship_address", "bill_address", "order_lines"]);
      catalog.Entity("shop.addresses").NavNames().ShouldBe(["customer", "orders_by_ship_address", "orders_by_bill_address"]);
      catalog.Entity("shop.orders").Nav("ship_address").Multiplicity.ShouldBe(Multiplicity.ZeroOrOne);
   }

   [Fact]
   public void SelfReferencesAreQualified()
   {
      QueryCatalog catalog = Build(("hr", Source(
         Table("employees", Col("id"), Col("manager_id", "int64?")) with { PrimaryKey = Pk("id"), ForeignKeys = [Fk("manager_id", "employees")] })));
      EntityDef employees = catalog.Entity("hr.employees");
      employees.NavNames().ShouldBe(["manager", "employees_by_manager"]);
      employees.Nav("employees_by_manager").IsCollection.ShouldBeTrue();
   }

   [Fact]
   public void CompositeForeignKeysAreNamedAfterTheTargetTable()
   {
      QueryCatalog catalog = Build(("shop", Source(
         Table("order_lines", Col("order_id"), Col("line_no")) with { PrimaryKey = Pk("order_id", "line_no") },
         Table("shipments", Col("id"), Col("order_id"), Col("line_no")) with
         {
            PrimaryKey = Pk("id"),
            ForeignKeys = [Fk(["order_id", "line_no"], "order_lines", ["order_id", "line_no"])],
         })));
      catalog.Entity("shop.shipments").NavNames().ShouldBe(["order_lines"]);
      catalog.Entity("shop.order_lines").NavNames().ShouldBe(["shipments"]);
   }

   [Fact]
   public void UniqueForeignKeysMakeASingleInverse()
   {
      QueryCatalog catalog = Build(("shop", Source(
         Table("customers", Col("id")) with { PrimaryKey = Pk("id") },
         Table("profiles", Col("customer_id"), Col("bio", "string?")) with
         {
            PrimaryKey = Pk("customer_id"),
            ForeignKeys = [Fk("customer_id", "customers")],
         })));
      NavigationDef profile = catalog.Entity("shop.customers").Nav("profiles");
      profile.Multiplicity.ShouldBe(Multiplicity.ZeroOrOne);
      profile.IsCollection.ShouldBeFalse();
   }

   [Fact]
   public void NameCollisionsFallBackToTheConstraintNameThenANumber()
   {
      QueryCatalog catalog = Build(("shop", Source(
         Table("customers", Col("id")) with { PrimaryKey = Pk("id") },
         Table("orders", Col("id"), Col("customer_id"), Col("customer", "string?"), Col("customer2_id")) with
         {
            PrimaryKey = Pk("id"),
            ForeignKeys = [Fk("customer_id", "customers", name: "fk_orders_customer"), Fk("customer2_id", "customers")],
         })));
      EntityDef orders = catalog.Entity("shop.orders");
      orders.NavNames().ShouldBe(["fk_orders_customer", "customer2"]);
      catalog.Entity("shop.customers").NavNames().ShouldBe(["orders_by_fk_orders_customer", "orders_by_customer2"]);
   }

   [Fact]
   public void NumberedFallbackWhenThereIsNoConstraintName()
   {
      QueryCatalog catalog = Build(("shop", Source(
         Table("customers", Col("id")) with { PrimaryKey = Pk("id") },
         Table("orders", Col("id"), Col("customer_id"), Col("customer", "string?")) with
         {
            PrimaryKey = Pk("id"),
            ForeignKeys = [Fk("customer_id", "customers")],
         })));
      catalog.Entity("shop.orders").NavNames().ShouldBe(["customer_2"]);
   }

   [Fact]
   public void MultiplicityFollowsNullabilityAndEnforcement()
   {
      QueryCatalog catalog = Build(("shop", Source(
         Table("customers", Col("id")) with { PrimaryKey = Pk("id") },
         Table("a", Col("id"), Col("customer_id")) with { PrimaryKey = Pk("id"), ForeignKeys = [Fk("customer_id", "customers")] },
         Table("b", Col("id"), Col("customer_id", "int64?")) with { PrimaryKey = Pk("id"), ForeignKeys = [Fk("customer_id", "customers")] },
         Table("c", Col("id"), Col("customer_id")) with
         {
            PrimaryKey = Pk("id"),
            ForeignKeys = [Fk("customer_id", "customers") with { IsEnforced = false }],
         })));
      catalog.Entity("shop.a").Nav("customer").Multiplicity.ShouldBe(Multiplicity.One);
      catalog.Entity("shop.b").Nav("customer").Multiplicity.ShouldBe(Multiplicity.ZeroOrOne);
      catalog.Entity("shop.c").Nav("customer").Multiplicity.ShouldBe(Multiplicity.ZeroOrOne);
   }

   [Fact]
   public void TrustedSourcesTreatUnenforcedForeignKeysAsEnforced()
   {
      SourceSchema schema = Source(
         Table("customers", Col("id")) with { PrimaryKey = Pk("id") },
         Table("orders", Col("id"), Col("customer_id")) with
         {
            PrimaryKey = Pk("id"),
            ForeignKeys = [Fk("customer_id", "customers") with { IsEnforced = false }],
         });
      QueryCatalog catalog = new CatalogBuilder()
         .AddSource(Info("plain"), schema)
         .AddSource(Info("trusted") with { TrustForeignKeys = true }, schema)
         .Build();
      catalog.Entity("plain.orders").Nav("customer").Multiplicity.ShouldBe(Multiplicity.ZeroOrOne);
      catalog.Entity("trusted.orders").Nav("customer").Multiplicity.ShouldBe(Multiplicity.One);
   }

   [Fact]
   public void ForeignKeysWithoutReferencedColumnsUseThePrimaryKey()
   {
      QueryCatalog catalog = Build(("shop", Source(
         Table("customers", Col("id")) with { PrimaryKey = Pk("id") },
         Table("orders", Col("id"), Col("customer_id")) with { PrimaryKey = Pk("id"), ForeignKeys = [Fk("customer_id", "customers", refColumn: null)] })));
      catalog.Entity("shop.orders").Nav("customer").TargetColumns.Single().Name.ShouldBe("id");
   }

   [Fact]
   public void ForeignKeyTableNamesMatchIgnoringCase()
   {
      QueryCatalog catalog = Build(("shop", Source(
         Table("customers", Col("id")) with { PrimaryKey = Pk("id") },
         Table("orders", Col("id"), Col("customer_id")) with { PrimaryKey = Pk("id"), ForeignKeys = [Fk("customer_id", "Customers")] })));
      catalog.Entity("shop.orders").NavNames().ShouldBe(["customer"]);
   }

   [Fact]
   public void ForeignKeysToMissingTablesAreReported()
   {
      QueryCatalog catalog = Build(("shop", Source(
         Table("orders", Col("id"), Col("customer_id")) with { PrimaryKey = Pk("id"), ForeignKeys = [Fk("customer_id", "customers")] })));
      catalog.Entity("shop.orders").Navigations.ShouldBeEmpty();
      catalog.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.ForeignKeyTargetMissing);
   }

   [Fact]
   public void OverlayRelationsLinkSources()
   {
      CatalogOverlay overlay = new()
      {
         Relations = [new OverlayRelation("crm.contacts", ["customer_ref"], "shop.customers", ["id"]) { Name = "customer" }],
      };
      QueryCatalog catalog = Build(overlay,
         ("shop", Shop()),
         ("crm", Source(Table("contacts", Col("id"), Col("customer_ref", "int32?"), Col("email", "string")) with { PrimaryKey = Pk("id") })));

      NavigationDef customer = catalog.Entity("crm.contacts").Nav("customer");
      customer.Target.ShouldBeSameAs(catalog.Entity("shop.customers"));
      customer.Relation.IsCrossSource.ShouldBeTrue();
      customer.Relation.Origin.ShouldBe(RelationOrigin.Overlay);
      customer.Multiplicity.ShouldBe(Multiplicity.ZeroOrOne);
      catalog.Entity("shop.customers").NavNames().ShouldBe(["addresses", "orders", "contacts"]);
      catalog.Diagnostics.ShouldBeEmpty();
   }

   /// <summary>
   /// A relation the overlay adds takes no name the databases' foreign keys give: shop's customers keep their own
   /// orders as <c>orders</c>, and the warehouse's, named by the convention too, are <c>wh_orders</c>.
   /// </summary>
   [Fact]
   public void OverlayRelationsLeaveForeignKeysTheirNames()
   {
      CatalogOverlay overlay = new() { Relations = [new OverlayRelation("wh.orders", ["customer_id"], "shop.customers", ["id"])] };
      QueryCatalog catalog = Build(overlay, ("shop", Shop()), ("wh", Shop()));
      EntityDef customers = catalog.Entity("shop.customers");
      customers.Nav("orders").Target.ShouldBeSameAs(catalog.Entity("shop.orders"));
      customers.Nav("wh_orders").Target.ShouldBeSameAs(catalog.Entity("wh.orders"));
      EntityDef orders = catalog.Entity("wh.orders");
      orders.Nav("customer").Target.ShouldBeSameAs(catalog.Entity("wh.customers"));
      orders.Nav("shop_customer").Target.ShouldBeSameAs(customers);
      catalog.Diagnostics.ShouldBeEmpty();
   }

   [Fact]
   public void OverlayRelationsMustTargetAUniqueKey()
   {
      CatalogOverlay overlay = new()
      {
         Relations = [new OverlayRelation("shop.orders", ["status"], "shop.customers", ["name"])],
      };
      QueryCatalog catalog = Build(overlay, ("shop", Shop()));
      catalog.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.RelationNotUnique);
      catalog.Relations.Count.ShouldBe(5);
   }

   [Fact]
   public void OverlayRelationProblemsAreReported()
   {
      CatalogOverlay overlay = new()
      {
         Relations =
         [
            new OverlayRelation("shop.nope", ["x"], "shop.customers", ["id"]),
            new OverlayRelation("shop.orders", ["nope"], "shop.customers", ["id"]),
            new OverlayRelation("shop.orders", ["status", "total"], "shop.customers", ["id"]),
            new OverlayRelation("shop.orders", ["status"], "shop.customers", ["id"]),
         ],
      };
      QueryCatalog catalog = Build(overlay, ("shop", Shop()));
      catalog.Diagnostics.Select(d => d.Code).ShouldBe(
      [
         DiagnosticCodes.UnknownEntity,
         DiagnosticCodes.UnknownColumn,
         DiagnosticCodes.RelationShape,
         DiagnosticCodes.RelationTypeMismatch,
      ]);
   }

   [Fact]
   public void OverlayCanRenameAndHideNavigations()
   {
      CatalogOverlay overlay = new()
      {
         Navigations =
         [
            new OverlayNavigation("shop.addresses", "orders_by_ship_address") { RenameTo = "shipments" },
            new OverlayNavigation("shop.addresses", "orders_by_bill_address") { Hidden = true },
            new OverlayNavigation("shop.orders", "bill_address") { RenameTo = "status" },
            new OverlayNavigation("shop.orders", "missing"),
         ],
      };
      QueryCatalog catalog = Build(overlay, ("shop", Shop()));
      EntityDef addresses = catalog.Entity("shop.addresses");
      addresses.Nav("shipments").ConventionName.ShouldBe("orders_by_ship_address");
      addresses.FindNavigation("orders_by_ship_address").IsFound.ShouldBeFalse();
      addresses.Nav("orders_by_bill_address").Hidden.ShouldBeTrue();
      catalog.Diagnostics.Select(d => d.Code).ShouldBe([DiagnosticCodes.NavigationNameTaken, DiagnosticCodes.UnknownNavigation]);
   }

   /// <summary>Each problem with the overlay says which of its items it is with, so a tool can mark that one.</summary>
   [Fact]
   public void OverlayProblemsNameTheirItems()
   {
      CatalogOverlay overlay = new()
      {
         Relations =
         [
            new OverlayRelation("shop.addresses", ["id"], "shop.orders", ["id"]) { Name = "buyer" },
            new OverlayRelation("shop.orders", ["nope"], "shop.customers", ["id"]),
            new OverlayRelation("shop.order_lines", ["qty"], "shop.addresses", ["id"]) { Name = "line_no" },
            new OverlayRelation("shop.orders", ["customer_id"], "shop.customers", ["id"]),
            new OverlayRelation("shop.main.addresses", ["id"], "shop.orders", ["id"]),
            new OverlayRelation("shop.orders", ["id", "ID"], "shop.order_lines", ["order_id", "line_no"]),
         ],
         VirtualEntities =
         [
            new OverlayVirtualEntity("reports.fine", "shop.orders.where(total > 1)"),
            new OverlayVirtualEntity("reports.bad", "shop.orders.where(nope > 1)"),
         ],
         Entities =
         [
            new OverlayEntitySettings("shop.orders") { DisplayColumn = "status" },
            new OverlayEntitySettings("shop.customers") { DisplayColumn = "nope", Columns = [new OverlayColumn("name") { Label = "Name" }, new OverlayColumn("NAME") { Hidden = true }] },
            new OverlayEntitySettings("SHOP.main.Orders") { Hidden = true },
            new OverlayEntitySettings("reports.bad") { DisplayColumn = "id" },
         ],
         Navigations =
         [
            new OverlayNavigation("shop.addresses", "buyer") { RenameTo = "client" },
            new OverlayNavigation("shop.main.addresses", "buyer") { Hidden = true },
            new OverlayNavigation("shop.nope", "x") { Hidden = true },
            new OverlayNavigation("reports.bad", "customer") { Hidden = true },
         ],
      };
      QueryCatalog catalog = Build(overlay, ("shop", Shop()));
      catalog.Diagnostics.Select(d => (d.Code, Item: d.Item?.ToString())).ShouldBe(
      [
         (DiagnosticCodes.DuplicateOverlayItem, "EntitySettings[2]"),
         (DiagnosticCodes.DuplicateOverlayItem, "EntitySettings[1]"),
         (DiagnosticCodes.UnknownColumn, "EntitySettings[1]"),
         (DiagnosticCodes.BrokenVirtualEntity, "EntitySettings[3]"),
         (DiagnosticCodes.DuplicateOverlayItem, "Navigation[1]"),
         (DiagnosticCodes.UnknownEntity, "Navigation[2]"),
         (DiagnosticCodes.BrokenVirtualEntity, "Navigation[3]"),
         (DiagnosticCodes.UnknownColumn, "Relation[1]"),
         (DiagnosticCodes.NavigationNameTaken, "Relation[2]"),
         (DiagnosticCodes.DuplicateOverlayItem, "Relation[3]"),
         (DiagnosticCodes.DuplicateOverlayItem, "Relation[4]"),
         (DiagnosticCodes.DuplicateOverlayItem, "Relation[5]"),
         (DiagnosticCodes.BrokenVirtualEntity, "VirtualEntity[1]"),
      ], ignoreOrder: true);
      CatalogDiagnostic duplicate = catalog.Diagnostics.First(d => d.Item == new OverlayItemRef(OverlayItemKind.EntitySettings, 2));
      duplicate.Message.ShouldBe("'SHOP.main.Orders' is shop.orders, which has settings already (as 'shop.orders'), so these are left out");
      catalog.Entity("shop.orders").Hidden.ShouldBeFalse("the second setting is left out");
      catalog.Entity("shop.addresses").Nav("client").Hidden.ShouldBeFalse("so is the second override");
      catalog.Diagnostics.Where(d => d.Item == null).ShouldBeEmpty();
      catalog.Entity("shop.addresses").Nav("client").Relation.OverlayItem.ShouldBe(new OverlayItemRef(OverlayItemKind.Relation, 0));
      catalog.Entity("shop.orders").Nav("customer").Relation.OverlayItem.ShouldBeNull();
      catalog.Diagnostics.First(d => d.Item == new OverlayItemRef(OverlayItemKind.Relation, 3)).Message
         .ShouldBe("The database declares this relation already (a foreign key): rename its navigations instead");
      catalog.Diagnostics.First(d => d.Item == new OverlayItemRef(OverlayItemKind.Relation, 5)).Message.ShouldBe("Column id is named twice");
      ColumnDef name = catalog.Entity("shop.customers").Columns.Single(c => c.Name == "name");
      (name.Label, name.Hidden).ShouldBe(("Name", false), "the first settings of a column apply, the second are left out");
      catalog.FindEntity("reports.fine").ShouldBeOfType<VirtualEntity>().OverlayItem.ShouldBe(new OverlayItemRef(OverlayItemKind.VirtualEntity, 0));
   }

   [Fact]
   public void OverlaySettingsDeclareKeysLabelsAndTypes()
   {
      CatalogOverlay overlay = new()
      {
         Entities =
         [
            new OverlayEntitySettings("shop.open_orders")
            {
               Key = ["id"],
               DisplayColumn = "total",
               Columns = [new OverlayColumn("total") { Label = "Order total", Type = ScalarType.Parse("decimal(12,2)") }],
            },
            new OverlayEntitySettings("shop.customers") { Key = ["name"], Columns = [new OverlayColumn("city") { Hidden = true }] },
         ],
      };
      SourceSchema shop = Shop() with
      {
         Tables = [.. Shop().Tables, View("open_orders", Col("id"), Col("total", "decimal(10,2)?"))],
      };
      QueryCatalog catalog = Build(overlay, ("shop", shop));

      EntityDef view = catalog.Entity("shop.open_orders");
      view.Key.ShouldNotBeNull().IsDeclared.ShouldBeTrue();
      view.DisplayColumn!.Name.ShouldBe("total");
      ColumnDef total = view.FindColumn("total").Item!;
      total.Label.ShouldBe("Order total");
      total.Type.ToString().ShouldBe("decimal(12,2)?");
      ((TableEntity)view).IsWritable.ShouldBeFalse();

      EntityDef customers = catalog.Entity("shop.customers");
      customers.Key!.Columns.Single().Name.ShouldBe("id");
      customers.FindColumn("city").Item!.Hidden.ShouldBeTrue();
      catalog.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(DiagnosticCodes.DeclaredKeyIgnored);
   }

   [Fact]
   public void DisplayColumnsFollowTheConvention()
   {
      QueryCatalog catalog = Build(("shop", Shop()));
      catalog.Entity("shop.customers").DisplayColumn!.Name.ShouldBe("name");
      catalog.Entity("shop.orders").DisplayColumn!.Name.ShouldBe("status");
      catalog.Entity("shop.order_lines").DisplayColumn!.Name.ShouldBe("order_id");
   }

   [Fact]
   public void WritabilityNeedsATableWithAKeyInAWritableSource()
   {
      SourceSchema schema = Source(
         Table("keyed", Col("id")) with { PrimaryKey = Pk("id") },
         Table("heap", Col("id")),
         View("summary", Col("id")));
      QueryCatalog catalog = new CatalogBuilder()
         .AddSource(Info("rw"), schema)
         .AddSource(Info("ro") with { IsReadOnly = true }, schema)
         .Build();
      ((TableEntity)catalog.Entity("rw.keyed")).IsWritable.ShouldBeTrue();
      ((TableEntity)catalog.Entity("rw.heap")).IsWritable.ShouldBeFalse();
      ((TableEntity)catalog.Entity("rw.summary")).IsWritable.ShouldBeFalse();
      ((TableEntity)catalog.Entity("ro.keyed")).IsWritable.ShouldBeFalse();
   }

   [Fact]
   public void InvalidAndDuplicateAliasesAreRejected()
   {
      QueryCatalog catalog = new CatalogBuilder()
         .AddSource(Info("shop"), Shop())
         .AddSource(Info("SHOP"), Shop())
         .AddSource(Info("my shop"), Shop())
         .AddSource(Info("in"), Shop())
         .Build();
      catalog.Sources.Select(s => s.Alias).ShouldBe(["shop"]);
      catalog.Diagnostics.Select(d => d.Code).ShouldBe(
         [DiagnosticCodes.DuplicateSource, DiagnosticCodes.InvalidSourceAlias, DiagnosticCodes.InvalidSourceAlias]);
   }

   [Fact]
   public void OverlayRoundTripsThroughJson()
   {
      CatalogOverlay overlay = new()
      {
         Relations = [new OverlayRelation("crm.contacts", ["customer_ref"], "shop.customers", ["id"]) { Name = "customer", InverseName = "contacts" }],
         Entities = [new OverlayEntitySettings("shop.open_orders") { Key = ["id"], Columns = [new OverlayColumn("total") { Type = ScalarType.Decimal(12, 2) }] }],
         Navigations = [new OverlayNavigation("shop.addresses", "orders_by_ship_address") { RenameTo = "shipments" }],
      };
      string json = overlay.ToJson();
      json.ShouldContain("\"type\": \"decimal(12,2)?\"");
      CatalogOverlay back = CatalogOverlay.FromJson(json);
      back.ToJson().ShouldBe(json);
      back.Relations.Single().InverseName.ShouldBe("contacts");
   }

   /// <summary>Lists an overlay's JSON leaves out are empty, and the catalog builds with them.</summary>
   [Fact]
   public void OverlayJsonMayLeaveListsOut()
   {
      CatalogOverlay overlay = CatalogOverlay.FromJson(
         """{ "relations": [{ "from": "crm.contacts", "fromColumns": ["customer_ref"], "to": "shop.customers", "toColumns": ["id"] }], "entities": [{ "entity": "shop.orders", "hidden": true }] }""");
      overlay.VirtualEntities.ShouldBeEmpty();
      overlay.Navigations.ShouldBeEmpty();
      overlay.Entities.Single().Columns.ShouldBeEmpty();
      QueryCatalog catalog = Build(overlay,
         ("shop", Shop()),
         ("crm", Source(Table("contacts", Col("id"), Col("customer_ref", "int32?")) with { PrimaryKey = Pk("id") })));
      catalog.Entity("crm.contacts").Nav("customer").Target.ShouldBeSameAs(catalog.Entity("shop.customers"));
      catalog.Entity("shop.orders").Hidden.ShouldBeTrue();
   }

   /// <summary>An overlay's JSON without the names a relation, virtual entity or setting needs fails to load, saying which.</summary>
   [Theory]
   [InlineData("""{ "relations": [{ "from": "a.b", "to": "c.d", "toColumns": ["id"] }] }""", "A relation of the overlay needs \"from\", \"fromColumns\", \"to\" and \"toColumns\"")]
   [InlineData("""{ "virtualEntities": [{ "name": "r.x" }] }""", "A virtual entity of the overlay needs \"name\" and \"query\"")]
   [InlineData("""{ "entities": [{ "hidden": true }] }""", "An entity setting of the overlay needs \"entity\"")]
   [InlineData("""{ "navigations": [{ "entity": "a.b" }] }""", "A navigation setting of the overlay needs \"entity\" and \"name\"")]
   public void OverlayJsonWithoutNamesItNeedsFails(string json, string message) =>
      Should.Throw<System.Text.Json.JsonException>(() => CatalogOverlay.FromJson(json)).Message.ShouldBe(message);
}
