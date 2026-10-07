using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Results;
using GalaxyData.Web.Browse;
using GalaxyData.Web.Catalog;
using GalaxyData.Web.Dashboards;
using GalaxyData.Web.Tests.Catalog;
using GalaxyData.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Dashboards;

/// <summary>Widgets' queries over the shop fixture, as written and as they run: a report of each case, its texts, parameters and rows.</summary>
public sealed class WidgetQueryTests
{
   private static readonly DateOnly Today = new(2026, 3, 1);

   private static readonly PlanLimits Signed = new(MaxSelectionKeys: 50, MaxFilterValues: 500, MaxChartRows: 10_000, Public: false);

   private static readonly WidgetKinds Kinds = new(WidgetKinds.BuiltIn());

   private static Listens All => Definitions.All;

   private static FieldRef F(string column, params string[] path) => new([.. path], column);

   private static Dimension D(string column, Bucket? bucket = null, params string[] path) => new(F(column, path), bucket, column);

   private static Measure Count() => new(Aggregate.Count, null, "Rows", null);

   private static Measure Of(Aggregate aggregate, string column, params string[] path) => new(aggregate, F(column, path), column, null);

   private static BarConfig Bar(string source, Dimension dimension, Dimension? series = null, int limit = 25, params Measure[] measures) =>
      new(source, [], true, All, dimension, series, measures.Length == 0 ? [Count()] : [.. measures], new WidgetSort(SortTarget.Measure, 0, true), limit,
         BarOrientation.Vertical, BarStack.None, false, LegendPosition.Bottom, null, null);

   private static LineConfig Line(string source, Dimension x, params Measure[] measures) =>
      new(source, [], true, All, x, null, measures.Length == 0 ? [Count()] : [.. measures], 1000, false, LineGaps.Break, false, LegendPosition.Bottom, null, null);

   private static PieConfig Pie(string source, Dimension dimension, Measure? measure = null, bool other = false, int limit = 10) =>
      new(source, [], true, All, dimension, measure ?? Count(), limit, other, false, true, LegendPosition.Right);

   private static DashboardDefinition Dashboard(IEnumerable<DashboardWidget> widgets, IEnumerable<DashboardFilter>? filters = null, IEnumerable<DashboardSource>? sources = null,
                                                IEnumerable<DashboardLink>? links = null)
   {
      List<DashboardWidget> list = [.. widgets];
      return Definitions.Blank() with
      {
         Sources = sources == null
            ? [new DashboardSource("orders", "shop.orders", "Orders"), new DashboardSource("customers", "shop.customers", "Customers"), new DashboardSource("lines", "shop.order_lines", "Lines")]
            : [.. sources],
         Links = links == null ? [new DashboardLink("orders", "customers", ["customer"]), new DashboardLink("lines", "orders", ["order"])] : [.. links],
         Filters = filters == null ? [] : [.. filters],
         Widgets = list,
         Layout = DashboardLayout.Default() with { Items = list.Select((w, i) => (w.Id, new Placement(0, i, 12, 1))).ToDictionary(p => p.Id, p => p.Item2) },
      };
   }

   private static DashboardWidget W(string id, WidgetConfig config) => new(id, null, config);

   private static DashboardState Choose(string widget, SelectionMode mode, params object?[][] keys) =>
      new(null, new Dictionary<string, SelectionState> { [widget] = new SelectionState(mode, [.. keys.Select(k => k.ToList())]) });

   private sealed class Harness(WebAppFactory factory, TestApi admin) : IAsyncDisposable
   {
      public TestApi Admin => admin;

      public static async Task<Harness> CreateAsync()
      {
         WebAppFactory factory = new();
         TestApi admin = await TestApi.SignedInAsync(factory);
         await TestSources.AddSqliteAsync(admin, "shop", await TestSources.ShopAsync(factory));
         return new Harness(factory, admin);
      }

      public async Task AddSecondAsync() => await TestSources.AddSqliteAsync(admin, "two", await TestSources.ShopAsync(factory, "two.db"));

      public async Task<CatalogState> StateAsync() => await factory.Services.GetRequiredService<CatalogService>().GetAsync(TestContext.Current.CancellationToken);

      public async Task<(WidgetPlan? Plan, IReadOnlyList<DashboardIssue> Issues, Dictionary<string, string[]> Errors)> PlanAsync(DashboardDefinition definition, string widget,
         DashboardState? state = null, PlanLimits? limits = null)
      {
         Dictionary<string, string[]> structure = [];
         DefinitionRules rules = factory.Services.GetRequiredService<DefinitionRules>();
         DashboardDefinition? checkedDefinition = rules.Check(definition, "definition.", structure);
         structure.ShouldBeEmpty();
         DashboardCatalog resolved = DashboardCatalog.Resolve(checkedDefinition!, (await StateAsync()).Catalog);
         Dictionary<string, string[]> errors = [];
         WidgetPlan? plan = new WidgetPlanner(resolved, state ?? DashboardState.Empty, limits ?? Signed, Today, errors).Plan(widget, Kinds);
         return (plan, resolved.Issues, errors);
      }

      public async Task<(BuiltQuery? Query, IReadOnlyList<DashboardIssue> Issues)> FilterValuesAsync(DashboardDefinition definition, string filter, string? search, DashboardState? state = null)
      {
         DashboardCatalog resolved = DashboardCatalog.Resolve(definition, (await StateAsync()).Catalog);
         Dictionary<string, string[]> errors = [];
         BuiltQuery? query = new WidgetPlanner(resolved, state ?? DashboardState.Empty, Signed, Today, errors).FilterValues(filter, search, 100);
         errors.ShouldBeEmpty();
         return (query, resolved.Issues);
      }

      public async Task<string> RowsAsync(BuiltQuery query)
      {
         CatalogState state = await StateAsync();
         QueryEngine engine = factory.Services.GetRequiredService<QueryEngines>().For(state);
         PreparedQuery prepared = engine.Prepare(new QueryRequest(query.Text) { Parameters = query.Parameters });
         prepared.Success.ShouldBeTrue(string.Join("; ", prepared.Diagnostics.Select(d => d.Message)) + "\n" + query.Text);
         await using QueryResult result = await prepared.ExecuteAsync(TestContext.Current.CancellationToken);
         IReadOnlyList<object?[]> rows = await result.ToListAsync(TestContext.Current.CancellationToken);
         int visible = result.Schema.VisibleColumns.Count;
         StringBuilder text = new();
         text.AppendLine(string.Join(" | ", result.Schema.VisibleColumns.Select(c => c.Name)));
         List<ResultColumn> columns = [.. result.Schema.VisibleColumns];
         foreach (object?[] row in rows) { text.AppendLine(string.Join(" | ", row.Take(visible).Select((v, i) => Value(v, columns[i].Type)))); }
         return text.ToString();
      }

      public async ValueTask DisposeAsync() => await factory.DisposeAsync();
   }

   private static string Parameters(QueryParameters parameters) =>
      string.Join(", ", parameters.All.Select(p => $"${p.Name} = {Value(p.Value, p.Type)} ({p.Type})"));

   private static string Value(object? value, GalaxyData.Query.Types.ScalarType type) => value == null ? "null" : ValueCodec.Json(ValueCodec.Encode(value, type)).GetRawText();

   private sealed class Report
   {
      private readonly StringBuilder text = new();

      public async Task CaseAsync(Harness harness, string title, DashboardDefinition definition, string widget, DashboardState? state = null, CategoryValues? categories = null)
      {
         (WidgetPlan? plan, IReadOnlyList<DashboardIssue> issues, Dictionary<string, string[]> errors) = await harness.PlanAsync(definition, widget, state);
         text.AppendLine("### " + title);
         foreach (DashboardIssue issue in issues) { text.AppendLine($"issue {issue.Severity} ({issue.Widget}, {issue.Field}): {issue.Message}"); }
         foreach ((string field, string[] messages) in errors) { text.AppendLine($"error {field}: {string.Join("; ", messages)}"); }
         if (plan == null)
         {
            text.AppendLine("(no plan)").AppendLine();
            return;
         }
         text.AppendLine("columns: " + string.Join(", ", plan.Columns.Select(c => $"{c.Name} {c.Role} {c.Label}")) + $"; limit {plan.Limit}{(plan.Paged ? ", paged" : string.Empty)}");
         if (plan.Categories is { } first)
         {
            text.AppendLine("categories: " + first.Text);
            if (first.Parameters.All.Any()) { text.AppendLine("  " + Parameters(first.Parameters)); }
            text.Append(await harness.RowsAsync(first));
         }
         BuiltQuery main = plan.Main(categories);
         text.AppendLine("main: " + main.Text);
         if (main.Parameters.All.Any()) { text.AppendLine("  " + Parameters(main.Parameters)); }
         text.Append(await harness.RowsAsync(main));
         if (plan.Total is { } total)
         {
            text.AppendLine("total: " + total.Text);
            text.Append(await harness.RowsAsync(total));
         }
         if (plan.Underlying.Text != main.Text) { text.AppendLine("underlying: " + plan.Underlying.Text); }
         text.AppendLine();
      }

      public void Line(string line) => text.AppendLine(line);

      public override string ToString() => text.ToString();
   }

   [Fact]
   public async Task WidgetsQueryTheirRowsAsLinkedWidgetsAndFiltersSay()
   {
      await using Harness harness = await Harness.CreateAsync();
      Report report = new();
      DashboardWidget byStatus = W("by-status", Bar("orders", D("status")));
      DashboardWidget byCity = W("by-city", Pie("customers", D("city")));
      DashboardWidget byProduct = W("by-product", Bar("lines", D("product_code"), measures: [Of(Aggregate.Sum, "qty")]));
      DashboardDefinition three = Dashboard([byStatus, byCity, byProduct]);

      await report.CaseAsync(harness, "A bar chart, counting rows", three, "by-status");
      DashboardFilter status = new("status", "Status", new SourceField("orders", [], "status"), FilterKind.Values, new ConditionValue(ConditionOp.In, Values: ["open", "shipped"]),
         true, true, true, []);
      await report.CaseAsync(harness, "A filter on orders reaches customers (some order kept) and lines (their order kept)", Dashboard([byStatus, byCity, byProduct], [status]), "by-city");
      await report.CaseAsync(harness, "... and lines", Dashboard([byStatus, byCity, byProduct], [status]), "by-product");
      await report.CaseAsync(harness, "A viewer's value of an editable filter: not cancelled, nor null", Dashboard([byStatus], [status]), "by-status",
         new DashboardState(new Dictionary<string, ConditionValue?> { ["status"] = new ConditionValue(ConditionOp.NotIn, Values: ["cancelled"]) }));
      await report.CaseAsync(harness, "Excepted from the filter", Dashboard([byStatus], [status with { Except = ["by-status"] }]), "by-status");
      await report.CaseAsync(harness, "Choosing open orders: customers with an open order", three, "by-city", Choose("by-status", SelectionMode.Include, ["open"]));
      await report.CaseAsync(harness, "Choosing open orders: never the emitter's own rows", three, "by-status", Choose("by-status", SelectionMode.Include, ["open"]));
      await report.CaseAsync(harness, "Choosing two statuses: one list", three, "by-product", Choose("by-status", SelectionMode.Include, ["open"], ["cancelled"]));
      await report.CaseAsync(harness, "Excluding open orders: customers with an order that isn't open", three, "by-city", Choose("by-status", SelectionMode.Exclude, ["open"]));
      await report.CaseAsync(harness, "Choosing Cape Town: orders of its customers (to one row, which every order has)", three, "by-status",
         Choose("by-city", SelectionMode.Include, ["Cape Town"]));
      await report.CaseAsync(harness, "Choosing no city (null): orders of customers without one", three, "by-status", Choose("by-city", SelectionMode.Include, new object?[] { null }));
      await report.CaseAsync(harness, "Excluding no city: customers with one, their orders", three, "by-status", Choose("by-city", SelectionMode.Exclude, new object?[] { null }));
      await report.CaseAsync(harness, "Choosing a product: customers with an order with a line of it (two hops of many)", three, "by-city",
         Choose("by-product", SelectionMode.Include, ["P-100"]));

      DashboardWidget big = W("big", Bar("orders", D("status")) with { Conditions = [new WidgetCondition(F("total"), new ConditionValue(ConditionOp.Gt, Value: 50))] });
      await report.CaseAsync(harness, "Its own condition, and a choice on the same source: one predicate", Dashboard([byStatus, big]), "big",
         Choose("by-status", SelectionMode.Include, ["open"]));

      DashboardWidget listener = W("listener", Bar("orders", D("status")) with { Listens = new Listens(ListenMode.Chosen, ["by-city"]) });
      await report.CaseAsync(harness, "Listening to chosen widgets only", Dashboard([byStatus, byCity, listener]), "listener",
         new DashboardState(null, new Dictionary<string, SelectionState>
         {
            ["by-status"] = new(SelectionMode.Include, [["open"]]),
            ["by-city"] = new(SelectionMode.Include, [["Johannesburg"]]),
         }));
      await report.CaseAsync(harness, "Listening to none", Dashboard([byCity, listener with { Config = ((BarConfig)listener.Config) with { Listens = new Listens(ListenMode.None, []) } }]),
         "listener", Choose("by-city", SelectionMode.Include, ["Johannesburg"]));

      Golden.Match(report.ToString());
   }

   [Fact]
   public async Task KindsGroupMeasureAndBucketTheirRows()
   {
      await using Harness harness = await Harness.CreateAsync();
      Report report = new();
      DashboardWidget months = W("months", Line("orders", D("order_date", Bucket.Month), Count(), Of(Aggregate.Sum, "total")));
      DashboardWidget weekdays = W("weekdays", Bar("orders", D("order_date", Bucket.DayOfWeek)) with { Sort = new WidgetSort(SortTarget.Dimension, 0, false) });
      DashboardWidget hours = W("hours", Bar("orders", D("placed_at", Bucket.HourOfDay)) with { Sort = new WidgetSort(SortTarget.Dimension, 0, false) });
      DashboardWidget cities = W("cities", Bar("orders", D("city", null, "customer"), measures: [Of(Aggregate.Avg, "total"), Of(Aggregate.CountValues, "ship_address_id")]));
      DashboardWidget pie = W("pie", Pie("orders", D("status"), Of(Aggregate.Sum, "total"), other: true, limit: 2));
      DashboardWidget series = W("series", Bar("orders", D("status"), D("customer_id"), limit: 1));
      DashboardWidget grouped = W("grouped", new TableConfig("orders", [], true, All, TableMode.Grouped, [D("city", null, "customer"), D("status")],
         [Count(), Of(Aggregate.Max, "order_date")], [], new WidgetSort(SortTarget.Measure, 0, true), 50));
      DashboardWidget raw = W("raw", new TableConfig("orders", [], true, All, TableMode.Raw, [], [],
         [new TableColumn(F("total"), "Total", null), new TableColumn(F("name", "customer"), "Customer", null)], new WidgetSort(SortTarget.Column, 0, true), 50));
      DashboardDefinition definition = Dashboard([months, weekdays, hours, cities, pie, series, grouped, raw]);

      await report.CaseAsync(harness, "A line by month, counting and summing", definition, "months");
      await report.CaseAsync(harness, "Bars by day of the week, in its order", definition, "weekdays");
      await report.CaseAsync(harness, "Bars by the hour (UTC) of a date-time with an offset", definition, "hours");
      await report.CaseAsync(harness, "Through a navigation: averages (counted once per order) and values counted", definition, "cities");
      await report.CaseAsync(harness, "A pie of the largest two, and the total for the rest", definition, "pie");
      await report.CaseAsync(harness, "Bars by a series: the first category found first", definition, "series", null,
         new CategoryValues(["open"], GalaxyData.Query.Types.ScalarType.Text()));
      await report.CaseAsync(harness, "... with null among the categories", definition, "series", null, new CategoryValues(["open", null], GalaxyData.Query.Types.ScalarType.Text()));
      await report.CaseAsync(harness, "A grouped table", definition, "grouped");
      await report.CaseAsync(harness, "A raw table, its key for choosing rows", definition, "raw");
      await report.CaseAsync(harness, "Choosing a month: its days, as a range of the column", definition, "weekdays", Choose("months", SelectionMode.Include, ["2026-01-01"]));
      await report.CaseAsync(harness, "Excluding a month", definition, "weekdays", Choose("months", SelectionMode.Exclude, ["2026-01-01"]));
      await report.CaseAsync(harness, "Choosing a day of the week", definition, "months", Choose("weekdays", SelectionMode.Include, [1], [7]));
      await report.CaseAsync(harness, "Choosing a bar of a series: both its parts", definition, "months", Choose("series", SelectionMode.Include, ["open", 1]));
      await report.CaseAsync(harness, "Choosing raw rows by their key", definition, "months", Choose("raw", SelectionMode.Include, [1001], [1003]));
      await report.CaseAsync(harness, "Choosing grouped rows", definition, "months", Choose("grouped", SelectionMode.Include, ["Cape Town", "open"], [null, "cancelled"]));

      DashboardFilter recent = new("recent", "Recent", new SourceField("orders", [], "order_date"), FilterKind.Relative,
         new ConditionValue(ConditionOp.Relative, Relative: new RelativeRange(RelativeUnit.Month, 2, RelativeMode.Last)), true, false, false, []);
      DashboardFilter placed = new("placed", "Placed", new SourceField("orders", [], "placed_at"), FilterKind.Range,
         new ConditionValue(ConditionOp.Between, Value: "2026-01-05", ValueTo: "2026-01-05"), false, false, false, []);
      DashboardFilter who = new("who", "Who", new SourceField("orders", ["customer"], "name"), FilterKind.Text, new ConditionValue(ConditionOp.Contains, Value: "ac"),
         true, true, false, []);
      await report.CaseAsync(harness, "The last two months (to 2026-03-01): from 2026-01-02", Dashboard([months], [recent]), "months");
      await report.CaseAsync(harness, "A hidden range of a date-time with an offset, by its UTC days", Dashboard([months], [placed]), "months");
      await report.CaseAsync(harness, "Text through a navigation, ignoring case", Dashboard([months], [who]), "months");

      Golden.Match(report.ToString());
   }

   [Fact]
   public async Task WhatDoesntFitTheCatalogIsAnIssue()
   {
      await using Harness harness = await Harness.CreateAsync();
      Report report = new();
      DashboardDefinition definition = Dashboard(
      [
         W("missing", Bar("orders", D("colour"))),
         W("summed", Bar("orders", D("status"), measures: [Of(Aggregate.Sum, "status")])),
         W("instants", Bar("orders", D("placed_at"))),
         W("hours", Bar("orders", D("order_date", Bucket.HourOfDay))),
         W("many", Bar("customers", D("status", null, "orders"))),
         W("nowhere", Bar("ghosts", D("x"))),
         W("deaf", Bar("orders", D("status")) with { Listens = new Listens(ListenMode.Chosen, ["elsewhere"]) }),
         W("elsewhere", Bar("employees", D("name"))),
      ], sources:
      [
         new DashboardSource("orders", "shop.orders", "Orders"), new DashboardSource("customers", "shop.customers", "Customers"),
         new DashboardSource("ghosts", "shop.ghosts", "Ghosts"), new DashboardSource("employees", "shop.employees", "Employees"),
      ], links: [new DashboardLink("orders", "customers", ["customer"])]);
      foreach (string widget in (string[])["missing", "summed", "instants", "hours", "many", "nowhere", "deaf"]) { await report.CaseAsync(harness, widget, definition, widget); }

      DashboardDefinition badLinks = Dashboard([W("by-status", Bar("orders", D("status")))], links:
      [
         new DashboardLink("orders", "customers", ["shipper"]),
         new DashboardLink("lines", "customers", ["order"]),
      ]);
      await report.CaseAsync(harness, "Links that don't lead where they say", badLinks, "by-status");

      // What a viewer sends that doesn't fit: refused by field.
      DashboardDefinition three = Dashboard([W("by-status", Bar("orders", D("status"))), W("months", Line("orders", D("order_date", Bucket.Month)))]);
      await report.CaseAsync(harness, "A key of the wrong size, a value that isn't the field's, a month not on its first day", three, "by-status",
         new DashboardState(null, new Dictionary<string, SelectionState> { ["months"] = new(SelectionMode.Include, [["2026-01-05"], ["soon"], ["2026-01-01", "x"]]) }));
      (_, _, Dictionary<string, string[]> errors) = await harness.PlanAsync(three, "by-status", Choose("months", SelectionMode.Include, [.. Enumerable.Range(1, 51).Select(_ => new object?[] { "2026-01-01" })]));
      errors.Keys.ShouldBe(["state.selections.months.keys"]);

      Golden.Match(report.ToString());
   }

   [Fact]
   public async Task FiltersOfferTheirValuesUnderTheOtherConditions()
   {
      await using Harness harness = await Harness.CreateAsync();
      DashboardFilter status = new("status", "Status", new SourceField("orders", [], "status"), FilterKind.Values, null, true, true, true, []);
      DashboardFilter city = new("city", "City", new SourceField("customers", [], "city"), FilterKind.Values, new ConditionValue(ConditionOp.In, Values: ["Cape Town"]), false, false, true, []);
      DashboardDefinition definition = Dashboard([W("by-status", Bar("orders", D("status")))], [status, city]);
      (BuiltQuery? query, IReadOnlyList<DashboardIssue> issues) = await harness.FilterValuesAsync(definition, "status", null);
      issues.ShouldBeEmpty();
      query!.Text.ShouldBe("(shop.orders).where(r0 => (r0.customer != null and (r0.customer.city in [$f1]))).groupBy(v: status).select(v, n: count()).orderBy(desc(n), v).take(101)",
         "the hidden filter on customers applies");
      (await harness.RowsAsync(query)).ShouldBe(Lines("v | n", "\"open\" | \"1\"", "\"shipped\" | \"1\""));
      (BuiltQuery? searched, _) = await harness.FilterValuesAsync(definition, "status", "SHIP");
      (await harness.RowsAsync(searched!)).ShouldBe(Lines("v | n", "\"shipped\" | \"1\""));
      DashboardFilter total = status with { Id = "total", Field = new SourceField("orders", [], "total") };
      (BuiltQuery? numbers, _) = await harness.FilterValuesAsync(Dashboard([W("by-status", Bar("orders", D("status")))], [total]), "total", "12.25");
      (await harness.RowsAsync(numbers!)).ShouldBe(Lines("v | n", "\"12.25\" | \"1\""));
      (BuiltQuery? words, _) = await harness.FilterValuesAsync(Dashboard([W("by-status", Bar("orders", D("status")))], [total]), "total", "twelve");
      (await harness.RowsAsync(words!)).ShouldBe(Lines("v | n"));
   }

   [Fact]
   public async Task LinksReachAcrossSourcesAndIntoVirtualEntities()
   {
      await using Harness harness = await Harness.CreateAsync();
      await harness.AddSecondAsync();
      (await harness.Admin.PostAsync("/api/overlay/relations", new { from = "two.orders", fromColumns = new[] { "customer_id" }, to = "shop.customers", toColumns = new[] { "id" }, inverseName = "two_orders" }))
         .StatusCode.ShouldBe(HttpStatusCode.Created);
      (await harness.Admin.PostAsync("/api/overlay/virtual-entities", new { name = "reports.big_orders", query = "shop.orders.where(total > 50)" })).StatusCode.ShouldBe(HttpStatusCode.Created);

      JsonElement paths = await (await harness.Admin.GetAsync("/api/catalog/paths?from=two.orders&to=shop.customers")).JsonAsync(HttpStatusCode.OK);
      List<string> found = [.. paths.EnumerateArray().Select(p => string.Join(" > ", p.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("navigation").GetString())))];
      found.ShouldContain("shop_customer", "the overlay's navigation, named apart from two's own customer");
      JsonElement intoVirtual = await (await harness.Admin.GetAsync("/api/catalog/paths?from=shop.customers&to=reports.big_orders&depth=1")).JsonAsync(HttpStatusCode.OK);
      intoVirtual.EnumerateArray().Select(p => p.GetProperty("steps")[0].GetProperty("navigation").GetString()).ShouldBe(["orders"], "to the base it filters");
      await (await harness.Admin.GetAsync("/api/catalog/paths?from=shop.nothing&to=shop.customers")).ProblemAsync(404, GalaxyData.Web.Problems.ProblemCodes.NotFound);

      Report report = new();
      List<DashboardSource> sources =
      [
         new("customers", "shop.customers", "Customers"), new("two", "two.orders", "Their orders"), new("big", "reports.big_orders", "Big orders"),
      ];
      List<DashboardLink> links = [new("two", "customers", ["shop_customer"]), new("customers", "big", ["orders"])];
      DashboardDefinition definition = Dashboard(
      [
         W("two-status", Bar("two", D("status"))), W("cities", Pie("customers", D("city"))), W("big-status", Bar("big", D("status"))),
      ], sources: sources, links: links);
      await report.CaseAsync(harness, "Choosing a city of shop's customers: two's orders of them (across sources)", definition, "two-status", Choose("cities", SelectionMode.Include, ["Johannesburg"]));
      await report.CaseAsync(harness, "Choosing two's open orders: shop's customers with one", definition, "cities", Choose("two-status", SelectionMode.Include, ["open"]));
      await report.CaseAsync(harness, "Choosing big orders: the customers with one (back from a virtual entity's base)", definition, "cities", Choose("big-status", SelectionMode.Include, ["shipped"]));
      await report.CaseAsync(harness, "Choosing a city: big orders of its customers (into the virtual entity, through its inherited navigation)", definition, "big-status",
         Choose("cities", SelectionMode.Include, ["Cape Town"]));
      await report.CaseAsync(harness, "Two hops across: two's open orders, to big orders of the same customers", definition, "big-status", Choose("two-status", SelectionMode.Include, ["open"]));
      Golden.Match(report.ToString());
   }

   /// <summary>Many slices of two parts nest as a balanced OR, within the parser's depth; two sources of one entity share their conditions.</summary>
   [Fact]
   public async Task ManySlicesAndOneEntityTwice()
   {
      await using Harness harness = await Harness.CreateAsync();
      DashboardDefinition definition = Dashboard([W("series", Bar("orders", D("status"), D("customer_id"))), W("months", Line("orders", D("order_date", Bucket.Month)))]);
      object?[][] keys = [.. Enumerable.Range(0, 100).Select(i => new object?[] { i % 2 == 0 ? "open" : "shipped", i })];
      (WidgetPlan? plan, _, Dictionary<string, string[]> errors) = await harness.PlanAsync(definition, "months", Choose("series", SelectionMode.Include, keys),
         Signed with { MaxSelectionKeys = 200 });
      errors.ShouldBeEmpty();
      BuiltQuery main = plan!.Main(null);
      main.Parameters.All.Count().ShouldBe(200);
      (await harness.RowsAsync(main)).ShouldBe(Lines("d0 | m0", "\"2026-01-01\" | \"1\"", "\"2026-02-01\" | \"1\""), "shipped of customer 1 (1002), open of 2 (1003)");

      DashboardDefinition twice = Dashboard([W("by-status", Bar("orders", D("status"))), W("by-customer", Bar("again", D("customer_id")))],
         sources: [new DashboardSource("orders", "shop.orders", "Orders"), new DashboardSource("again", "shop.orders", "Orders again")],
         links: [new DashboardLink("orders", "again", [])]);
      (WidgetPlan? same, _, _) = await harness.PlanAsync(twice, "by-customer", Choose("by-status", SelectionMode.Include, ["open"]));
      same!.Main(null).Text.ShouldBe("(shop.orders).where(r0 => r0.status == $f1).groupBy(d0: customer_id).select(d0, m0: count()).orderBy(desc(m0), d0).take(26)");
   }

   /// <summary>A dashboard is saved whatever the catalog has, and says what doesn't fit it, in the copy the user sees.</summary>
   [Fact]
   public async Task DashboardsSayWhatDoesntFitTheCatalog()
   {
      await using Harness harness = await Harness.CreateAsync();
      DashboardDefinition definition = Dashboard([W("fine", Bar("orders", D("status"))), W("broken", Bar("orders", D("colour")))]);
      JsonElement saved = await (await harness.Admin.PostAsync("/api/dashboards", new { name = "Sales", definition = Definitions.Json(definition) })).JsonAsync(HttpStatusCode.Created);
      saved.GetProperty("issues").EnumerateArray().Select(i => $"{i.GetProperty("severity").GetString()} {i.GetProperty("widget").GetString()} {i.GetProperty("field").GetString()}: {i.GetProperty("message").GetString()}")
         .ShouldBe(["error broken widgets[1].config.dimension.field: shop.orders has no column colour"]);
      JsonElement read = await (await harness.Admin.GetAsync($"/api/dashboards/{saved.GetProperty("id").GetInt32()}")).JsonAsync(HttpStatusCode.OK);
      read.GetProperty("issues").GetArrayLength().ShouldBe(1);
   }

   private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;
}
