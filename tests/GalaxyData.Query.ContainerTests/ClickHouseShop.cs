using System.Collections.Generic;
using GalaxyData.Query.Catalog;

namespace GalaxyData.Query.ContainerTests;

/// <summary>
/// The keys and relations the shop has in the other databases, which ClickHouse doesn't keep: declared in an overlay,
/// with the names the other databases' foreign keys give their navigations.
/// </summary>
internal static class ClickHouseShop
{
   /// <summary>
   /// <paramref name="overlay"/> with the keys and relations of the shop's tables in ClickHouse: orders and their lines
   /// in <paramref name="sales"/>, customers, addresses and employees in <paramref name="crm"/> (null where they aren't
   /// in ClickHouse). The orders' relations to customers and addresses are declared when both are in one ClickHouse
   /// database; across sources the split shop's overlay has them.
   /// </summary>
   public static CatalogOverlay Keyed(CatalogOverlay overlay, string? sales, string? crm)
   {
      List<OverlayEntitySettings> keys = [];
      List<OverlayRelation> relations = [];
      if (crm != null)
      {
         keys.Add(new OverlayEntitySettings($"{crm}.customers") { Key = ["id"] });
         keys.Add(new OverlayEntitySettings($"{crm}.addresses") { Key = ["id"] });
         keys.Add(new OverlayEntitySettings($"{crm}.employees") { Key = ["id"] });
         relations.Add(new OverlayRelation($"{crm}.addresses", ["customer_id"], $"{crm}.customers", ["id"]) { Name = "customer", InverseName = "addresses" });
         relations.Add(new OverlayRelation($"{crm}.employees", ["manager_id"], $"{crm}.employees", ["id"]) { Name = "manager", InverseName = "employees" });
      }
      if (sales != null)
      {
         keys.Add(new OverlayEntitySettings($"{sales}.orders") { Key = ["id"] });
         keys.Add(new OverlayEntitySettings($"{sales}.order_lines") { Key = ["order_id", "line_no"] });
         relations.Add(new OverlayRelation($"{sales}.order_lines", ["order_id"], $"{sales}.orders", ["id"]) { Name = "order", InverseName = "order_lines" });
      }
      if (sales != null && sales == crm)
      {
         relations.Add(new OverlayRelation($"{sales}.orders", ["customer_id"], $"{crm}.customers", ["id"]) { Name = "customer", InverseName = "orders" });
         relations.Add(new OverlayRelation($"{sales}.orders", ["ship_address_id"], $"{crm}.addresses", ["id"]) { Name = "ship_address", InverseName = "orders_by_ship_address" });
         relations.Add(new OverlayRelation($"{sales}.orders", ["bill_address_id"], $"{crm}.addresses", ["id"]) { Name = "bill_address", InverseName = "orders_by_bill_address" });
      }
      return overlay with { Entities = [.. overlay.Entities, .. keys], Relations = [.. overlay.Relations, .. relations] };
   }
}
