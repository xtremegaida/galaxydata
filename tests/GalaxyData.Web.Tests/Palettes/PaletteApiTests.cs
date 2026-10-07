using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using GalaxyData.Web.Dashboards;
using GalaxyData.Web.Palettes;
using GalaxyData.Web.Problems;
using GalaxyData.Web.Tests.Dashboards;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Palettes;

public sealed class PaletteApiTests
{
   private static async Task<TestApi> UserAsync(WebAppFactory factory, TestApi admin, string name, string role = "read")
   {
      await admin.CreateUserAsync(name, role, "first-password-of-a-user");
      return await TestApi.SignedInAsync(factory, name, "first-password-of-a-user", changeTo: "second-password-of-a-user");
   }

   private static JsonElement Json(PaletteDefinition definition) => JsonSerializer.SerializeToElement(definition, DefinitionJson.Options);

   private static async Task<JsonElement> CreateAsync(TestApi api, string name, PaletteDefinition? definition = null) =>
      await (await api.PostAsync("/api/palettes", new { name, description = (string?)null, definition = Json(definition ?? PaletteRulesTests.Statuses()) }))
         .JsonAsync(HttpStatusCode.Created);

   private static Task<HttpResponseMessage> SaveAsync(TestApi api, JsonElement palette, PaletteDefinition definition, string? name = null) =>
      api.PutAsync($"/api/palettes/{palette.GetProperty("id").GetInt32()}", new
      {
         name = name ?? palette.GetProperty("name").GetString(),
         description = (string?)null,
         definition = Json(definition),
         version = palette.GetProperty("version").GetInt32(),
      });

   private static async Task<JsonElement> DashboardAsync(TestApi api, string name, DashboardDefinition definition) =>
      await (await api.PostAsync("/api/dashboards", new { name, definition = Definitions.Json(definition) })).JsonAsync(HttpStatusCode.Created);

   private static async Task<JsonElement> DashboardSavedAsync(TestApi api, JsonElement dashboard, DashboardDefinition definition) =>
      await (await api.PutAsync($"/api/dashboards/{dashboard.GetProperty("id").GetInt32()}", new
      {
         name = dashboard.GetProperty("name").GetString(),
         description = (string?)null,
         definition = Definitions.Json(definition),
         version = dashboard.GetProperty("version").GetInt32(),
      })).JsonAsync(HttpStatusCode.OK);

   private static async Task<JsonElement> DashboardPostAsync(TestApi api, JsonElement dashboard, string action) =>
      await (await api.PostAsync($"/api/dashboards/{dashboard.GetProperty("id").GetInt32()}/{action}", new { version = dashboard.GetProperty("version").GetInt32() }))
         .JsonAsync(HttpStatusCode.OK);

   private static async Task<int> UsedByAsync(TestApi api, int palette) =>
      (await (await api.GetAsync($"/api/palettes/{palette}")).JsonAsync(HttpStatusCode.OK)).GetProperty("usedBy").GetInt32();

   /// <summary>The sales dashboard with <paramref name="palette"/> for its charts, and the pie's own <paramref name="pie"/>.</summary>
   private static DashboardDefinition Using(int? palette, int? pie = null)
   {
      DashboardDefinition sales = Definitions.Sales();
      return sales with
      {
         Palette = palette,
         Widgets = [.. sales.Widgets.Select(w => w.Config is PieConfig config ? w with { Config = config with { Palette = pie } } : w)],
      };
   }

   [Fact]
   public async Task EveryoneSeesEveryPaletteAndOwnersChangeTheirs()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      TestApi lee = await UserAsync(factory, admin, "lee");
      TestApi kim = await UserAsync(factory, admin, "kim");

      JsonElement statuses = await CreateAsync(lee, " Statuses ");
      (statuses.GetProperty("name").GetString(), statuses.GetProperty("isMine").GetBoolean(), statuses.GetProperty("canEdit").GetBoolean(), statuses.GetProperty("usedBy").GetInt32())
         .ShouldBe(("Statuses", true, true, 0));
      statuses.GetProperty("definition").GetProperty("colors")[0].GetProperty("light").GetString().ShouldBe("#2a78d6", "kept in lower case");
      int id = statuses.GetProperty("id").GetInt32();
      await (await lee.PostAsync("/api/palettes", new { name = "statuses", definition = Json(PaletteRulesTests.Statuses()) })).ProblemAsync(409, ProblemCodes.PaletteNameTaken);
      (await CreateAsync(kim, "Statuses")).GetProperty("owner").GetString().ShouldBe("kim", "names are each owner's");

      JsonElement listed = await (await kim.GetAsync("/api/palettes")).JsonAsync(HttpStatusCode.OK);
      listed.EnumerateArray().Select(p => $"{p.GetProperty("name").GetString()} ({p.GetProperty("owner").GetString()}, {p.GetProperty("canEdit").GetBoolean()}, {p.GetProperty("overrides").GetInt32()})")
         .ShouldBe(["Statuses (kim, True, 2)", "Statuses (lee, False, 2)"]);
      listed[1].TryGetProperty("definition", out _).ShouldBeFalse("lists say no labels");
      JsonElement asKim = await (await kim.GetAsync($"/api/palettes/{id}")).JsonAsync(HttpStatusCode.OK);
      (asKim.GetProperty("isMine").GetBoolean(), asKim.GetProperty("canEdit").GetBoolean()).ShouldBe((false, false));
      await (await SaveAsync(kim, asKim, PaletteRulesTests.Statuses())).ProblemAsync(403, ProblemCodes.Forbidden);
      await (await kim.DeleteAsync($"/api/palettes/{id}")).ProblemAsync(403, ProblemCodes.Forbidden);
      await (await kim.GetAsync("/api/palettes/999")).ProblemAsync(404, ProblemCodes.NotFound);

      // Saving nothing new changes nothing; a change to the version read, once.
      JsonElement same = await (await SaveAsync(lee, statuses, PaletteRulesTests.Statuses())).JsonAsync(HttpStatusCode.OK);
      same.GetProperty("version").GetInt32().ShouldBe(0);
      PaletteDefinition byOrder = PaletteRulesTests.Statuses() with { Assign = PaletteAssign.Order };
      JsonElement changed = await (await SaveAsync(lee, statuses, byOrder)).JsonAsync(HttpStatusCode.OK);
      (changed.GetProperty("version").GetInt32(), changed.GetProperty("hash").GetString()).ShouldNotBe((0, statuses.GetProperty("hash").GetString()));
      await (await SaveAsync(lee, statuses, byOrder)).ProblemAsync(409, ProblemCodes.ConcurrencyConflict);
      JsonElement refused = (await (await SaveAsync(lee, changed, byOrder with { Colors = [new("red", null)] })).ProblemAsync(400, ProblemCodes.InvalidRequest))
         .GetProperty("errors");
      refused.EnumerateObject().Select(e => e.Name).ShouldBe(["definition.colors[0].light"]);

      // Administrators change and delete others' (said in the audit), but don't rename them.
      JsonElement asAdmin = await (await admin.GetAsync($"/api/palettes/{id}")).JsonAsync(HttpStatusCode.OK);
      asAdmin.GetProperty("canEdit").GetBoolean().ShouldBeTrue();
      await (await SaveAsync(admin, asAdmin, byOrder, name: "Mine now")).ProblemAsync(403, ProblemCodes.Forbidden);
      JsonElement edited = await (await SaveAsync(admin, asAdmin, byOrder with { Distinct = false })).JsonAsync(HttpStatusCode.OK);
      edited.GetProperty("owner").GetString().ShouldBe("lee");
      await (await admin.DeleteAsync($"/api/palettes/{id}?version=0")).ProblemAsync(409, ProblemCodes.ConcurrencyConflict);
      (await admin.DeleteAsync($"/api/palettes/{id}?version={edited.GetProperty("version").GetInt32()}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
      await (await lee.GetAsync($"/api/palettes/{id}")).ProblemAsync(404, ProblemCodes.NotFound);
      JsonElement audit = await (await admin.GetAsync("/api/audit/admin-events")).JsonAsync(HttpStatusCode.OK);
      audit.EnumerateArray().Select(e => e.GetProperty("action").GetString()!).Where(a => a.StartsWith("palette.", System.StringComparison.Ordinal))
         .ShouldBe(["palette.deleted", "palette.updated"]);
      audit.GetRawText().ShouldNotContain("Open", Case.Sensitive, "no labels in the audit");
   }

   [Fact]
   public async Task APaletteKnowsTheDashboardsUsingIt()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      TestApi lee = await UserAsync(factory, admin, "lee");
      TestApi kim = await UserAsync(factory, admin, "kim");
      int statuses = (await CreateAsync(lee, "Statuses")).GetProperty("id").GetInt32();
      int cities = (await CreateAsync(lee, "Cities")).GetProperty("id").GetInt32();

      JsonElement sales = await DashboardAsync(lee, "Sales", Using(statuses, pie: cities));
      (await UsedByAsync(lee, statuses), await UsedByAsync(lee, cities)).ShouldBe((1, 1));
      sales.GetProperty("palettes").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ShouldBe(["Statuses", "Cities"]);
      sales.GetProperty("palettes")[0].GetProperty("definition").GetProperty("overrides").GetArrayLength().ShouldBe(2, "whole, signed in");
      JsonElement used = await (await kim.GetAsync($"/api/palettes/{statuses}")).JsonAsync(HttpStatusCode.OK);
      (used.GetProperty("usedBy").GetInt32(), used.GetProperty("dashboards").GetArrayLength()).ShouldBe((1, 0), "kim doesn't see lee's private dashboard, only that it is used");
      (await (await lee.GetAsync($"/api/palettes/{statuses}")).JsonAsync(HttpStatusCode.OK)).GetProperty("dashboards")[0].GetProperty("name").GetString().ShouldBe("Sales");

      // The working copy no longer names Cities; the published one still names Statuses when the working copy doesn't.
      JsonElement published = await DashboardPostAsync(lee, sales, "publish");
      JsonElement saved = await DashboardSavedAsync(lee, published, Using(null));
      (await UsedByAsync(lee, statuses), await UsedByAsync(lee, cities)).ShouldBe((1, 1), "published, it names both still");
      JsonElement republished = await DashboardPostAsync(lee, saved, "publish");
      (await UsedByAsync(lee, statuses), await UsedByAsync(lee, cities)).ShouldBe((0, 0));
      JsonElement restored = await (await lee.PostAsync($"/api/dashboards/{sales.GetProperty("id").GetInt32()}/revisions/1/restore",
         new { version = republished.GetProperty("version").GetInt32() })).JsonAsync(HttpStatusCode.OK);
      (await UsedByAsync(lee, statuses), await UsedByAsync(lee, cities)).ShouldBe((1, 1), "revision 1 named them");
      JsonElement discarded = await DashboardPostAsync(lee, restored, "discard");
      (await UsedByAsync(lee, statuses)).ShouldBe(0);
      await (await lee.PostAsync($"/api/dashboards/{sales.GetProperty("id").GetInt32()}/revisions/1/restore", new { version = discarded.GetProperty("version").GetInt32() }))
         .JsonAsync(HttpStatusCode.OK);
      await (await lee.PostAsync($"/api/dashboards/{sales.GetProperty("id").GetInt32()}/copy", new { name = "Sales again" })).JsonAsync(HttpStatusCode.Created);
      (await UsedByAsync(lee, statuses)).ShouldBe(2, "the copy too");
      (await lee.DeleteAsync($"/api/dashboards/{sales.GetProperty("id").GetInt32()}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
      (await UsedByAsync(lee, statuses)).ShouldBe(1);

      // A palette that isn't there is the dashboard's issue, not a refusal; deleted, a palette's uses go with it.
      JsonElement dangling = await DashboardAsync(lee, "Dangling", Using(999, pie: cities));
      dangling.GetProperty("palettes").EnumerateArray().Select(p => p.GetProperty("id").GetInt32()).ShouldBe([cities]);
      (await lee.DeleteAsync($"/api/palettes/{cities}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
      JsonElement read = await (await lee.GetAsync($"/api/dashboards/{dangling.GetProperty("id").GetInt32()}")).JsonAsync(HttpStatusCode.OK);
      read.GetProperty("issues").EnumerateArray().Where(i => i.GetProperty("field").GetString()?.EndsWith("palette", System.StringComparison.Ordinal) == true)
         .Select(i => $"{i.GetProperty("severity").GetString()} {i.GetProperty("widget")} {i.GetProperty("field").GetString()}")
         .ShouldBe(["warning  palette", "warning by-city widgets[2].config.palette"]);
      (await (await lee.GetAsync($"/api/dashboards/{dangling.GetProperty("id").GetInt32()}/palettes")).JsonAsync(HttpStatusCode.OK)).GetArrayLength().ShouldBe(0);
      int again = (await CreateAsync(lee, "Cities")).GetProperty("id").GetInt32();
      again.ShouldNotBe(cities, "ids aren't used again, so a dangling reference finds no new palette");
      (await UsedByAsync(lee, again)).ShouldBe(0);
   }

   [Fact]
   public async Task DefinitionsWithoutPalettesAreWrittenAsTheyWere()
   {
      // As D9 wrote them: no palette, no colorBy.
      string before = DefinitionJson.Write(Definitions.Sales());
      before.ShouldNotContain("palette");
      before.ShouldNotContain("colorBy");
      DefinitionJson.Write(DefinitionJson.Read(before)).ShouldBe(before);
      string with = DefinitionJson.Write(Using(3, pie: 4) with { Widgets = [.. Using(3).Widgets.Select(w => w.Config is BarConfig b ? w with { Config = b with { ColorBy = BarColorBy.Category } } : w)] });
      with.ShouldContain("\"palette\":3");
      with.ShouldContain("\"colorBy\":\"category\"");

      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      JsonElement dashboard = await DashboardAsync(admin, "Sales", Definitions.Sales());
      JsonElement published = await DashboardPostAsync(admin, dashboard, "publish");
      JsonElement saved = await DashboardSavedAsync(admin, published, Definitions.Sales());
      (saved.GetProperty("version").GetInt32(), saved.GetProperty("hasUnpublishedChanges").GetBoolean()).ShouldBe((published.GetProperty("version").GetInt32(), false));
      saved.GetProperty("working").TryGetProperty("palette", out _).ShouldBeFalse();
   }
}
