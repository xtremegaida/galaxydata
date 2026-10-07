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
