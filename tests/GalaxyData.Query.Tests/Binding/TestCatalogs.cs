using GalaxyData.Query.Catalog;
using GalaxyData.Query.Introspection;
using static GalaxyData.Query.Tests.Catalog.Schemas;

namespace GalaxyData.Query.Tests.Binding;

/// <summary>A sales source, a crm source linked to it by an overlay relation, and a spreadsheet-like source with awkward names.</summary>
internal static class TestCatalogs
{
   public static SourceSchema SalesSchema() => Source(
      Table("regions", Col("code", "string(3)"), Col("name", "string")) with { PrimaryKey = Pk("code") },
      Table("customers", Col("id", "int32"), Col("name", "string(100)"), Col("city", "string?"), Col("credit_limit", "decimal(10,2)?"),
            Col("created", "date?"), Col("region_code", "string(3)?"), Col("uid", "guid?"), Col("notes", "json?")) with
      {
         PrimaryKey = Pk("id"),
         ForeignKeys = [Fk("region_code", "regions", "code")],
      },
      Table("orders", Col("id"), Col("customer_id", "int32"), Col("status", "string(20,ansi)"), Col("total", "decimal(10,2)"),
            Col("order_date", "date"), Col("shipped_at", "datetime?"), Col("ship_customer_id", "int32?"), Col("attachment", "binary?")) with
      {
         PrimaryKey = Pk("id"),
         ForeignKeys = [Fk("customer_id", "customers"), Fk("ship_customer_id", "customers")],
      },
      Table("products", Col("code", "string(20)"), Col("name", "string"), Col("price", "decimal(10,2)")) with { PrimaryKey = Pk("code") },
      Table("order_lines", Col("order_id"), Col("line_no", "int32"), Col("product_code", "string(20)"), Col("qty", "int32"), Col("price", "decimal(10,2)")) with
      {
         PrimaryKey = Pk("order_id", "line_no"),
         ForeignKeys = [Fk("order_id", "orders"), Fk("product_code", "products", "code")],
      });

   public static SourceSchema CrmSchema() => Source(
      Table("contacts", Col("id", "guid"), Col("customer_ref", "int32?"), Col("email", "string"), Col("Display Name", "string?")) with
      {
         PrimaryKey = Pk("id"),
      });

   public static SourceSchema SheetsSchema() => new("test", null, string.Empty,
   [
      InSchema("Budget 2024", "Sheet 1", Col("Line Item", "string?"), Col("Amount", "double?")),
   ]);

   public static CatalogOverlay SalesOverlay(params OverlayVirtualEntity[] virtualEntities) => new()
   {
      Relations = [new OverlayRelation("crm.contacts", ["customer_ref"], "sales.customers", ["id"]) { Name = "customer" }],
      VirtualEntities = virtualEntities,
   };

   public static QueryCatalog Sales(CatalogOverlay? overlay = null) => new CatalogBuilder()
      .AddSource(Info("sales"), SalesSchema())
      .AddSource(Info("crm"), CrmSchema())
      .AddSource(Info("xl", string.Empty), SheetsSchema())
      .WithOverlay(overlay ?? SalesOverlay())
      .Build();
}
