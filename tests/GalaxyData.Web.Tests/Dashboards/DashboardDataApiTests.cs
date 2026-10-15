using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Dashboards;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Problems;
using GalaxyData.Web.Tests.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Dashboards;

public sealed class DashboardDataApiTests
{
   private static Listens All => Definitions.All;

   private static Dimension D(string column, Bucket? bucket = null) => new(new FieldRef([], column), bucket, column);

   private static BarConfig Bar(string column, int limit = 25) =>
      new("orders", [], true, All, D(column), null, [new Measure(Aggregate.Count, null, "Orders", null)], new WidgetSort(SortTarget.Measure, 0, true), limit,
         BarOrientation.Vertical, BarStack.None, false, LegendPosition.Bottom, null, null);

   private static DashboardDefinition Slice(params (string Id, WidgetConfig Config)[] widgets) => Definitions.Blank() with
   {
      Sources = [new DashboardSource("orders", "shop.orders", "Orders"), new DashboardSource("customers", "shop.customers", "Customers")],
      Links = [new DashboardLink("orders", "customers", ["customer"])],
      Widgets = [.. widgets.Select(w => new DashboardWidget(w.Id, null, w.Config))],
      Layout = DashboardLayout.Default() with { Items = widgets.Select((w, i) => (w.Id, new Placement(0, i, 12, 1))).ToDictionary(p => p.Id, p => p.Item2) },
   };

   private static async Task<(WebAppFactory Factory, TestApi Admin)> ShopAsync(Dictionary<string, string?>? settings = null)
   {
      WebAppFactory factory = new() { Settings = settings ?? new Dictionary<string, string?>() };
      TestApi admin = await TestApi.SignedInAsync(factory);
      await TestSources.AddSqliteAsync(admin, "shop", await TestSources.ShopAsync(factory));
      return (factory, admin);
   }

   private static string Rows(JsonElement data) =>
      string.Join(" / ", data.GetProperty("rows").EnumerateArray().Select(r => string.Join(" ", r.EnumerateArray().Select(v => v.GetRawText()))));

   [Fact]
   public async Task AWidgetOfADefinitionSentGivesItsRows()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      DashboardDefinition slice = Slice(("by-status", Bar("status")), ("note", new TextConfig("# Hi")));
      JsonElement data = await (await admin.PostAsync("/api/dashboards/data", new { slice = Definitions.Json(slice), widget = "by-status" })).JsonAsync(HttpStatusCode.OK);
      data.GetProperty("columns").EnumerateArray().Select(c => $"{c.GetProperty("name").GetString()} {c.GetProperty("role").GetString()} {c.GetProperty("label").GetString()} {c.GetProperty("type").GetProperty("text").GetString()}")
         .ShouldBe(["d0 dimension status string", "m0 measure Orders int64"]);
      Rows(data).ShouldBe("\"open\" \"2\" / \"cancelled\" \"1\" / \"shipped\" \"1\"");
      (data.GetProperty("truncated").GetBoolean(), data.GetProperty("cached").GetBoolean()).ShouldBe((false, false));

      JsonElement top = await (await admin.PostAsync("/api/dashboards/data", new { slice = Definitions.Json(Slice(("by-status", Bar("status", limit: 1)))), widget = "by-status" }))
         .JsonAsync(HttpStatusCode.OK);
      (Rows(top), top.GetProperty("truncated").GetBoolean()).ShouldBe(("\"open\" \"2\"", true));

      // The same again: kept from the first, for a while.
      JsonElement again = await (await admin.PostAsync("/api/dashboards/data", new { slice = Definitions.Json(slice), widget = "by-status" })).JsonAsync(HttpStatusCode.OK);
      (again.GetProperty("cached").GetBoolean(), again.GetProperty("refreshedAt").GetString()).ShouldBe((true, data.GetProperty("refreshedAt").GetString()));
      JsonElement refreshed = await (await admin.PostAsync("/api/dashboards/data", new { slice = Definitions.Json(slice), widget = "by-status", refresh = true }))
         .JsonAsync(HttpStatusCode.OK);
      refreshed.GetProperty("cached").GetBoolean().ShouldBeFalse();

      await (await admin.PostAsync("/api/dashboards/data", new { slice = Definitions.Json(slice), widget = "note" })).ProblemAsync(404, ProblemCodes.NotFound);
      await (await admin.PostAsync("/api/dashboards/data", new { slice = Definitions.Json(slice), widget = "gone" })).ProblemAsync(404, ProblemCodes.NotFound);
   }

   [Fact]
   public async Task WhatIsWrongIsSaidByFieldOrAsTheWidgetsIssues()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      DashboardDefinition slice = Slice(("by-colour", Bar("colour")), ("by-status", Bar("status")), ("again", Bar("status")));
      JsonElement invalid = await (await admin.PostAsync("/api/dashboards/data", new { slice = Definitions.Json(slice), widget = "by-colour" })).ProblemAsync(422, ProblemCodes.WidgetInvalid);
      invalid.GetProperty("issues")[0].GetProperty("message").GetString().ShouldBe("shop.orders has no column colour");

      JsonElement errors = (await (await admin.PostAsync("/api/dashboards/data", new { slice = Definitions.Json(slice with { Schema = 3 }), widget = "by-status" }))
         .ProblemAsync(400, ProblemCodes.InvalidRequest)).GetProperty("errors");
      errors.EnumerateObject().Select(e => e.Name).ShouldBe(["slice.schema"]);

      JsonElement state = (await (await admin.PostAsync("/api/dashboards/data", new
      {
         slice = Definitions.Json(slice),
         widget = "again",
         state = new { selections = new Dictionary<string, object> { ["by-status"] = new { mode = "include", keys = new[] { new[] { "open", "extra" } } } } },
      })).ProblemAsync(400, ProblemCodes.InvalidRequest)).GetProperty("errors");
      state.EnumerateObject().Select(e => e.Name).ShouldBe(["state.selections.by-status.keys[0]"]);
   }

   [Fact]
   public async Task ViewersAskForThePublishedCopyTheyRead()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      int kimId = await admin.CreateUserAsync("kim", "read", "first-password-of-a-user");
      TestApi kim = await TestApi.SignedInAsync(factory, "kim", "first-password-of-a-user", changeTo: "second-password-of-a-user");
      DashboardFilter hidden = new("open", "Open", new SourceField("orders", [], "status"), FilterKind.Values, new ConditionValue(ConditionOp.In, Values: ["open"]), false, false, true, []);
      DashboardDefinition definition = Slice(("by-status", Bar("status"))) with { Filters = [hidden] };
      JsonElement dashboard = await (await admin.PostAsync("/api/dashboards", new { name = "Sales", definition = Definitions.Json(definition) })).JsonAsync(HttpStatusCode.Created);
      int id = dashboard.GetProperty("id").GetInt32();
      await (await kim.PostAsync($"/api/dashboards/{id}/widgets/by-status/data", new { hash = dashboard.GetProperty("workingHash").GetString() }))
         .ProblemAsync(404, ProblemCodes.NotFound);
      dashboard = await (await admin.PostAsync($"/api/dashboards/{id}/publish", new { version = 0 })).JsonAsync(HttpStatusCode.OK);
      await (await admin.PutAsync($"/api/dashboards/{id}/sharing", new { everyone = false, users = new[] { kimId }, version = dashboard.GetProperty("version").GetInt32() }))
         .JsonAsync(HttpStatusCode.OK);
      string hash = dashboard.GetProperty("publishedHash").GetString()!;

      // A hidden filter holds whatever a viewer sends for it.
      JsonElement data = await (await kim.PostAsync($"/api/dashboards/{id}/widgets/by-status/data", new
      {
         hash,
         state = new { filters = new Dictionary<string, object> { ["open"] = new { op = "in", values = new[] { "cancelled" } } } },
      })).JsonAsync(HttpStatusCode.OK);
      Rows(data).ShouldBe("\"open\" \"2\"");
      await (await kim.PostAsync($"/api/dashboards/{id}/widgets/by-status/data", new { hash = new string('0', 64) })).ProblemAsync(409, ProblemCodes.DashboardChanged);

      JsonElement query = await (await kim.PostAsync($"/api/dashboards/{id}/widgets/by-status/query", new { hash })).JsonAsync(HttpStatusCode.OK);
      query.GetProperty("main").GetProperty("text").GetString().ShouldBe("(shop.orders).where(r0 => r0.status in [$f1]).groupBy(d0: status).select(d0, m0: count()).orderBy(desc(m0), d0).take(26)");
      query.GetProperty("main").GetProperty("parameters").GetRawText().ShouldBe("""[{"name":"f1","type":"string","value":"open"}]""");
      query.GetProperty("underlying").GetProperty("text").GetString().ShouldBe("(shop.orders).where(r0 => r0.status in [$f1])");
   }

   [Fact]
   public async Task TablesPageAndPiesAddUpTheRest()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      TableConfig raw = new("orders", [], true, All, TableMode.Raw, [], [], [new TableColumn(new FieldRef([], "total"), "Total", null)], new WidgetSort(SortTarget.Column, 0, true), 2);
      PieConfig pie = new("orders", [], true, All, D("status"), new Measure(Aggregate.Sum, new FieldRef([], "total"), "Total", null), 1, true, false, true, LegendPosition.Right);
      DashboardDefinition slice = Slice(("raw", raw), ("pie", pie));
      JsonElement first = await (await admin.PostAsync("/api/dashboards/data", new { slice = Definitions.Json(slice), widget = "raw", page = new { offset = 0, count = true } }))
         .JsonAsync(HttpStatusCode.OK);
      (Rows(first), first.GetProperty("truncated").GetBoolean(), first.GetProperty("total").GetInt64()).ShouldBe(("\"250.00\" \"1001\" / \"99.50\" \"1002\"", true, 4L));
      JsonElement second = await (await admin.PostAsync("/api/dashboards/data", new { slice = Definitions.Json(slice), widget = "raw", page = new { offset = 2, limit = 5 } }))
         .JsonAsync(HttpStatusCode.OK);
      (Rows(second), second.GetProperty("truncated").GetBoolean(), second.GetProperty("offset").GetInt64()).ShouldBe(("\"12.25\" \"1003\" / \"0.00\" \"1004\"", false, 2L));

      JsonElement slices = await (await admin.PostAsync("/api/dashboards/data", new { slice = Definitions.Json(slice), widget = "pie" })).JsonAsync(HttpStatusCode.OK);
      (Rows(slices), slices.GetProperty("other").GetString()).ShouldBe(("\"open\" \"262.25\"", "99.50"), "shipped and cancelled: 99.50 and 0.00");
   }

   private static long? Next(JsonElement data) => data.TryGetProperty("next", out JsonElement next) ? next.GetInt64() : null;

   private static string Texts(JsonElement data, string name) =>
      data.TryGetProperty(name, out JsonElement list) ? string.Join(" / ", list.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Array
         ? string.Join(" ", v.EnumerateArray().Select(p => p.GetRawText()))
         : v.GetRawText())) : "(none)";

   [Fact]
   public async Task PiesStepIntoTheirOther()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      PieConfig pie = new("orders", [], true, All, D("status"), new Measure(Aggregate.Count, null, "Orders", null), 1, true, false, true, LegendPosition.Right);
      DashboardDefinition slice = Slice(("pie", pie), ("bars", Bar("status")));
      async Task<JsonElement> At(string widget, long offset) =>
         await (await admin.PostAsync("/api/dashboards/data", new { slice = Definitions.Json(slice), widget, page = new { offset } })).JsonAsync(HttpStatusCode.OK);

      JsonElement first = await At("pie", 0);
      (Rows(first), first.GetProperty("other").GetString(), Next(first), first.GetProperty("offset").GetInt64()).ShouldBe(("\"open\" \"2\"", "2", 1L, 0L));
      JsonElement second = await At("pie", 1);
      (Rows(second), second.GetProperty("other").GetString(), Next(second), second.GetProperty("offset").GetInt64())
         .ShouldBe(("\"cancelled\" \"1\"", "1", 2L, 1L), "the rest is the total less the slices before and shown");
      JsonElement last = await At("pie", 2);
      (Rows(last), last.GetProperty("other").ValueKind, Next(last)).ShouldBe(("\"shipped\" \"1\"", JsonValueKind.Null, (long?)null));
      (await At("pie", 1)).GetProperty("cached").GetBoolean().ShouldBeTrue("a step is kept as any answer is");

      JsonElement bars = await At("bars", 5);
      (bars.GetProperty("offset").GetInt64(), Next(bars), Rows(bars)).ShouldBe((0L, (long?)null, "\"open\" \"2\" / \"cancelled\" \"1\" / \"shipped\" \"1\""), "bars without series don't step");
   }

   /// <summary>The same whether the charts' rows are read whole and limited in memory, or limited by queries.</summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task SeriesShowTheLargestAndOtherStepsIntoTheRest(bool whole)
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync(new Dictionary<string, string?>
      {
         ["GalaxyData:Dashboards:MaxOtherDepth"] = "2",
         ["GalaxyData:Dashboards:MaxWholeRows"] = whole ? "20000" : "0",
      });
      await using WebAppFactory _ = factory;
      Measure count = new(Aggregate.Count, null, "Orders", null);
      BarConfig bars = Bar("status") with { Series = D("customer_id"), SeriesLimit = 1, SeriesOther = true };
      LineConfig days = new("orders", [], true, All, D("order_date", Bucket.Day), D("status"), [count], 1000, false, LineGaps.Break, false, LegendPosition.Bottom, null, null,
         SeriesLimit: 1, SeriesOther: true);
      DashboardDefinition slice = Slice(("bars", bars), ("days", days), ("cut", days with { Limit = 1 }));
      async Task<JsonElement> At(string widget, long offset) =>
         await (await admin.PostAsync("/api/dashboards/data", new { slice = Definitions.Json(slice), widget, page = new { offset } })).JsonAsync(HttpStatusCode.OK);

      JsonElement first = await At("bars", 0);
      (Rows(first), Texts(first, "series"), first.GetProperty("moreSeries").GetBoolean(), Texts(first, "otherRows"), Next(first))
         .ShouldBe(("\"open\" \"1\" \"1\" / \"shipped\" \"1\" \"1\"", "\"1\"", true, "\"cancelled\" \"1\" / \"open\" \"1\"", 1L));
      Texts(first, "categories").ShouldBe("\"open\" / \"cancelled\" / \"shipped\"");
      JsonElement second = await At("bars", 1);
      (Rows(second), Texts(second, "series"), Texts(second, "otherRows"), Next(second), second.GetProperty("offset").GetInt64())
         .ShouldBe(("\"open\" \"2\" \"1\"", "\"2\"", "\"cancelled\" \"1\"", (long?)null, 1L), "two deep at most: no step to the third");
      JsonElement deep = (await (await admin.PostAsync("/api/dashboards/data", new { slice = Definitions.Json(slice), widget = "bars", page = new { offset = 2 } }))
         .ProblemAsync(400, ProblemCodes.InvalidRequest)).GetProperty("errors");
      deep.EnumerateObject().Select(e => e.Name).ShouldBe(["page.offset"]);

      // A line's x values are its points' and its rest's.
      JsonElement line = await At("days", 0);
      (Texts(line, "categories"), Rows(line), Texts(line, "otherRows")).ShouldBe(("\"2026-01-05\" / \"2026-01-09\" / \"2026-02-01\" / \"2026-02-14\"",
         "\"2026-01-05\" \"open\" \"1\" / \"2026-02-01\" \"open\" \"1\"", "\"2026-01-09\" \"1\" / \"2026-02-14\" \"1\""));
      // Cut short, at the first last value of what was cut: what follows may be missing from it.
      JsonElement cut = await At("cut", 0);
      (Texts(cut, "categories"), Rows(cut), Texts(cut, "otherRows"), cut.GetProperty("truncated").GetBoolean())
         .ShouldBe(("\"2026-01-05\"", "\"2026-01-05\" \"open\" \"1\"", string.Empty, true));

      JsonElement query = await (await admin.PostAsync("/api/dashboards/query", new { slice = Definitions.Json(slice), widget = "bars", page = new { offset = 1 } }))
         .JsonAsync(HttpStatusCode.OK);
      if (whole)
      {
         query.GetProperty("main").GetProperty("text").GetString()
            .ShouldBe("shop.orders.groupBy(d0: status, s0: customer_id).select(d0, s0, m0: count()).orderBy(d0, s0).take(20001)", "one query, whatever the step");
         query.EnumerateObject().Select(p => p.Name).ShouldNotContain("series");
         return;
      }
      query.GetProperty("series").GetProperty("text").GetString()
         .ShouldBe("(shop.orders).where(r0 => r0.status in [$f1, $f2, $f3]).groupBy(s0: customer_id).select(s0, m0: count()).orderBy(desc(m0), s0).take(3)");
      query.GetProperty("rest").GetProperty("text").GetString()
         .ShouldBe("(shop.orders).where(r0 => r0.status in [$f1, $f2, $f3]).where(r0 => (not (r0.customer_id in [$f4, $f5]) or r0.customer_id == null)).groupBy(d0: status).select(d0, m0: count()).orderBy(d0)");
      query.GetProperty("main").GetProperty("parameters")[3].GetProperty("value").GetString().ShouldBe("2", "the second largest customer's bars");
   }

   /// <summary>A KPI's one row is its number; compared, the number over the period before is given with its days; with a sparkline, its points, the oldest first.</summary>
   [Fact]
   public async Task AKpiGivesItsNumberThePeriodBeforeAndItsPoints()
   {
      // The orders are of January and February: on 1 March, the last 30 days hold February's two, and the 30 before January's.
      ManualClock clock = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
      WebAppFactory factory = new() { ServiceChanges = services => services.AddSingleton<TimeProvider>(clock) };
      await using WebAppFactory _ = factory;
      TestApi admin = await TestApi.SignedInAsync(factory);
      await TestSources.AddSqliteAsync(admin, "shop", await TestSources.ShopAsync(factory));
      DashboardFilter period = new("period", "Period", new SourceField("orders", [], "order_date"), FilterKind.Relative,
         new ConditionValue(ConditionOp.Relative, Relative: new RelativeRange(RelativeUnit.Day, 30, RelativeMode.Last)), true, true, false, []);
      KpiConfig kpi = new("orders", [], false, All, new Measure(Aggregate.Count, null, "Orders", new NumberFormat(null, null, null, true)), new KpiCompare("period", KpiGood.Up),
         new Dimension(new FieldRef([], "order_date"), Bucket.Day, "Day"));
      DashboardDefinition slice = Slice(("orders", kpi), ("by-status", Bar("status"))) with { Filters = [period] };
      async Task<JsonElement> DataAsync(object? state = null) =>
         await (await admin.PostAsync("/api/dashboards/data", new { slice = Definitions.Json(slice), widget = "orders", state })).JsonAsync(HttpStatusCode.OK);

      JsonElement data = await DataAsync();
      data.GetProperty("columns").EnumerateArray().Select(c => $"{c.GetProperty("name").GetString()} {c.GetProperty("role").GetString()} {c.GetProperty("label").GetString()}")
         .ShouldBe(["m0 measure Orders"]);
      (Rows(data), data.GetProperty("truncated").GetBoolean()).ShouldBe(("\"2\"", false));
      data.GetProperty("previous").GetRawText().ShouldBe("""{"value":"2","from":"2026-01-01","to":"2026-01-30"}""");
      Texts(data, "points").ShouldBe("\"2026-02-01\" \"1\" / \"2026-02-14\" \"1\"");
      (await DataAsync()).GetProperty("cached").GetBoolean().ShouldBeTrue();

      // The viewer's period, and the choices it listens to, hold for the period before too.
      JsonElement year = await DataAsync(new
      {
         filters = new Dictionary<string, object> { ["period"] = new { op = "relative", relative = new { unit = "year", count = 1, mode = "last" } } },
         selections = new Dictionary<string, object> { ["by-status"] = new { mode = "include", keys = new[] { new[] { "open" } } } },
      });
      (Rows(year), year.GetProperty("previous").GetRawText()).ShouldBe(("\"2\"", """{"value":"0","from":"2024-03-02","to":"2025-03-01"}"""));
      Texts(year, "points").ShouldBe("\"2026-01-05\" \"1\" / \"2026-02-01\" \"1\"");
      JsonElement cleared = await DataAsync(new { filters = new Dictionary<string, object?> { ["period"] = null } });
      (Rows(cleared), cleared.TryGetProperty("previous", out JsonElement _)).ShouldBe(("\"4\"", false), "no period: nothing to compare with");

      JsonElement query = await (await admin.PostAsync("/api/dashboards/query", new { slice = Definitions.Json(slice), widget = "orders" })).JsonAsync(HttpStatusCode.OK);
      query.GetProperty("main").GetProperty("text").GetString().ShouldBe("(shop.orders).where(r0 => (r0.order_date >= $f1 and r0.order_date < $f2)).groupBy().select(m0: count())");
      query.GetProperty("previous").GetProperty("text").GetString().ShouldBe("(shop.orders).where(r0 => (r0.order_date >= $f3 and r0.order_date < $f4)).groupBy().select(m0: count())",
         "the period's text, of other days (parameters of its own, after the main query's)");
      query.GetProperty("previous").GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("value").GetString()).ShouldBe(["2026-01-01", "2026-01-31"]);
      query.GetProperty("sparkline").GetProperty("text").GetString()
         .ShouldBe("(shop.orders).where(r0 => (r0.order_date >= $f1 and r0.order_date < $f2)).groupBy(d0: date(order_date)).select(d0, m0: count()).orderBy(desc(d0)).take(501)");
      JsonElement bars = await (await admin.PostAsync("/api/dashboards/query", new { slice = Definitions.Json(slice), widget = "by-status" })).JsonAsync(HttpStatusCode.OK);
      (bars.TryGetProperty("previous", out JsonElement _), bars.TryGetProperty("sparkline", out JsonElement _)).ShouldBe((false, false));
   }

   [Fact]
   public async Task FiltersOfferTheirValues()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      DashboardFilter status = new("status", "Status", new SourceField("orders", [], "status"), FilterKind.Values, null, true, true, true, []);
      DashboardFilter city = new("city", "City", new SourceField("customers", [], "city"), FilterKind.Values, null, true, true, true, []);
      DashboardDefinition slice = Slice(("by-status", Bar("status"))) with { Filters = [status, city] };
      JsonElement values = await (await admin.PostAsync("/api/dashboards/filter-values", new
      {
         slice = Definitions.Json(slice),
         filter = "status",
         state = new { filters = new Dictionary<string, object> { ["city"] = new { op = "in", values = new[] { "Cape Town" } } } },
      })).JsonAsync(HttpStatusCode.OK);
      values.GetProperty("values").EnumerateArray().Select(v => $"{v.GetProperty("value").GetString()} {v.GetProperty("rows").GetInt64()}").ShouldBe(["open 1", "shipped 1"]);
      (values.GetProperty("type").GetProperty("text").GetString(), values.GetProperty("more").GetBoolean()).ShouldBe(("string", false));
      JsonElement searched = await (await admin.PostAsync("/api/dashboards/filter-values", new { slice = Definitions.Json(slice), filter = "city", search = "town" }))
         .JsonAsync(HttpStatusCode.OK);
      searched.GetProperty("values").EnumerateArray().Select(v => v.GetProperty("value").GetString()).ShouldBe(["Cape Town"]);
      await (await admin.PostAsync("/api/dashboards/filter-values", new { slice = Definitions.Json(slice), filter = "nowhere" })).ProblemAsync(404, ProblemCodes.NotFound);
   }

   [Fact]
   public async Task AnswersAreWorkedOutOnceForEveryoneAskingAndFailuresKeptAWhile()
   {
      using WidgetResults results = new(Options.Create(new GalaxyDataOptions()), TimeProvider.System);
      int computed = 0;
      TaskCompletionSource release = new();
      async Task<string> Compute(CancellationToken cancellationToken)
      {
         Interlocked.Increment(ref computed);
         await release.Task;
         return "rows";
      }
      using CancellationTokenSource leaving = new();
      Task<(string, bool, DateTime)> first = results.GetAsync("k", TimeSpan.FromMinutes(1), false, Compute, leaving.Token);
      Task<(string, bool, DateTime)>[] others = [.. Enumerable.Range(0, 7).Select(_ => results.GetAsync("k", TimeSpan.FromMinutes(1), false, Compute, CancellationToken.None))];
      await leaving.CancelAsync();
      await Should.ThrowAsync<OperationCanceledException>(() => first);
      release.SetResult();
      (await Task.WhenAll(others)).Select(r => r.Item1).ShouldAllBe(v => v == "rows");
      computed.ShouldBe(1, "the first going away doesn't stop it for the others");
      (await results.GetAsync("k", TimeSpan.FromMinutes(1), false, Compute, CancellationToken.None)).Item2.ShouldBeTrue("kept");

      int failed = 0;
      Task<string> Fail(CancellationToken cancellationToken)
      {
         Interlocked.Increment(ref failed);
         throw new InvalidOperationException("down");
      }
      await Should.ThrowAsync<InvalidOperationException>(() => results.GetAsync("f", TimeSpan.FromMinutes(1), false, Fail, CancellationToken.None));
      await Should.ThrowAsync<InvalidOperationException>(() => results.GetAsync("f", TimeSpan.FromMinutes(1), false, Fail, CancellationToken.None));
      failed.ShouldBe(1, "a failure is kept a little while");
   }
}
