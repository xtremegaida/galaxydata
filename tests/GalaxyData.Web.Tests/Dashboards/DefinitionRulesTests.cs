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
      Check(With(new HeadingConfig("", HeadingSize.Small, HeadingAlign.End))).Errors.ShouldBeEmpty("a heading still to be written");
      Fields(With(new HeadingConfig(new string('x', 201), HeadingSize.Large, HeadingAlign.Start))).ShouldBe(["definition.widgets[1].config.text"]);
      Fields(With(new HeadingConfig(null!, HeadingSize.Large, HeadingAlign.Start))).ShouldBe(["definition.widgets[1].config.text"]);
   }

   /// <summary>
   /// A measure's conditions, and what it is divided by, are checked as a widget's are; percentages and durations
   /// (in a unit, never shortened); labels for true and false. What isn't given isn't written.
   /// </summary>
   [Fact]
   public void MeasuresKeepRowsDivideAndShowAsAsked()
   {
      DashboardDefinition sales = Definitions.Sales();
      DashboardDefinition With(WidgetConfig config) => sales with { Widgets = [sales.Widgets[0], sales.Widgets[1] with { Config = config }, sales.Widgets[2]] };
      BarConfig bar = Definitions.Bar("orders", "status");
      WidgetCondition open = new(new FieldRef([], "status"), new ConditionValue(ConditionOp.Eq, Value: "open"));
      Measure share = new(Aggregate.Count, null, "Open", new NumberFormat(1, null, null, false, NumberStyle.Percent), [open], new MeasureTerm(Aggregate.Count, null, []));
      Measure waited = new(Aggregate.Avg, new FieldRef([], "wait_ms"), "Wait", new NumberFormat(null, null, null, false, NumberStyle.Duration, DurationUnit.Milliseconds));
      Dimension status = bar.Dimension with { Booleans = new BooleanLabels("Done", "Not yet") };

      (DashboardDefinition? fine, Dictionary<string, string[]> errors) = Check(With(bar with { Dimension = status, Measures = [share, waited, bar.Measures[0] with { Where = [] }] }));
      errors.ShouldBeEmpty();
      string json = DefinitionJson.Write(fine!);
      json.ShouldContain("\"where\":[{\"field\":{\"path\":[],\"column\":\"status\"},\"value\":{\"op\":\"eq\",\"value\":\"open\"");
      json.ShouldContain("\"per\":{\"aggregate\":\"count\",\"field\":null}", Case.Sensitive, "no conditions written as none");
      json.ShouldContain("\"format\":{\"decimals\":1,\"prefix\":null,\"suffix\":null,\"compact\":false,\"style\":\"percent\"}");
      json.ShouldContain("\"style\":\"duration\",\"unit\":\"milliseconds\"");
      json.ShouldContain("\"booleans\":{\"true\":\"Done\",\"false\":\"Not yet\"}");
      json.ShouldContain("{\"aggregate\":\"count\",\"field\":null,\"label\":\"Rows\",\"format\":null}", Case.Sensitive, "a measure as it was before");
      DefinitionJson.Write(Check(DefinitionJson.Read(json)).Checked!).ShouldBe(json, "the same once read back");

      Fields(With(bar with
      {
         Dimension = status with { Booleans = new BooleanLabels(null!, new string('x', 61)) },
         Measures =
         [
            share with { Where = [open with { Field = null! }], Per = new MeasureTerm(Aggregate.Sum, null, [open with { Value = new ConditionValue(ConditionOp.Between, Value: 1) }]) },
            waited with { Format = waited.Format! with { Unit = null, Compact = true } },
            share with { Format = share.Format! with { Unit = DurationUnit.Seconds }, Where = [.. Enumerable.Repeat(open, 21)] },
         ],
      })).ShouldBe(
      [
         "definition.widgets[1].config.dimension.booleans.false",
         "definition.widgets[1].config.dimension.booleans.true",
         "definition.widgets[1].config.measures[0].per.field",
         "definition.widgets[1].config.measures[0].per.where[0].value.valueTo",
         "definition.widgets[1].config.measures[0].where[0].field",
         "definition.widgets[1].config.measures[1].format.compact",
         "definition.widgets[1].config.measures[1].format.unit",
         "definition.widgets[1].config.measures[2].format.unit",
         "definition.widgets[1].config.measures[2].where",
      ]);
      TableConfig raw = new("orders", [], true, Definitions.All, TableMode.Raw, [], [], [new TableColumn(new FieldRef([], "paid"), "Paid", null, new BooleanLabels("Paid", ""))],
         new WidgetSort(SortTarget.Column, 0, false), 50);
      Check(With(raw)).Errors.ShouldBeEmpty("an empty label is no's");
      Fields(With(raw with { Columns = [raw.Columns[0] with { Booleans = new BooleanLabels("Paid", null!) }] }))
         .ShouldBe(["definition.widgets[1].config.columns[0].booleans.false"]);
      Fields(With(Definitions.Pie("orders", "status", Aggregate.Sum, "total", other: true) with { Measure = share with { Format = null } }))
         .ShouldBe(["definition.widgets[1].config.other"], "the rest of a ratio isn't a slice that adds up");
   }

   [Fact]
   public void AHeadingIsReadAsTheClientWritesIt()
   {
      DashboardDefinition sales = Definitions.Sales();
      string json = DefinitionJson.Write(sales with { Widgets = [sales.Widgets[0] with { Config = new HeadingConfig("Sales", HeadingSize.Medium, HeadingAlign.Center) }] });
      json.ShouldContain("{\"kind\":\"heading\",\"text\":\"Sales\",\"size\":\"medium\",\"align\":\"center\"}");
      DefinitionJson.Read(json.Replace("\"size\":\"medium\"", "\"size\":\"large\"", System.StringComparison.Ordinal))
         .Widgets[0].Config.ShouldBe(new HeadingConfig("Sales", HeadingSize.Large, HeadingAlign.Center));
      Should.Throw<JsonException>(() => DefinitionJson.Read(json.Replace("\"size\":\"medium\"", "\"size\":\"huge\"", System.StringComparison.Ordinal)));
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

   [Fact]
   public void TabsHaveIdsAndLabelsAndEachWidgetIsOnOne()
   {
      DashboardDefinition sales = Definitions.Sales();
      DashboardDefinition Tabbed(List<DashboardTab>? tabs, params string?[] on) => sales with
      {
         Tabs = tabs,
         Widgets = [.. sales.Widgets.Select((w, i) => w with { Tab = on[i] })],
      };
      List<DashboardTab> two = [new("orders", "Orders"), new("customers", "Customers")];

      (DashboardDefinition? tabbed, Dictionary<string, string[]> errors) = Check(Tabbed(two, "orders", "orders", "customers"));
      errors.ShouldBeEmpty();
      string json = DefinitionJson.Write(tabbed!);
      json.ShouldContain("\"tabs\":[{\"id\":\"orders\",\"label\":\"Orders\"},{\"id\":\"customers\",\"label\":\"Customers\"}]");
      json.ShouldContain("\"tab\":\"customers\"");
      DefinitionJson.Write(DefinitionJson.Read(json)).ShouldBe(json, "written as it reads back");
      Check(Tabbed([new("only", "Only")], "only", "only", "only")).Errors.ShouldBeEmpty("one tab, and tabs with no widgets, are allowed");

      Fields(Tabbed(two, "orders", null, "nowhere")).ShouldBe(["definition.widgets[1].tab", "definition.widgets[2].tab"]);
      Check(Tabbed(two, "orders", null, "nowhere")).Errors["definition.widgets[2].tab"].ShouldBe(["There is no tab nowhere"]);
      Fields(Tabbed(null, "orders", null, null)).ShouldBe(["definition.widgets[0].tab"]);
      Check(Tabbed(null, "orders", null, null)).Errors["definition.widgets[0].tab"].ShouldBe(["The dashboard has no tabs"]);
      Fields(Tabbed([new("Orders!", " "), new("orders", "Orders"), new("orders", new string('x', 61)), null!], "orders", "orders", "orders"))
         .ShouldBe(["definition.tabs[0].id", "definition.tabs[0].label", "definition.tabs[2].id", "definition.tabs[2].label", "definition.tabs[3]"]);
      DefinitionRules few = new(new WidgetKinds(WidgetKinds.BuiltIn()), new DashboardSettings { MaxTabs = 1 });
      Fields(Tabbed(two, "orders", "orders", "orders"), few).ShouldBe(["definition.tabs"]);
   }

   [Fact]
   public void ASeriesIsLimitedOnlyWhenThereIsOneAndItsRestIsOtherOnlyWhenLimited()
   {
      DashboardDefinition sales = Definitions.Sales();
      DashboardDefinition With(WidgetConfig config) => sales with { Widgets = [sales.Widgets[0], sales.Widgets[1] with { Config = config }, sales.Widgets[2]] };
      BarConfig bars = Definitions.Bar("orders", "status", series: "customer_id");
      LineConfig line = new("orders", [], true, Definitions.All, new Dimension(new FieldRef([], "order_date"), Bucket.Month, "Month"),
         new Dimension(new FieldRef([], "status"), null, "Status"), [new Measure(Aggregate.Count, null, "Rows", null)], 1000, false, LineGaps.Break, false,
         LegendPosition.Bottom, null, null);

      Check(With(bars with { SeriesLimit = 8, SeriesOther = true })).Errors.ShouldBeEmpty();
      Check(With(line with { SeriesLimit = 100, SeriesOther = true })).Errors.ShouldBeEmpty();
      Check(With(bars with { SeriesLimit = 8 })).Errors.ShouldBeEmpty("the largest, and no Other");
      Fields(With(bars with { Series = null, SeriesLimit = 8 })).ShouldBe(["definition.widgets[1].config.seriesLimit"]);
      Fields(With(bars with { SeriesLimit = 0 })).ShouldBe(["definition.widgets[1].config.seriesLimit"]);
      Fields(With(line with { SeriesLimit = 101 })).ShouldBe(["definition.widgets[1].config.seriesLimit"]);
      Fields(With(line with { SeriesOther = true })).ShouldBe(["definition.widgets[1].config.seriesOther"]);
      Fields(With(bars with { Series = null, SeriesOther = true })).ShouldBe(["definition.widgets[1].config.seriesOther"]);

      // Unset, they aren't written: a chart saved before them is written as it was.
      string json = DefinitionJson.Write(Check(With(bars)).Checked!);
      json.ShouldNotContain("\"seriesLimit\"");
      json.ShouldNotContain("\"seriesOther\"");
      DefinitionJson.Write(Check(With(bars with { SeriesLimit = 8, SeriesOther = true })).Checked!).ShouldContain("\"seriesLimit\":8,\"seriesOther\":true");
   }

   [Fact]
   public void AKpiIsComparedThroughARelativeFilterAndItsSparklineIsByAPeriod()
   {
      DashboardDefinition sales = Definitions.Sales();
      DashboardFilter period = new("period", "Period", new SourceField("orders", [], "order_date"), FilterKind.Relative,
         new ConditionValue(ConditionOp.Relative, Relative: new RelativeRange(RelativeUnit.Day, 30, RelativeMode.Last)), true, true, false, []);
      KpiConfig kpi = new("orders", [], false, Definitions.All, new Measure(Aggregate.Sum, new FieldRef([], "total"), "Revenue", new NumberFormat(0, "R ", null, true)),
         new KpiCompare("period", KpiGood.Down), new Dimension(new FieldRef([], "order_date"), Bucket.Day, "Day"));
      DashboardDefinition With(KpiConfig config) => sales with
      {
         Filters = [.. sales.Filters, period],
         Widgets = [sales.Widgets[0], sales.Widgets[1] with { Config = config }, sales.Widgets[2]],
      };

      (DashboardDefinition? checkedKpi, Dictionary<string, string[]> errors) = Check(With(kpi));
      errors.ShouldBeEmpty();
      string json = DefinitionJson.Write(checkedKpi!);
      json.ShouldContain("{\"kind\":\"kpi\",\"measure\":{\"aggregate\":\"sum\"");
      json.ShouldContain("\"compare\":{\"filter\":\"period\",\"good\":\"down\"},\"sparkline\":{\"field\":{\"path\":[],\"column\":\"order_date\"},\"bucket\":\"day\"");
      DefinitionJson.Write(DefinitionJson.Read(json)).ShouldBe(json, "written as it reads back");
      DefinitionJson.Read(json).Widgets[1].Config.ShouldBeOfType<KpiConfig>().Compare.ShouldBe(new KpiCompare("period", KpiGood.Down));
      Check(With(kpi with { Compare = null, Sparkline = null })).Errors.ShouldBeEmpty("a number alone");
      DefinitionJson.Write(Check(With(kpi with { Compare = null, Sparkline = null })).Checked!).ShouldContain("\"compare\":null,\"sparkline\":null");

      Fields(With(kpi with { Emits = true })).ShouldBe(["definition.widgets[1].config.emits"]);
      Fields(With(kpi with { Measure = new Measure(Aggregate.Sum, null, "", null) }))
         .ShouldBe(["definition.widgets[1].config.measure.field", "definition.widgets[1].config.measure.label"]);
      Fields(With(kpi with { Compare = new KpiCompare("nowhere", KpiGood.Up) })).ShouldBe(["definition.widgets[1].config.compare.filter"]);
      Check(With(kpi with { Compare = new KpiCompare("status", KpiGood.Up) })).Errors["definition.widgets[1].config.compare.filter"]
         .ShouldBe(["Compare with a period relative to today: a relative filter's"], "a filter of values has no period before");
      Fields(With(kpi with { Sparkline = new Dimension(new FieldRef([], "order_date"), Bucket.DayOfWeek, "Weekday") })).ShouldBe(["definition.widgets[1].config.sparkline.bucket"]);
      Fields(With(kpi with { Sparkline = new Dimension(new FieldRef([], "order_date"), null, "") }))
         .ShouldBe(["definition.widgets[1].config.sparkline.bucket", "definition.widgets[1].config.sparkline.label"]);
      Should.Throw<JsonException>(() => DefinitionJson.Read(json.Replace("\"good\":\"down\"", "\"good\":\"sideways\"", System.StringComparison.Ordinal)));
   }

   [Fact]
   public void ADashboardWithoutTabsIsWrittenAsItWas()
   {
      DashboardDefinition sales = Check(Definitions.Sales()).Checked!;
      string json = DefinitionJson.Write(sales);
      json.ShouldNotContain("\"tabs\"");
      json.ShouldNotContain("\"tab\"");
      DashboardDefinition none = Check(Definitions.Sales() with { Tabs = [] }).Checked!;
      none.Tabs.ShouldBeNull("no tabs are written as none");
      DefinitionJson.Hash(DefinitionJson.Write(none)).ShouldBe(DefinitionJson.Hash(json));
      Fields(Definitions.Sales() with { Tabs = [], Widgets = [.. Definitions.Sales().Widgets.Select(w => w with { Tab = "orders" })] })
         .ShouldBe(["definition.widgets[0].tab", "definition.widgets[1].tab", "definition.widgets[2].tab"]);
   }
}
