using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GalaxyData.Web.Dashboards;
using GalaxyData.Web.Hosting;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Dashboards;

public sealed class DefinitionRulesTests
{
   private static readonly DefinitionRules Rules = new(new WidgetKinds(WidgetKinds.BuiltIn()), new DashboardSettings());

   private static (DashboardDefinition? Checked, Dictionary<string, string[]> Errors) Check(DashboardDefinition definition, DefinitionRules? rules = null)
   {
      Dictionary<string, string[]> errors = [];
      DashboardDefinition? checkedDefinition = (rules ?? Rules).Check(definition, "definition.", errors);
      return (checkedDefinition, errors);
   }

   private static string[] Fields(DashboardDefinition definition, DefinitionRules? rules = null) => [.. Check(definition, rules).Errors.Keys.Order()];

   [Fact]
   public void ABlankDashboardAndOneWithWidgetsPass()
   {
      Check(Definitions.Blank()).Errors.ShouldBeEmpty();
      (DashboardDefinition? sales, Dictionary<string, string[]> errors) = Check(Definitions.Sales());
      errors.ShouldBeEmpty();
      string json = DefinitionJson.Write(sales!);
      DefinitionJson.Write(DefinitionJson.Read(json)).ShouldBe(json, "written as it reads back");
      DefinitionJson.Hash(json).Length.ShouldBe(64);
   }

   [Fact]
   public void TheCanonicalFormHasCellsInOrderAndNumbersShortest()
   {
      DashboardDefinition given = Definitions.Sales() with
      {
         Layout = Definitions.Sales().Layout with
         {
            Items = new Dictionary<string, Placement> { ["title"] = new(0, 0, 12, 1), ["by-city"] = new(6, 1, 6, 6), ["by-status"] = new(0, 1, 6, 6) },
         },
         Filters =
         [
            Definitions.Sales().Filters[0] with
            {
               Value = new ConditionValue(ConditionOp.In, Values: [JsonSerializer.SerializeToElement(50.0), JsonSerializer.SerializeToElement(1.50m), "x"]),
            },
         ],
      };
      DashboardDefinition canonical = Check(given).Checked!;
      canonical.Layout.Items.Keys.ShouldBe(["by-city", "by-status", "title"]);
      string json = DefinitionJson.Write(canonical);
      json.ShouldContain("\"values\":[50,1.5,\"x\"]");
      DefinitionJson.Hash(json).ShouldBe(DefinitionJson.Hash(DefinitionJson.Write(Check(DefinitionJson.Read(json)).Checked!)), "the same once read back");
   }

   [Fact]
   public void MistakesAreNamedByTheirFields()
   {
      DashboardDefinition sales = Definitions.Sales();
      DashboardDefinition broken = sales with
      {
         Schema = 2,
         Sources = [sales.Sources[0], sales.Sources[1] with { Id = "Orders!" }, new DashboardSource("orders", "", "")],
         Filters = [sales.Filters[0] with { Visible = false, Kind = FilterKind.Range, Except = ["nowhere"] }],
         Widgets =
         [
            sales.Widgets[0],
            sales.Widgets[1] with { Config = Definitions.Bar("missing", "status") with { Measures = [], Limit = 0 } },
            new DashboardWidget("by-status", null, new TextConfig("again")),
         ],
         Refresh = new RefreshPolicy(RefreshMode.Interval, 5),
      };
      Fields(broken).ShouldBe(
      [
         "definition.filters[0].editable",
         "definition.filters[0].except[0]",
         "definition.filters[0].value.op",
         "definition.layout.items.by-city",
         "definition.links[0].to",
         "definition.refresh.seconds",
         "definition.schema",
         "definition.sources[1].id",
         "definition.sources[2].entity",
         "definition.sources[2].id",
         "definition.sources[2].label",
         "definition.widgets[1].config.limit",
         "definition.widgets[1].config.measures",
         "definition.widgets[1].config.sort.index",
         "definition.widgets[1].config.source",
         "definition.widgets[2].id",
      ]);
      Check(broken).Errors["definition.layout.items.by-city"].ShouldBe(["There is no widget by-city"]);
   }

   [Fact]
   public void LinksMakeAForest()
   {
      DashboardDefinition sales = Definitions.Sales();
      DashboardDefinition three = sales with { Sources = [.. sales.Sources, new DashboardSource("lines", "shop.order_lines", "Lines")] };
      Check(three with { Links = [.. sales.Links, new DashboardLink("lines", "orders", ["order"])] }).Errors.ShouldBeEmpty();
      Fields(three with { Links = [.. sales.Links, new DashboardLink("customers", "orders", ["orders"])] }).ShouldBe(["definition.links[1]"]);
      Fields(three with { Links = [.. sales.Links, new DashboardLink("lines", "orders", ["order"]), new DashboardLink("lines", "customers", ["order", "customer"])] })
         .ShouldBe(["definition.links[2]"], "a cycle through three");
      Fields(three with { Links = [new DashboardLink("orders", "orders", [])] }).ShouldBe(["definition.links[0].to"]);
      Fields(three with { Links = [new DashboardLink("orders", "customers", ["a", "b", "c", "d", "e", "f"])] }).ShouldBe(["definition.links[0].path"]);
   }

   [Fact]
   public void EachKindHasItsRules()
   {
      DashboardDefinition sales = Definitions.Sales();
      DashboardDefinition With(WidgetConfig config) => sales with { Widgets = [sales.Widgets[0], sales.Widgets[1] with { Config = config }, sales.Widgets[2]] };

      Fields(With(Definitions.Pie("orders", "status", Aggregate.Avg, "total", other: true))).ShouldBe(["definition.widgets[1].config.other"]);
      Check(With(Definitions.Pie("orders", "status", Aggregate.Sum, "total", other: true))).Errors.ShouldBeEmpty();
      Fields(With(Definitions.Bar("orders", "status", series: "customer_id") with
      {
         Measures = [new Measure(Aggregate.Count, null, "Rows", null), new Measure(Aggregate.Sum, null, "Total", null)],
      })).ShouldBe(["definition.widgets[1].config.measures", "definition.widgets[1].config.measures[1].field"]);
      Fields(With(Definitions.Bar("orders", "status") with { Sort = new WidgetSort(SortTarget.Measure, 3, true) })).ShouldBe(["definition.widgets[1].config.sort.index"]);
      Fields(With(Definitions.Bar("orders", "status") with { Listens = new Listens(ListenMode.Chosen, ["by-status", "nowhere"]) }))
         .ShouldBe(["definition.widgets[1].config.listens.widgets[0]", "definition.widgets[1].config.listens.widgets[1]"]);
      TableConfig raw = new("orders", [], true, Definitions.All, TableMode.Raw, [], [], [new TableColumn(new FieldRef(["customer"], "name"), "Customer", null)],
         new WidgetSort(SortTarget.Column, 0, false), 50);
      Check(With(raw)).Errors.ShouldBeEmpty();
      Fields(With(raw with { Dimensions = [new Dimension(new FieldRef([], "status"), null, "Status")], PageSize = 5000 }))
         .ShouldBe(["definition.widgets[1].config.dimensions", "definition.widgets[1].config.pageSize"]);
      Fields(With(new TextConfig(new string('x', 20_001)))).ShouldBe(["definition.widgets[1].config.markdown"]);
   }

   [Fact]
   public void ConditionsTakeTheValuesTheirOpsDo()
   {
      DashboardDefinition sales = Definitions.Sales();
      DashboardDefinition With(ConditionValue value, FilterKind kind = FilterKind.Values) => sales with { Filters = [sales.Filters[0] with { Kind = kind, Value = value }] };

      Check(With(new ConditionValue(ConditionOp.NotIn, Values: ["cancelled", null]))).Errors.ShouldBeEmpty();
      Fields(With(new ConditionValue(ConditionOp.In))).ShouldBe(["definition.filters[0].value.values"]);
      Fields(With(new ConditionValue(ConditionOp.In, Values: [JsonSerializer.SerializeToElement(new[] { 1 })]))).ShouldBe(["definition.filters[0].value.values[0]"]);
      Fields(With(new ConditionValue(ConditionOp.Between, Value: "2026-01-01"), FilterKind.Range)).ShouldBe(["definition.filters[0].value.valueTo"]);
      Check(With(new ConditionValue(ConditionOp.Relative, Relative: new RelativeRange(RelativeUnit.Month, 3, RelativeMode.Last)), FilterKind.Relative)).Errors.ShouldBeEmpty();
      Fields(With(new ConditionValue(ConditionOp.Relative, Relative: new RelativeRange(RelativeUnit.Month, 0, RelativeMode.Last)), FilterKind.Relative))
         .ShouldBe(["definition.filters[0].value.relative.count"]);
      Fields(sales with { Filters = [sales.Filters[0] with { Multiple = false }] }).ShouldBe(["definition.filters[0].value.values"]);
      Fields(With(new ConditionValue(ConditionOp.In, Values: [.. Enumerable.Repeat<object?>("x", 501)]))).ShouldBe(["definition.filters[0].value.values"]);
   }

   [Fact]
   public void LayoutsKeepToTheirColumns()
   {
      DashboardDefinition sales = Definitions.Sales();
      DashboardLayout layout = sales.Layout;
      Fields(sales with { Layout = layout with { Items = new(layout.Items) { ["by-city"] = new Placement(8, 1, 6, 6) } } }).ShouldBe(["definition.layout.items.by-city.x"]);
      Fields(sales with { Layout = layout with { Breakpoints = [layout.Breakpoints[1], layout.Breakpoints[0], layout.Breakpoints[2]] } })
         .ShouldBe(["definition.layout.breakpoints[0].minWidth", "definition.layout.breakpoints[1].minWidth"]);
      Fields(sales with { Layout = layout with { Breakpoints = [layout.Breakpoints[0], layout.Breakpoints[1], layout.Breakpoints[2] with { Columns = 25 }] } })
         .ShouldContain("definition.layout.breakpoints[2].columns");
      Dictionary<string, LayoutOverride> overrides = new()
      {
         ["medium"] = new LayoutOverride(6, new Dictionary<string, Placement> { ["title"] = new(0, 0, 6, 1), ["gone"] = new(0, 1, 6, 1) }, ["by-city"]),
         ["wide"] = new LayoutOverride(12, [], []),
      };
      Fields(sales with { Layout = layout with { Overrides = overrides } }).ShouldBe(["definition.layout.overrides.medium.items.gone", "definition.layout.overrides.wide"]);
   }

   [Fact]
   public void TheKindIsReadWhereverItIsAndUnknownKindsAreRefused()
   {
      string json = DefinitionJson.Write(Definitions.Sales());
      string moved = json.Replace("{\"kind\":\"text\",\"markdown\":\"# Sales\"}", "{\"markdown\":\"# Sales\",\"kind\":\"text\"}", System.StringComparison.Ordinal);
      moved.ShouldNotBe(json);
      DefinitionJson.Read(moved).Widgets[0].Config.ShouldBeOfType<TextConfig>().Markdown.ShouldBe("# Sales");
      Should.Throw<JsonException>(() => DefinitionJson.Read(json.Replace("\"kind\":\"text\"", "\"kind\":\"map\"", System.StringComparison.Ordinal)));
      Should.Throw<JsonException>(() => DefinitionJson.Read(json.Replace("\"orientation\":\"vertical\"", "\"orientation\":0", System.StringComparison.Ordinal)), "enums by name");
   }

   [Fact]
   public void TooLongADefinitionIsRefused()
   {
      DefinitionRules small = new(new WidgetKinds(WidgetKinds.BuiltIn()), new DashboardSettings { MaxDefinitionLength = 2000, MaxWidgets = 2 });
      DashboardDefinition sales = Definitions.Sales();
      Fields(sales, small).ShouldBe(["definition.widgets"], "three widgets, of two");
      DashboardDefinition two = sales with { Widgets = [sales.Widgets[0], sales.Widgets[1] with { Config = new TextConfig(new string('x', 1500)) }], Layout = sales.Layout with
      {
         Items = new Dictionary<string, Placement> { ["title"] = new(0, 0, 12, 1), ["by-status"] = new(0, 1, 6, 6) },
      } };
      Fields(two, small).ShouldBe(["definition"]);
   }

   [Fact]
   public void PalettesAreNamedByPositiveIdsAndBarsTakeTheirCategoriesColoursAlone()
   {
      DashboardDefinition sales = Definitions.Sales();
      BarConfig bar = (BarConfig)sales.Widgets[1].Config;
      PieConfig pie = (PieConfig)sales.Widgets[2].Config;
      DashboardDefinition With(int? palette, BarConfig barConfig, PieConfig pieConfig) => sales with
      {
         Palette = palette,
         Widgets = [sales.Widgets[0], sales.Widgets[1] with { Config = barConfig }, sales.Widgets[2] with { Config = pieConfig }],
      };
      Check(With(3, bar with { Palette = 4, ColorBy = BarColorBy.Category }, pie with { Palette = 999 })).Errors.ShouldBeEmpty("whether they are there is said when shown");
      Fields(With(0, bar with { Palette = -1 }, pie with { Palette = 0 })).ShouldBe(["definition.palette", "definition.widgets[1].config.palette", "definition.widgets[2].config.palette"]);
      Measure revenue = new(Aggregate.Sum, new FieldRef([], "total"), "Revenue", null);
      Fields(With(null, bar with { ColorBy = BarColorBy.Category, Measures = [.. bar.Measures, revenue] }, pie)).ShouldBe(["definition.widgets[1].config.colorBy"]);
      Fields(With(null, bar with { ColorBy = BarColorBy.Category, Series = bar.Dimension }, pie)).ShouldBe(["definition.widgets[1].config.colorBy"]);
   }
}
