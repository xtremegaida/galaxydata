using System.Collections.Generic;
using System.Text.Json;
using GalaxyData.Web.Dashboards;

namespace GalaxyData.Web.Tests.Dashboards;

/// <summary>Definitions to test with, over the shop fixture: orders by status, customers by city, linked through <c>customer</c>.</summary>
internal static class Definitions
{
   public static Listens All { get; } = new(ListenMode.All, []);

   public static DashboardDefinition Blank() => DashboardDefinition.Blank();

   public static BarConfig Bar(string source, string column, string? series = null, int limit = 25) =>
      new(source, [], Emits: true, All, new Dimension(new FieldRef([], column), null, column), series == null ? null : new Dimension(new FieldRef([], series), null, series),
         [new Measure(Aggregate.Count, null, "Rows", null)], new WidgetSort(SortTarget.Measure, 0, Descending: true), limit, BarOrientation.Vertical, BarStack.None,
         Labels: false, LegendPosition.Bottom, XTitle: null, YTitle: null);

   public static PieConfig Pie(string source, string column, Aggregate aggregate = Aggregate.Count, string? field = null, bool other = false) =>
      new(source, [], Emits: true, All, new Dimension(new FieldRef([], column), null, column),
         new Measure(aggregate, field == null ? null : new FieldRef([], field), "Value", null), 10, other, Donut: false, Labels: true, LegendPosition.Right);

   public static DashboardDefinition Sales() => Blank() with
   {
      Layout = DashboardLayout.Default() with
      {
         Items = new Dictionary<string, Placement>
         {
            ["title"] = new(0, 0, 12, 1),
            ["by-status"] = new(0, 1, 6, 6),
            ["by-city"] = new(6, 1, 6, 6),
         },
      },
      Sources = [new DashboardSource("orders", "shop.orders", "Orders"), new DashboardSource("customers", "shop.customers", "Customers")],
      Links = [new DashboardLink("orders", "customers", ["customer"])],
      Filters =
      [
         new DashboardFilter("status", "Status", new SourceField("orders", [], "status"), FilterKind.Values, new ConditionValue(ConditionOp.In, Values: ["open", "shipped"]),
            Visible: true, Editable: true, Multiple: true, Except: []),
      ],
      Widgets =
      [
         new DashboardWidget("title", null, new TextConfig("# Sales")),
         new DashboardWidget("by-status", "Orders by status", Bar("orders", "status")),
         new DashboardWidget("by-city", "Customers by city", Pie("customers", "city")),
      ],
   };

   /// <summary>The definition as the API's JSON writes it (enums by name), to send.</summary>
   public static JsonElement Json(DashboardDefinition definition) => JsonSerializer.SerializeToElement(definition, DefinitionJson.Options);
}
