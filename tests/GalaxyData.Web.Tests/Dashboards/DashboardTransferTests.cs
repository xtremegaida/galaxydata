using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GalaxyData.Web.Dashboards;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Palettes;
using GalaxyData.Web.Problems;
using GalaxyData.Web.Tests.Palettes;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Dashboards;

public sealed class DashboardTransferTests
{
   private static async Task<(TestApi Api, int Id)> UserAsync(WebAppFactory factory, TestApi admin, string name)
   {
      int id = await admin.CreateUserAsync(name, "read", "first-password-of-a-user");
      return (await TestApi.SignedInAsync(factory, name, "first-password-of-a-user", changeTo: "second-password-of-a-user"), id);
   }

   private static async Task<int> PaletteAsync(TestApi api, string name, PaletteDefinition definition) =>
      (await (await api.PostAsync("/api/palettes", new { name, description = $"{name}' colours", definition = JsonSerializer.SerializeToElement(definition, DefinitionJson.Options) }))
         .JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetInt32();

   private static async Task<JsonElement> DashboardAsync(TestApi api, string name, DashboardDefinition definition) =>
      await (await api.PostAsync("/api/dashboards", new { name, description = "Orders and their customers", definition = Definitions.Json(definition) }))
         .JsonAsync(HttpStatusCode.Created);

   /// <summary>The sales dashboard drawn with <paramref name="palette"/>, its pie with <paramref name="pie"/>.</summary>
   private static DashboardDefinition Using(int? palette, int? pie)
   {
      DashboardDefinition sales = Definitions.Sales();
      return sales with
      {
         Palette = palette,
         Widgets = [.. sales.Widgets.Select(w => w.Config is PieConfig config ? w with { Config = config with { Palette = pie } } : w)],
      };
   }

   private static async Task<JsonNode> ExportAsync(TestApi api, int id, string? copy = null)
   {
      HttpResponseMessage response = await api.GetAsync($"/api/dashboards/{id}/export{(copy == null ? string.Empty : "?copy=" + copy)}");
      return JsonNode.Parse((await response.JsonAsync(HttpStatusCode.OK)).GetRawText())!;
   }

   private static Task<HttpResponseMessage> ImportAsync(TestApi api, JsonNode file, string? name = null, object? aliases = null) =>
      api.PostAsync("/api/dashboards/import", new { dashboard = file, name, aliases });

   private static DashboardDefinition DefinitionOf(JsonNode node) => DefinitionJson.Read(node.ToJsonString());

   private static string[] ErrorsOf(JsonElement problem) => [.. problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).Order()];

   [Fact]
   public async Task AFileHoldsACopyTheUserSeesWithThePalettesItNames()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      (TestApi lee, _) = await UserAsync(factory, admin, "lee");
      (TestApi kim, int kimId) = await UserAsync(factory, admin, "kim");
      int statuses = await PaletteAsync(lee, "Statuses", PaletteRulesTests.Statuses());
      int cities = await PaletteAsync(lee, "Cities", PaletteDefinition.New([new("#123456", null)]));
      JsonElement sales = await DashboardAsync(lee, "Sales: Q1 / 2026", Using(statuses, cities) with
      {
         Widgets = [new DashboardWidget("title", null, new TextConfig("# Lee's <sales> & Zoë's")), .. Using(statuses, cities).Widgets.Skip(1)],
      });
      int id = sales.GetProperty("id").GetInt32();

      HttpResponseMessage response = await lee.GetAsync($"/api/dashboards/{id}/export");
      response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
      response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
      response.Content.Headers.ContentDisposition.FileName.ShouldBe("sales-q1-2026.dashboard.json");
      string text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      text.ShouldContain("\n  \"format\": \"galaxydata.dashboard\"", Case.Sensitive, "indented, to compare line by line");
      text.ShouldContain("\"markdown\": \"# Lee's <sales> & Zoë's\"", Case.Sensitive, "text as it is, to read");
      DashboardExport file = DashboardExport.Read(text);
      (file.Format, file.Version, file.Name, file.Description).ShouldBe(("galaxydata.dashboard", 1, "Sales: Q1 / 2026", "Orders and their customers"));
      DefinitionJson.Write(file.Definition).ShouldBe(DefinitionJson.Write(DefinitionJson.Read(sales.GetProperty("working").GetRawText())), "the working copy, as stored");
      file.Palettes.Select(p => (p.Id, p.Name, p.Description)).ShouldBe([(statuses, "Statuses", "Statuses' colours"), (cities, "Cities", "Cities' colours")]);
      file.Palettes[0].Definition.Overrides.Count.ShouldBe(2, "whole");

      // Only those who see the dashboard, and only the copies they see.
      await (await kim.GetAsync($"/api/dashboards/{id}/export")).ProblemAsync(404, ProblemCodes.NotFound);
      await (await lee.GetAsync($"/api/dashboards/{id}/export?copy=published")).ProblemAsync(404, ProblemCodes.NotFound);
      ErrorsOf(await (await lee.GetAsync($"/api/dashboards/{id}/export?copy=draft")).ProblemAsync(400, ProblemCodes.InvalidRequest)).ShouldBe(["copy"]);
      JsonElement published = await (await lee.PostAsync($"/api/dashboards/{id}/publish", new { version = sales.GetProperty("version").GetInt32() })).JsonAsync(HttpStatusCode.OK);
      JsonElement shared = await (await lee.PutAsync($"/api/dashboards/{id}/sharing", new { everyone = false, users = new[] { kimId }, version = published.GetProperty("version").GetInt32() }))
         .JsonAsync(HttpStatusCode.OK);
      await (await lee.PutAsync($"/api/dashboards/{id}", new
      {
         name = "Sales: Q1 / 2026",
         description = (string?)null,
         definition = Definitions.Json(Using(null, null)),
         version = shared.GetProperty("version").GetInt32(),
      })).JsonAsync(HttpStatusCode.OK);
      DefinitionOf((await ExportAsync(lee, id))["definition"]!).Palette.ShouldBeNull("the working copy, changed");
      JsonNode asKim = await ExportAsync(kim, id);
      DefinitionOf(asKim["definition"]!).Palette.ShouldBe(statuses, "kim sees what is published");
      asKim["palettes"]!.AsArray().Count.ShouldBe(2);
      (await ExportAsync(lee, id, "published"))["palettes"]!.AsArray().Count.ShouldBe(2);
      await (await kim.GetAsync($"/api/dashboards/{id}/export?copy=working")).ProblemAsync(404, ProblemCodes.NotFound);
   }

   [Fact]
   public async Task AnImportIsANewDashboardOfTheUsersWithPalettesFoundOrMade()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      (TestApi lee, _) = await UserAsync(factory, admin, "lee");
      (TestApi kim, _) = await UserAsync(factory, admin, "kim");
      int statuses = await PaletteAsync(lee, "Statuses", PaletteRulesTests.Statuses());
      int cities = await PaletteAsync(lee, "Cities", PaletteDefinition.New([new("#123456", null)]));
      int kims = await PaletteAsync(kim, "Cities", PaletteDefinition.New([new("#654321", null)]));
      int id = (await DashboardAsync(lee, "Sales", Using(statuses, cities))).GetProperty("id").GetInt32();
      JsonNode file = await ExportAsync(lee, id);
      JsonElement cityPalette = await (await lee.GetAsync($"/api/palettes/{cities}")).JsonAsync(HttpStatusCode.OK);
      await (await lee.PutAsync($"/api/palettes/{cities}", new
      {
         name = "Cities",
         description = (string?)null,
         definition = JsonSerializer.SerializeToElement(PaletteDefinition.New([new("#abcdef", null)]), DefinitionJson.Options),
         version = cityPalette.GetProperty("version").GetInt32(),
      })).JsonAsync(HttpStatusCode.OK);

      // Statuses is there as it was; Cities isn't (lee's changed, kim's is another), so kim gets one, under a name kim hasn't got.
      JsonElement imported = await (await ImportAsync(kim, file)).JsonAsync(HttpStatusCode.Created);
      (imported.GetProperty("name").GetString(), imported.GetProperty("owner").GetString(), imported.GetProperty("isMine").GetBoolean()).ShouldBe(("Sales", "kim", true));
      (imported.GetProperty("description").GetString(), imported.GetProperty("publishedNumber").ValueKind).ShouldBe(("Orders and their customers", JsonValueKind.Null));
      imported.GetProperty("sharing").GetProperty("everyone").GetBoolean().ShouldBeFalse();
      imported.GetProperty("sharing").GetProperty("users").GetArrayLength().ShouldBe(0);
      DashboardDefinition definition = DefinitionJson.Read(imported.GetProperty("working").GetRawText());
      definition.Palette.ShouldBe(statuses);
      int made = PaletteRefs.Own(definition.Widgets.Single(w => w.Config is PieConfig).Config)!.Value;
      new[] { statuses, cities, kims }.ShouldNotContain(made);
      JsonElement palette = await (await kim.GetAsync($"/api/palettes/{made}")).JsonAsync(HttpStatusCode.OK);
      (palette.GetProperty("name").GetString(), palette.GetProperty("owner").GetString(), palette.GetProperty("description").GetString())
         .ShouldBe(("Cities (2)", "kim", "Cities' colours"));
      palette.GetProperty("definition").GetProperty("colors")[0].GetProperty("light").GetString().ShouldBe("#123456");

      // Again: the palette made is found now; the name is taken.
      await (await ImportAsync(kim, file)).ProblemAsync(409, ProblemCodes.DashboardNameTaken);
      JsonElement again = await (await ImportAsync(kim, file, name: "Sales again")).JsonAsync(HttpStatusCode.Created);
      DashboardDefinition second = DefinitionJson.Read(again.GetProperty("working").GetRawText());
      DefinitionJson.Write(second).ShouldBe(DefinitionJson.Write(definition));
      (await (await kim.GetAsync("/api/palettes")).JsonAsync(HttpStatusCode.OK)).GetArrayLength().ShouldBe(4, "none made twice");

      // Exported again, it is what was imported: the palettes' ids are this instance's.
      JsonNode back = await ExportAsync(kim, again.GetProperty("id").GetInt32());
      DefinitionJson.Write(DefinitionOf(back["definition"]!)).ShouldBe(DefinitionJson.Write(definition));
      DefinitionJson.Write(DefinitionOf(back["definition"]!) with { Palette = statuses, Widgets = DefinitionOf(file["definition"]!).Widgets })
         .ShouldBe(DefinitionJson.Write(DefinitionOf(file["definition"]!)), "but for Cities' id");
   }

   [Fact]
   public async Task AnImportTakesItsSourcesToOtherAliasesAndDropsPalettesNotBrought()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      int statuses = await PaletteAsync(admin, "Statuses", PaletteRulesTests.Statuses());
      JsonElement sales = await DashboardAsync(admin, "Sales", Using(statuses, statuses));
      JsonNode file = await ExportAsync(admin, sales.GetProperty("id").GetInt32());
      file["palettes"] = new JsonArray();

      JsonElement imported = await (await ImportAsync(admin, file, name: "Elsewhere", aliases: new { SHOP = "store" })).JsonAsync(HttpStatusCode.Created);
      DashboardDefinition definition = DefinitionJson.Read(imported.GetProperty("working").GetRawText());
      definition.Sources.Select(s => s.Entity).ShouldBe(["store.orders", "store.customers"]);
      definition.Palette.ShouldBeNull("a palette the file doesn't bring may be another here");
      PaletteRefs.Of(definition).ShouldBeEmpty();
      imported.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("message").GetString()).ShouldContain("There is no entity store.orders in the catalog");

      static string[] Issues(JsonElement dashboard) =>
         [.. dashboard.GetProperty("issues").EnumerateArray().Where(i => i.GetProperty("severity").GetString() == "error").Select(i => i.GetProperty("message").GetString()!)];
      JsonElement same = await (await ImportAsync(admin, file, name: "Same", aliases: new { shop = "shop" })).JsonAsync(HttpStatusCode.Created);
      Issues(same).ShouldBe(Issues(sales), "the same alias, the same entities");
   }

   /// <summary>The files of <c>docs/examples/dashboards</c> are dashboards' files still, as definitions change: canonical, checked, bringing their palettes, imported.</summary>
   [Fact]
   public async Task TheExampleDashboardsImport()
   {
      string[] paths = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "examples", "dashboards"), "*.dashboard.json");
      paths.ShouldNotBeEmpty();
      DefinitionRules rules = new(new WidgetKinds(WidgetKinds.BuiltIn()), new DashboardSettings());
      PaletteRules paletteRules = new(new PaletteSettings());
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      foreach (string path in paths)
      {
         string text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
         DashboardExport file = DashboardExport.Read(text);
         (file.Format, file.Version).ShouldBe((DashboardExport.FileFormat, DashboardExport.CurrentVersion), path);
         Dictionary<string, string[]> errors = [];
         DashboardDefinition? definition = rules.Check(file.Definition, "definition.", errors);
         for (int i = 0; i < file.Palettes.Count; i++) { paletteRules.Check(file.Palettes[i].Definition, $"palettes[{i}].definition.", errors); }
         errors.ShouldBeEmpty(path);
         DefinitionJson.Write(definition!).ShouldBe(DefinitionJson.Write(file.Definition), $"{path} is canonical, as exported");
         PaletteRefs.Of(definition).ShouldBeSubsetOf(file.Palettes.Select(p => p.Id), $"{path} brings every palette it names");
         JsonElement imported = await (await ImportAsync(admin, JsonNode.Parse(text)!)).JsonAsync(HttpStatusCode.Created);
         PaletteRefs.Of(DefinitionJson.Read(imported.GetProperty("working").GetRawText())).Count.ShouldBe(PaletteRefs.Of(definition).Count, path);
      }
   }

   [Fact]
   public async Task FilesAreRefusedByField()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      int id = (await DashboardAsync(admin, "Sales", Definitions.Sales())).GetProperty("id").GetInt32();
      JsonNode file = await ExportAsync(admin, id);

      JsonNode other = file.DeepClone();
      other["format"] = "galaxydata.query";
      ErrorsOf(await (await ImportAsync(admin, other, name: "A")).ProblemAsync(400, ProblemCodes.InvalidRequest)).ShouldBe(["dashboard.format"]);
      JsonNode later = file.DeepClone();
      later["version"] = 2;
      ErrorsOf(await (await ImportAsync(admin, later, name: "A")).ProblemAsync(400, ProblemCodes.InvalidRequest)).ShouldBe(["dashboard.version"]);

      JsonNode wrong = file.DeepClone();
      wrong["definition"]!["sources"]![0]!["entity"] = "not a path!";
      wrong["palettes"] = new JsonArray(new JsonObject { ["id"] = 1, ["name"] = " ", ["definition"] = JsonSerializer.SerializeToNode(PaletteRulesTests.Statuses(), DefinitionJson.Options) },
         new JsonObject { ["id"] = 1, ["name"] = "Two", ["definition"] = JsonSerializer.SerializeToNode(PaletteDefinition.New([new("red", null)]), DefinitionJson.Options) });
      ErrorsOf(await (await ImportAsync(admin, wrong, name: "A", aliases: new { shop = "two parts" })).ProblemAsync(400, ProblemCodes.InvalidRequest))
         .ShouldBe(["aliases.shop", "dashboard.definition.sources[0].entity", "dashboard.palettes[0].name", "dashboard.palettes[1].definition.colors[0].light", "dashboard.palettes[1].id"]);
      ErrorsOf(await (await ImportAsync(admin, file, name: " ")).ProblemAsync(400, ProblemCodes.InvalidRequest)).ShouldBe(["name"]);
      ErrorsOf(await (await admin.PostAsync("/api/dashboards/import", new { name = "A" })).ProblemAsync(400, ProblemCodes.InvalidRequest)).ShouldBe(["dashboard"]);
      (await (await admin.GetAsync("/api/dashboards")).JsonAsync(HttpStatusCode.OK)).GetArrayLength().ShouldBe(1, "nothing made");
      (await (await admin.GetAsync("/api/palettes")).JsonAsync(HttpStatusCode.OK)).GetArrayLength().ShouldBe(0);
   }
}
