using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Dashboards;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Palettes;
using GalaxyData.Web.Problems;
using GalaxyData.Web.Tests.Catalog;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Dashboards;

public sealed class PublicDashboardApiTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   private static Listens All => Definitions.All;

   /// <summary>
   /// A dashboard of the shop's orders: by state (a label unlike its column's), a pie of cities, a raw table, a visible
   /// filter, and a hidden one leaving out Gamma Inc.
   /// </summary>
   private static DashboardDefinition Definition()
   {
      DashboardDefinition blank = Definitions.Blank();
      BarConfig byState = new("sales", [new WidgetCondition(new FieldRef([], "total"), new ConditionValue(ConditionOp.Ge, Value: 0))], true, All,
         new Dimension(new FieldRef([], "status"), null, "State"), null, [new Measure(Aggregate.Count, null, "Orders", null)], new WidgetSort(SortTarget.Measure, 0, true), 25,
         BarOrientation.Vertical, BarStack.None, false, LegendPosition.Bottom, null, null);
      PieConfig cities = new("buyers", [], true, All, new Dimension(new FieldRef([], "city"), null, "Town"), new Measure(Aggregate.Count, null, "Buyers", null), 10, false, false, true,
         LegendPosition.Right);
      TableConfig raw = new("sales", [], true, All, TableMode.Raw, [], [], [new TableColumn(new FieldRef([], "total"), "Amount", null)], new WidgetSort(SortTarget.Column, 0, true), 10);
      BarConfig broken = byState with { Dimension = new Dimension(new FieldRef([], "colour"), null, "Colour") };
      List<DashboardWidget> widgets =
      [
         new("heading", null, new TextConfig("# Sales")), new("by-state", "By state", byState), new("towns", "Towns", cities), new("amounts", null, raw),
         new("broken", null, broken),
      ];
      return blank with
      {
         Sources = [new DashboardSource("sales", "shop.orders", "Sales"), new DashboardSource("buyers", "shop.customers", "Buyers")],
         Links = [new DashboardLink("sales", "buyers", ["customer"])],
         Filters =
         [
            new DashboardFilter("state", "State", new SourceField("sales", [], "status"), FilterKind.Values, null, true, true, true, []),
            new DashboardFilter("not-gamma", "Not Gamma", new SourceField("buyers", [], "name"), FilterKind.Values, new ConditionValue(ConditionOp.NotIn, Values: ["Gamma Inc"]),
               false, false, true, []),
         ],
         Widgets = widgets,
         Layout = DashboardLayout.Default() with { Items = widgets.Select((w, i) => (w.Id, new Placement(0, i, 12, 2))).ToDictionary(p => p.Id, p => p.Item2) },
         Refresh = new RefreshPolicy(RefreshMode.Interval, 31),
      };
   }

   private static string State(object state) =>
      Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, DefinitionJson.Options))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

   private sealed record Setup(WebAppFactory Factory, TestApi Admin, int Id, string Token, string WebRoot);

   private static async Task<Setup> PublicAsync(Dictionary<string, string?>? settings = null, string[]? origins = null)
   {
      string webRoot = Path.Combine(Path.GetTempPath(), "gd-web-tests", Guid.NewGuid().ToString("N")[..12] + "-root");
      Directory.CreateDirectory(webRoot);
      await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<html>the client</html>", Token);
      WebAppFactory factory = new() { WebRoot = webRoot, Settings = settings ?? new Dictionary<string, string?>() };
      TestApi admin = await TestApi.SignedInAsync(factory);
      await TestSources.AddSqliteAsync(admin, "shop", await TestSources.ShopAsync(factory));
      JsonElement dashboard = await (await admin.PostAsync("/api/dashboards", new { name = "Sales", definition = Definitions.Json(Definition()) })).JsonAsync(HttpStatusCode.Created);
      int id = dashboard.GetProperty("id").GetInt32();
      dashboard = await (await admin.PostAsync($"/api/dashboards/{id}/publish", new { version = 0 })).JsonAsync(HttpStatusCode.OK);
      dashboard = await (await admin.PutAsync($"/api/dashboards/{id}/public", new { enabled = true, origins = origins ?? [], version = dashboard.GetProperty("version").GetInt32() }))
         .JsonAsync(HttpStatusCode.OK);
      return new Setup(factory, admin, id, dashboard.GetProperty("public").GetProperty("token").GetString()!, webRoot);
   }

   private static string Rows(JsonElement data) =>
      string.Join(" / ", data.GetProperty("rows").EnumerateArray().Select(r => string.Join(" ", r.EnumerateArray().Select(v => v.GetRawText()))));

   [Fact]
   public async Task AnyoneWithTheLinkSeesWhatItShowsAndNothingOfItsQueries()
   {
      Setup setup = await PublicAsync();
      await using WebAppFactory factory = setup.Factory;
      TestApi anyone = new(setup.Factory);
      List<string> bodies = [];
      async Task<HttpResponseMessage> GetAsync(string path)
      {
         HttpResponseMessage response = await anyone.GetAsync(path);
         response.Headers.Contains("X-Catalog-Version").ShouldBeFalse(path);
         bodies.Add(await response.Content.ReadAsStringAsync(Token));
         return response;
      }

      HttpResponseMessage read = await GetAsync($"/api/public/dashboards/{setup.Token}");
      JsonElement dashboard = await read.JsonAsync(HttpStatusCode.OK);
      (read.Headers.CacheControl!.Private, read.Headers.CacheControl.NoCache).ShouldBe((true, true));
      string tag = read.Headers.ETag!.Tag;
      JsonElement definition = dashboard.GetProperty("definition");
      (definition.GetProperty("sources").GetArrayLength(), definition.GetProperty("links").GetArrayLength()).ShouldBe((0, 0));
      definition.GetProperty("filters").EnumerateArray().Select(f => f.GetProperty("id").GetString()).ShouldBe(["state"], "the hidden filter stays on the server");
      definition.GetProperty("refresh").GetProperty("seconds").GetInt32().ShouldBe(31, "longer than answers are kept");
      JsonElement bar = definition.GetProperty("widgets")[1].GetProperty("config");
      (bar.GetProperty("source").GetString(), bar.GetProperty("conditions").GetArrayLength(), bar.GetProperty("dimension").GetProperty("label").GetString())
         .ShouldBe((string.Empty, 0, "State"));
      bar.GetProperty("dimension").GetProperty("field").GetProperty("column").GetString().ShouldBe(string.Empty);
      using HttpRequestMessage again = new(HttpMethod.Get, $"/api/public/dashboards/{setup.Token}");
      again.Headers.TryAddWithoutValidation("If-None-Match", tag);
      (await anyone.Client.SendAsync(again, Token)).StatusCode.ShouldBe(HttpStatusCode.NotModified);

      JsonElement byState = await (await GetAsync($"/api/public/dashboards/{setup.Token}/widgets/by-state/data")).JsonAsync(HttpStatusCode.OK);
      Rows(byState).ShouldBe("\"open\" \"2\" / \"shipped\" \"1\"", "Gamma's cancelled order is left out by the hidden filter");
      byState.GetProperty("issues").GetArrayLength().ShouldBe(0);
      string chosen = State(new DashboardState(new Dictionary<string, ConditionValue?> { ["state"] = new(ConditionOp.In, Values: ["shipped"]) },
         new Dictionary<string, SelectionState> { ["towns"] = new(SelectionMode.Include, [["Cape Town"]]) }));
      JsonElement filtered = await (await GetAsync($"/api/public/dashboards/{setup.Token}/widgets/by-state/data?s={chosen}")).JsonAsync(HttpStatusCode.OK);
      Rows(filtered).ShouldBe("\"shipped\" \"1\"");
      JsonElement amounts = await (await GetAsync($"/api/public/dashboards/{setup.Token}/widgets/amounts/data?limit=2&count=true")).JsonAsync(HttpStatusCode.OK);
      (Rows(amounts), amounts.GetProperty("columns").GetArrayLength(), amounts.GetProperty("total").GetInt64()).ShouldBe(("\"250.00\" / \"99.50\"", 1, 3L), "no key column");
      JsonElement values = await (await GetAsync($"/api/public/dashboards/{setup.Token}/filters/state/values?text=o")).JsonAsync(HttpStatusCode.OK);
      values.GetProperty("values").EnumerateArray().Select(v => v.GetProperty("value").GetString()).ShouldBe(["open"], "shipped has no o... and Gamma's cancelled is hidden");
      await (await GetAsync($"/api/public/dashboards/{setup.Token}/filters/not-gamma/values")).ProblemAsync(404, ProblemCodes.NotFound);

      JsonElement broken = await (await GetAsync($"/api/public/dashboards/{setup.Token}/widgets/broken/data")).ProblemAsync(422, ProblemCodes.WidgetInvalid);
      broken.TryGetProperty("issues", out JsonElement issues).ShouldBeFalse(issues.ToString());
      broken.TryGetProperty("detail", out JsonElement detail).ShouldBeFalse(detail.ToString());
      JsonElement refused = (await (await GetAsync($"/api/public/dashboards/{setup.Token}/widgets/by-state/data?s=%%%")).ProblemAsync(400, ProblemCodes.InvalidRequest))
         .GetProperty("errors");
      refused.EnumerateObject().Select(e => e.Name).ShouldBe(["s"]);
      await (await GetAsync($"/api/public/dashboards/{setup.Token}/widgets/by-state/data?s={new string('A', 5000)}")).ProblemAsync(400, ProblemCodes.InvalidRequest);
      await (await GetAsync($"/api/public/dashboards/{setup.Token}/widgets/heading/data")).ProblemAsync(404, ProblemCodes.NotFound);

      foreach (string body in bodies)
      {
         foreach (string secret in (string[])["shop.", "Gamma", "not-gamma", "customer_id", "r0 =>", "colour", "sales", "buyers"]) { body.ShouldNotContain(secret, Case.Sensitive, body); }
      }
   }

   [Fact]
   public async Task ChartsGetTheOverridesOfWhatTheyShowAlone()
   {
      Setup setup = await PublicAsync();
      await using WebAppFactory factory = setup.Factory;
      TestApi anyone = new(setup.Factory);
      List<string> bodies = [];
      async Task<HttpResponseMessage> GetAsync(string path, string? tag = null)
      {
         using HttpRequestMessage request = new(HttpMethod.Get, path);
         if (tag != null) { request.Headers.TryAddWithoutValidation("If-None-Match", tag); }
         HttpResponseMessage response = await anyone.Client.SendAsync(request, Token);
         bodies.Add(await response.Content.ReadAsStringAsync(Token));
         return response;
      }
      async Task<string> ColorsAsync(string widget)
      {
         JsonElement data = await (await GetAsync($"/api/public/dashboards/{setup.Token}/widgets/{widget}/data")).JsonAsync(HttpStatusCode.OK);
         return data.TryGetProperty("colors", out JsonElement colors) ? colors.GetRawText() : "none";
      }

      // Overrides of what it shows (open, with what the palette ignores; the measure's label), and of what it doesn't.
      PaletteDefinition palette = PaletteDefinition.New([new("#2a78d6", "#3987e5"), new("#eb6834", null)]) with
      {
         Matching = new LabelMatching(IgnoreCase: true, IgnoreWhitespace: false, IgnoreBrackets: true, IgnoreAccents: false),
         Overrides =
         [
            new("OPEN (codename Falcon)", new("#1baf7a", null)), new("Gamma Inc", new("#e34948", null)), new("Secret project", new("#4a3aa7", null)),
            new("Orders", new("#eda100", "#c98500")),
         ],
      };
      JsonElement made = await (await setup.Admin.PostAsync("/api/palettes", new { name = "Brand", definition = JsonSerializer.SerializeToElement(palette, DefinitionJson.Options) }))
         .JsonAsync(HttpStatusCode.Created);
      int id = made.GetProperty("id").GetInt32();
      string path = $"/api/public/dashboards/{setup.Token}";
      string before = (await GetAsync(path)).Headers.ETag!.Tag;
      (await ColorsAsync("by-state")).ShouldBe("none", "no palette yet");

      JsonElement dashboard = await (await setup.Admin.GetAsync($"/api/dashboards/{setup.Id}")).JsonAsync(HttpStatusCode.OK);
      dashboard = await (await setup.Admin.PutAsync($"/api/dashboards/{setup.Id}", new
      {
         name = "Sales", description = (string?)null, definition = Definitions.Json(Definition() with { Palette = id }), version = dashboard.GetProperty("version").GetInt32(),
      })).JsonAsync(HttpStatusCode.OK);
      await (await setup.Admin.PostAsync($"/api/dashboards/{setup.Id}/publish", new { version = dashboard.GetProperty("version").GetInt32() })).JsonAsync(HttpStatusCode.OK);
      HttpResponseMessage named = await GetAsync(path, before);
      JsonElement shown = await named.JsonAsync(HttpStatusCode.OK);
      string tag = named.Headers.ETag!.Tag;
      tag.ShouldNotBe(before);
      shown.GetProperty("palettes").GetRawText()
         .ShouldBe($$$"""[{"id":{{{id}}},"colors":[{"light":"#2a78d6","dark":"#3987e5"},{"light":"#eb6834","dark":null}],"assign":"label","distinct":true,"whenOut":"repeat","matching":{"ignoreCase":true,"ignoreWhitespace":false,"ignoreBrackets":true,"ignoreAccents":false}}]""");
      (await ColorsAsync("by-state")).ShouldBe("""[{"label":"open","light":"#1baf7a","dark":null},{"label":"Orders","light":"#eda100","dark":"#c98500"}]""",
         "by the labels as they are in the answer");
      (await ColorsAsync("towns")).ShouldBe("[]");
      (await ColorsAsync("amounts")).ShouldBe("none", "tables aren't coloured");
      (await GetAsync(path, tag)).StatusCode.ShouldBe(HttpStatusCode.NotModified);

      // The palette changed: the dashboard's answer is a new one, and its rows' colours follow at once.
      JsonElement read = await (await setup.Admin.GetAsync($"/api/palettes/{id}")).JsonAsync(HttpStatusCode.OK);
      PaletteDefinition black = palette with { Overrides = [new("open", new("#000000", null))] };
      (await setup.Admin.PutAsync($"/api/palettes/{id}", new { name = "Brand", description = (string?)null, definition = JsonSerializer.SerializeToElement(black, DefinitionJson.Options),
         version = read.GetProperty("version").GetInt32() })).StatusCode.ShouldBe(HttpStatusCode.OK);
      HttpResponseMessage edited = await GetAsync(path, tag);
      edited.StatusCode.ShouldBe(HttpStatusCode.OK);
      (await ColorsAsync("by-state")).ShouldBe("""[{"label":"open","light":"#000000","dark":null}]""");

      // So does a rename (the name is in the answer).
      dashboard = await (await setup.Admin.GetAsync($"/api/dashboards/{setup.Id}")).JsonAsync(HttpStatusCode.OK);
      (await setup.Admin.PutAsync($"/api/dashboards/{setup.Id}", new
      {
         name = "Sales, renamed", description = (string?)null, definition = Definitions.Json(Definition() with { Palette = id }), version = dashboard.GetProperty("version").GetInt32(),
      })).StatusCode.ShouldBe(HttpStatusCode.OK);
      HttpResponseMessage renamed = await GetAsync(path, edited.Headers.ETag!.Tag);
      (await renamed.JsonAsync(HttpStatusCode.OK)).GetProperty("name").GetString().ShouldBe("Sales, renamed");

      foreach (string body in bodies)
      {
         foreach (string secret in (string[])["Gamma", "Secret", "Falcon", "Brand", "#e34948", "#4a3aa7", "\"admin\""]) { body.ShouldNotContain(secret, Case.Sensitive, body); }
      }
   }

   [Fact]
   public async Task ALinkIsntThereWhenItShouldntWork()
   {
      Setup setup = await PublicAsync();
      await using WebAppFactory factory = setup.Factory;
      TestApi anyone = new(setup.Factory);
      string path = $"/api/public/dashboards/{setup.Token}";
      (await anyone.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.OK);
      await (await anyone.GetAsync("/api/public/dashboards/AAAAAAAAAAAAAAAAAAAAAA")).ProblemAsync(404, ProblemCodes.NotFound);
      await (await anyone.GetAsync("/api/public/dashboards/short")).ProblemAsync(404, ProblemCodes.NotFound);
      await (await anyone.GetAsync("/api/public/dashboards/AAAAAAAAAAAAAAAAAAAAAA/widgets/by-state/data")).ProblemAsync(404, ProblemCodes.NotFound);

      // Whoever made it public may no more: the link stops.
      JsonElement user = await (await setup.Admin.GetAsync("/api/users/1")).JsonAsync(HttpStatusCode.OK);
      int other = await setup.Admin.CreateUserAsync("kim", "admin", "first-password-of-a-user");
      TestApi kim = await TestApi.SignedInAsync(setup.Factory, "kim", "first-password-of-a-user", changeTo: "second-password-of-a-user");
      (await kim.PutAsync("/api/users/1", new { role = "read", isDisabled = false, version = user.GetProperty("version").GetInt32() })).StatusCode.ShouldBe(HttpStatusCode.OK);
      await (await anyone.GetAsync(path)).ProblemAsync(404, ProblemCodes.NotFound);
      other.ShouldBeGreaterThan(1);
   }

   [Fact]
   public async Task RevokedOrTurnedOffLinksAreNotFound()
   {
      Setup setup = await PublicAsync();
      await using (setup.Factory)
      {
         TestApi anyone = new(setup.Factory);
         JsonElement dashboard = await (await setup.Admin.GetAsync($"/api/dashboards/{setup.Id}")).JsonAsync(HttpStatusCode.OK);
         await (await setup.Admin.PutAsync($"/api/dashboards/{setup.Id}/public", new { enabled = false, version = dashboard.GetProperty("version").GetInt32() })).JsonAsync(HttpStatusCode.OK);
         await (await anyone.GetAsync($"/api/public/dashboards/{setup.Token}")).ProblemAsync(404, ProblemCodes.NotFound);
      }

      // Public again, then the application started again with public dashboards turned off: the link isn't there.
      Setup again = await PublicAsync();
      string data;
      await using (again.Factory)
      {
         data = again.Factory.DataDirectory;
         (await new TestApi(again.Factory).GetAsync($"/api/public/dashboards/{again.Token}")).StatusCode.ShouldBe(HttpStatusCode.OK);
      }
      await using WebAppFactory off = new() { DataDirectory = data, Settings = new Dictionary<string, string?> { ["GalaxyData:Dashboards:AllowPublic"] = "false" } };
      await (await new TestApi(off).GetAsync($"/api/public/dashboards/{again.Token}")).ProblemAsync(404, ProblemCodes.NotFound);
   }

   [Fact]
   public async Task PublicPagesAreFramedAsTheirDashboardsSay()
   {
      Setup setup = await PublicAsync(new Dictionary<string, string?> { ["GalaxyData:Dashboards:EmbedFrameAncestors"] = "https://*.example.com https://partner.org" },
         origins: ["https://news.example.com", "https://elsewhere.net"]);
      await using WebAppFactory factory = setup.Factory;
      HttpClient client = setup.Factory.CreateClient();
      static string Header(HttpResponseMessage response, string name) => response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(",", values) : string.Empty;

      HttpResponseMessage page = await client.GetAsync($"/embed/{setup.Token}", Token);
      page.StatusCode.ShouldBe(HttpStatusCode.OK);
      (await page.Content.ReadAsStringAsync(Token)).ShouldBe("<html>the client</html>");
      Header(page, "X-Frame-Options").ShouldBeEmpty();
      Header(page, "Content-Security-Policy").ShouldEndWith("frame-ancestors 'self' https://news.example.com", Case.Sensitive, "elsewhere.net isn't allowed here");
      Header(page, "Cross-Origin-Resource-Policy").ShouldBe("cross-origin");
      HttpResponseMessage unknown = await client.GetAsync("/embed/AAAAAAAAAAAAAAAAAAAAAA", Token);
      Header(unknown, "Content-Security-Policy").ShouldEndWith("frame-ancestors 'self' https://*.example.com https://partner.org");
      HttpResponseMessage start = await client.GetAsync("/", Token);
      (Header(start, "X-Frame-Options"), Header(start, "Content-Security-Policy").EndsWith("frame-ancestors 'none'", StringComparison.Ordinal)).ShouldBe(("DENY", true));
      Header(await client.GetAsync("/browse/shop.orders", Token), "X-Frame-Options").ShouldBe("DENY");
   }

   [Fact]
   public void TheFrameAncestorsSettingIsChecked()
   {
      PublicDashboards.AllowedAncestors("*").ShouldBeNull();
      PublicDashboards.AllowedAncestors(" https://A.example.com  http://localhost:4200 ").ShouldBe(["https://a.example.com", "http://localhost:4200"]);
      Should.Throw<FormatException>(() => PublicDashboards.AllowedAncestors("https://x.org; script-src *"));
      Should.Throw<FormatException>(() => PublicDashboards.AllowedAncestors("'self'"));
      SecurityHeaders.Framed(SecuritySettings.DefaultContentSecurityPolicy, "*").ShouldEndWith("; frame-ancestors *");
      SecurityHeaders.Framed("default-src 'self'", "'self' https://a.org").ShouldBe("default-src 'self'; frame-ancestors 'self' https://a.org");
      SecurityHeaders.Framed(null, "*").ShouldBe("frame-ancestors *");
   }

   [Fact]
   public void TokensInPathsAreMaskedInTheLog()
   {
      SecretRedactor redactor = new();
      redactor.Redact("Request starting HTTP/1.1 GET http://localhost/embed/AbCdEfGhIjKlMnOpQrSt_- - -")
         .ShouldBe("Request starting HTTP/1.1 GET http://localhost/embed/******** - -");
      redactor.Redact("GET /api/public/dashboards/AbCdEfGhIjKlMnOpQrSt_-/widgets/by-state/data").ShouldBe("GET /api/public/dashboards/********/widgets/by-state/data");
      redactor.Redact("GET /api/dashboards/12/widgets/by-state/data").ShouldBe("GET /api/dashboards/12/widgets/by-state/data");
   }

   [Fact]
   public async Task ADashboardsQueriesRunAFewAtATimeForAllItsViewers()
   {
      using PublicDashboards dashboards = new(Options.Create(new GalaxyDataOptions { Dashboards = new DashboardSettings { PublicQueriesPerDashboard = 1 } }));
      IAsyncDisposable first = await dashboards.GateAsync(7, Token);
      using CancellationTokenSource waited = new(TimeSpan.FromMilliseconds(100));
      await Should.ThrowAsync<OperationCanceledException>(() => dashboards.GateAsync(7, waited.Token));
      await using (await dashboards.GateAsync(8, Token)) { }
      await first.DisposeAsync();
      await first.DisposeAsync();
      await using IAsyncDisposable second = await dashboards.GateAsync(7, Token);
   }
}
