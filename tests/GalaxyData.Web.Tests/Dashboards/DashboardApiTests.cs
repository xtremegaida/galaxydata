using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using GalaxyData.Web.Dashboards;
using GalaxyData.Web.Problems;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Dashboards;

public sealed class DashboardApiTests
{
   private static async Task<(TestApi Api, int Id)> UserAsync(WebAppFactory factory, TestApi admin, string name, string role = "read")
   {
      int id = await admin.CreateUserAsync(name, role, "first-password-of-a-user");
      return (await TestApi.SignedInAsync(factory, name, "first-password-of-a-user", changeTo: "second-password-of-a-user"), id);
   }

   private static async Task<JsonElement> CreateAsync(TestApi api, string name, DashboardDefinition? definition = null) =>
      await (await api.PostAsync("/api/dashboards", new { name, definition = definition == null ? (JsonElement?)null : Definitions.Json(definition) }))
         .JsonAsync(HttpStatusCode.Created);

   private static async Task<JsonElement> SaveAsync(TestApi api, JsonElement dashboard, DashboardDefinition definition, string? name = null) =>
      await (await api.PutAsync($"/api/dashboards/{dashboard.GetProperty("id").GetInt32()}", new
      {
         name = name ?? dashboard.GetProperty("name").GetString(),
         description = (string?)null,
         definition = Definitions.Json(definition),
         version = dashboard.GetProperty("version").GetInt32(),
      })).JsonAsync(HttpStatusCode.OK);

   private static async Task<JsonElement> PostAsync(TestApi api, JsonElement dashboard, string action, object? body = null) =>
      await (await api.PostAsync($"/api/dashboards/{dashboard.GetProperty("id").GetInt32()}/{action}", body ?? new { version = dashboard.GetProperty("version").GetInt32() }))
         .JsonAsync(HttpStatusCode.OK);

   private static async Task<List<string>> ListedAsync(TestApi api) =>
      [.. (await (await api.GetAsync("/api/dashboards")).JsonAsync(HttpStatusCode.OK)).EnumerateArray()
         .Select(d => $"{d.GetProperty("name").GetString()} ({d.GetProperty("owner").GetString()}, {d.GetProperty("sharing").GetString()}"
                      + $"{(d.GetProperty("isPublic").GetBoolean() ? ", public" : string.Empty)}{(d.GetProperty("hasUnpublishedChanges").GetBoolean() ? ", changed" : string.Empty)})")];

   private static string Status(JsonElement dashboard) =>
      $"working: {dashboard.GetProperty("working").ValueKind != JsonValueKind.Null}, published: {dashboard.GetProperty("publishedNumber")}, changed: {dashboard.GetProperty("hasUnpublishedChanges").GetBoolean()}";

   [Fact]
   public async Task OthersSeeWhatIsPublishedOnceItIsSharedWithThem()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      (TestApi lee, _) = await UserAsync(factory, admin, "lee");
      (TestApi kim, int kimId) = await UserAsync(factory, admin, "kim");
      (TestApi sam, _) = await UserAsync(factory, admin, "sam");

      JsonElement blank = await CreateAsync(lee, " Sales ");
      (blank.GetProperty("name").GetString(), Status(blank)).ShouldBe(("Sales", "working: True, published: , changed: True"));
      blank.GetProperty("working").GetProperty("widgets").GetArrayLength().ShouldBe(0, "a blank page");
      int id = blank.GetProperty("id").GetInt32();
      JsonElement saved = await SaveAsync(lee, blank, Definitions.Sales());
      saved.GetProperty("version").GetInt32().ShouldBe(1);
      saved.GetProperty("working").GetProperty("widgets")[1].GetProperty("config").GetProperty("kind").GetString().ShouldBe("bar");

      // Shared before it is published: still no one's to see.
      JsonElement shared = await (await lee.PutAsync($"/api/dashboards/{id}/sharing", new { everyone = false, users = new[] { kimId }, version = 1 })).JsonAsync(HttpStatusCode.OK);
      shared.GetProperty("sharing").GetProperty("users")[0].GetProperty("userName").GetString().ShouldBe("kim");
      (await ListedAsync(kim)).ShouldBeEmpty("it isn't published");
      await (await kim.GetAsync($"/api/dashboards/{id}")).ProblemAsync(404, ProblemCodes.NotFound);

      JsonElement published = await PostAsync(lee, shared, "publish", new { version = shared.GetProperty("version").GetInt32(), note = " First " });
      Status(published).ShouldBe("working: True, published: 1, changed: False");
      (await ListedAsync(kim)).ShouldBe(["Sales (lee, chosen)"]);
      (await ListedAsync(sam)).ShouldBeEmpty("shared with kim alone");
      JsonElement asKim = await (await kim.GetAsync($"/api/dashboards/{id}")).JsonAsync(HttpStatusCode.OK);
      Status(asKim).ShouldBe("working: False, published: 1, changed: False");
      asKim.GetProperty("sharing").ValueKind.ShouldBe(JsonValueKind.Null, "who else sees it is the owner's business");
      asKim.GetProperty("can").GetRawText().ShouldBe("""{"edit":false,"publish":false,"share":false,"makePublic":false,"revokePublic":false,"delete":false,"copy":true}""");

      // The owner's changes are theirs until published.
      DashboardDefinition changed = Definitions.Sales() with { Widgets = [.. Definitions.Sales().Widgets.Take(2)], Layout = Definitions.Sales().Layout with
      {
         Items = new Dictionary<string, Placement> { ["title"] = new(0, 0, 12, 1), ["by-status"] = new(0, 1, 12, 6) },
      } };
      JsonElement edited = await SaveAsync(lee, published, changed);
      Status(edited).ShouldBe("working: True, published: 1, changed: True");
      (await ListedAsync(lee)).ShouldBe(["Sales (lee, chosen, changed)"]);
      (await (await kim.GetAsync($"/api/dashboards/{id}")).JsonAsync(HttpStatusCode.OK)).GetProperty("published").GetProperty("widgets").GetArrayLength().ShouldBe(3);
      await (await kim.PutAsync($"/api/dashboards/{id}", new { name = "Mine", definition = Definitions.Json(changed), version = edited.GetProperty("version").GetInt32() }))
         .ProblemAsync(403, ProblemCodes.Forbidden);
      await (await kim.PostAsync($"/api/dashboards/{id}/publish", new { version = edited.GetProperty("version").GetInt32() })).ProblemAsync(403, ProblemCodes.Forbidden);
      await (await kim.GetAsync($"/api/dashboards/{id}/revisions")).ProblemAsync(403, ProblemCodes.Forbidden);
      await (await kim.DeleteAsync($"/api/dashboards/{id}")).ProblemAsync(403, ProblemCodes.Forbidden);

      // A copy of what kim sees: the published copy, kim's own.
      JsonElement copy = await (await kim.PostAsync($"/api/dashboards/{id}/copy", new { name = "Sales" })).JsonAsync(HttpStatusCode.Created);
      (copy.GetProperty("owner").GetString(), copy.GetProperty("working").GetProperty("widgets").GetArrayLength(), Status(copy))
         .ShouldBe(("kim", 3, "working: True, published: , changed: True"));
      (await ListedAsync(kim)).ShouldBe(["Sales (kim, private, changed)", "Sales (lee, chosen)"]);

      // Everyone, then no one.
      await (await lee.PutAsync($"/api/dashboards/{id}/sharing", new { everyone = true, users = new int[0], version = edited.GetProperty("version").GetInt32() }))
         .JsonAsync(HttpStatusCode.OK);
      (await ListedAsync(sam)).ShouldBe(["Sales (lee, everyone)"]);
   }

   [Fact]
   public async Task AdministratorsTakeSharedDashboardsBackButDontShareThemWider()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      (TestApi lee, _) = await UserAsync(factory, admin, "lee");
      (_, int kimId) = await UserAsync(factory, admin, "kim");
      (_, int samId) = await UserAsync(factory, admin, "sam");
      JsonElement mine = await CreateAsync(lee, "Private");
      JsonElement shared = await CreateAsync(lee, "Shared", Definitions.Sales());
      int id = shared.GetProperty("id").GetInt32();
      shared = await (await lee.PutAsync($"/api/dashboards/{id}/sharing", new { everyone = false, users = new[] { kimId, samId }, version = 0 })).JsonAsync(HttpStatusCode.OK);

      (await ListedAsync(admin)).ShouldBe(["Shared (lee, chosen)"], "unpublished, but shared: administrators look after it (lee's changes are lee's)");
      await (await admin.GetAsync($"/api/dashboards/{mine.GetProperty("id").GetInt32()}")).ProblemAsync(404, ProblemCodes.NotFound);
      JsonElement asAdmin = await (await admin.GetAsync($"/api/dashboards/{id}")).JsonAsync(HttpStatusCode.OK);
      asAdmin.GetProperty("working").ValueKind.ShouldBe(JsonValueKind.Null, "the working copy is lee's");
      asAdmin.GetProperty("can").GetProperty("share").GetBoolean().ShouldBeTrue();
      await (await admin.PutAsync($"/api/dashboards/{id}", new { name = "Taken", definition = Definitions.Json(Definitions.Blank()), version = 1 }))
         .ProblemAsync(403, ProblemCodes.Forbidden);

      int version = shared.GetProperty("version").GetInt32();
      await (await admin.PutAsync($"/api/dashboards/{id}/sharing", new { everyone = true, users = new[] { kimId }, version })).ProblemAsync(403, ProblemCodes.Forbidden);
      JsonElement fewer = await (await admin.PutAsync($"/api/dashboards/{id}/sharing", new { everyone = false, users = new[] { kimId }, version })).JsonAsync(HttpStatusCode.OK);
      fewer.GetProperty("sharing").GetProperty("users").GetArrayLength().ShouldBe(1);
      await (await admin.PutAsync($"/api/dashboards/{id}/sharing", new { everyone = false, users = new int[0], version })).ProblemAsync(409, ProblemCodes.ConcurrencyConflict);
      JsonElement none = await (await admin.PutAsync($"/api/dashboards/{id}/sharing", new { everyone = false, users = new int[0], version = fewer.GetProperty("version").GetInt32() }))
         .JsonAsync(HttpStatusCode.OK);
      (await ListedAsync(admin)).ShouldBeEmpty("private again");

      JsonElement errors = (await (await lee.PutAsync($"/api/dashboards/{id}/sharing", new
      {
         everyone = false,
         users = new[] { 999, kimId, kimId, 2 },
         version = none.GetProperty("version").GetInt32(),
      }))
         .ProblemAsync(400, ProblemCodes.InvalidRequest)).GetProperty("errors");
      errors.EnumerateObject().Select(e => e.Name).ShouldBe(["users[0]", "users[2]", "users[3]"], ignoreOrder: true, "an unknown user, kim twice, and lee, the owner");
   }

   [Fact]
   public async Task PublishingNumbersRevisionsWhichComeBack()
   {
      await using WebAppFactory factory = new() { Settings = new Dictionary<string, string?> { ["GalaxyData:Dashboards:KeepRevisions"] = "2" } };
      TestApi admin = await TestApi.SignedInAsync(factory);
      JsonElement dashboard = await CreateAsync(admin, "Sales", Definitions.Sales());
      int id = dashboard.GetProperty("id").GetInt32();
      await (await admin.PostAsync($"/api/dashboards/{id}/discard", new { version = 0 })).ProblemAsync(409, ProblemCodes.Conflict);

      dashboard = await PostAsync(admin, dashboard, "publish");
      JsonElement again = await PostAsync(admin, dashboard, "publish");
      (again.GetProperty("publishedNumber").GetInt32(), again.GetProperty("version").GetInt32()).ShouldBe((1, dashboard.GetProperty("version").GetInt32()), "nothing new to publish");
      DashboardDefinition blank = Definitions.Blank();
      dashboard = await PostAsync(admin, await SaveAsync(admin, dashboard, blank), "publish");
      dashboard = await PostAsync(admin, await SaveAsync(admin, dashboard, Definitions.Sales() with { Refresh = new RefreshPolicy(RefreshMode.Interval, 60) }), "publish");
      dashboard.GetProperty("publishedNumber").GetInt32().ShouldBe(3);

      JsonElement revisions = await (await admin.GetAsync($"/api/dashboards/{id}/revisions")).JsonAsync(HttpStatusCode.OK);
      revisions.EnumerateArray().Select(r => (r.GetProperty("number").GetInt32(), r.GetProperty("isPublished").GetBoolean())).ShouldBe([(3, true), (2, false)], "the newest two");
      await (await admin.GetAsync($"/api/dashboards/{id}/revisions/1")).ProblemAsync(404, ProblemCodes.NotFound);
      JsonElement second = await (await admin.GetAsync($"/api/dashboards/{id}/revisions/2")).JsonAsync(HttpStatusCode.OK);
      second.GetProperty("definition").GetProperty("widgets").GetArrayLength().ShouldBe(0);

      JsonElement restored = await PostAsync(admin, dashboard, "revisions/2/restore");
      (Status(restored), restored.GetProperty("working").GetProperty("widgets").GetArrayLength()).ShouldBe(("working: True, published: 3, changed: True", 0));
      JsonElement discarded = await PostAsync(admin, restored, "discard");
      (Status(discarded), discarded.GetProperty("working").GetProperty("refresh").GetProperty("seconds").GetInt32()).ShouldBe(("working: True, published: 3, changed: False", 60));
   }

   [Fact]
   public async Task SavingNothingNewChangesNothing()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      JsonElement dashboard = await CreateAsync(admin, "Sales", Definitions.Sales());
      JsonElement same = await SaveAsync(admin, dashboard, Definitions.Sales());
      same.GetProperty("version").GetInt32().ShouldBe(0);
      JsonElement asRead = await (await admin.PutAsync($"/api/dashboards/{dashboard.GetProperty("id").GetInt32()}", new
      {
         name = "Sales",
         definition = dashboard.GetProperty("working"),
         version = 0,
      })).JsonAsync(HttpStatusCode.OK);
      (asRead.GetProperty("version").GetInt32(), asRead.GetProperty("workingHash").GetString()).ShouldBe((0, dashboard.GetProperty("workingHash").GetString()), "saved as it was read");
      await (await admin.PutAsync($"/api/dashboards/{dashboard.GetProperty("id").GetInt32()}", new { name = "Sales 2", definition = Definitions.Json(Definitions.Blank()), version = 3 }))
         .ProblemAsync(409, ProblemCodes.ConcurrencyConflict);
      await CreateAsync(admin, "Other");
      await (await admin.PostAsync("/api/dashboards", new { name = "SALES" })).ProblemAsync(409, ProblemCodes.DashboardNameTaken);
      await (await admin.PutAsync($"/api/dashboards/{dashboard.GetProperty("id").GetInt32()}", new { name = "other", definition = Definitions.Json(Definitions.Blank()), version = 0 }))
         .ProblemAsync(409, ProblemCodes.DashboardNameTaken);
   }

   [Fact]
   public async Task DefinitionsAreRefusedByField()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      DashboardDefinition sales = Definitions.Sales();
      JsonElement errors = (await (await admin.PostAsync("/api/dashboards", new
      {
         name = "Odd",
         definition = Definitions.Json(sales with { Sources = [sales.Sources[0]] }),
      })).ProblemAsync(400, ProblemCodes.InvalidRequest)).GetProperty("errors");
      errors.EnumerateObject().Select(e => e.Name).ShouldBe(["definition.links[0].to", "definition.widgets[2].config.source"], ignoreOrder: true);
      (await (await admin.PostAsync("/api/dashboards", new { name = " " })).ProblemAsync(400, ProblemCodes.InvalidRequest))
         .GetProperty("errors").EnumerateObject().Select(e => e.Name).ShouldBe(["name"]);

      JsonElement unknownKind = Definitions.Json(sales);
      string json = unknownKind.GetRawText().Replace("\"kind\":\"pie\"", "\"kind\":\"map\"", System.StringComparison.Ordinal);
      System.Net.Http.HttpResponseMessage refused = await admin.PostAsync("/api/dashboards", new { name = "Map", definition = JsonDocument.Parse(json).RootElement });
      refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
   }

   [Fact]
   public async Task DataManagersMakeTheirDashboardsPublic()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      (TestApi reader, _) = await UserAsync(factory, admin, "lee");
      (TestApi manager, int managerId) = await UserAsync(factory, admin, "kim", "dataManager");

      JsonElement readers = await CreateAsync(reader, "Mine", Definitions.Sales());
      readers.GetProperty("can").GetProperty("makePublic").GetBoolean().ShouldBeFalse();
      await (await reader.PutAsync($"/api/dashboards/{readers.GetProperty("id").GetInt32()}/public", new { enabled = true, version = 0 })).ProblemAsync(403, ProblemCodes.Forbidden);

      JsonElement dashboard = await CreateAsync(manager, "Sales", Definitions.Sales());
      int id = dashboard.GetProperty("id").GetInt32();
      JsonElement refused = (await (await manager.PutAsync($"/api/dashboards/{id}/public", new
      {
         enabled = true,
         origins = new[] { "https://Example.COM/", "ftp://example.com", "https://*.example.org:8443", "https://example.com" },
         version = 0,
      })).ProblemAsync(400, ProblemCodes.InvalidRequest)).GetProperty("errors");
      refused.EnumerateObject().Select(e => e.Name).ShouldBe(["origins[1]"]);
      JsonElement made = await (await manager.PutAsync($"/api/dashboards/{id}/public", new
      {
         enabled = true,
         origins = new[] { "https://Example.COM/", "https://*.example.org:8443", "https://example.com" },
         version = 0,
      })).JsonAsync(HttpStatusCode.OK);
      JsonElement link = made.GetProperty("public");
      string token = link.GetProperty("token").GetString()!;
      token.ShouldMatch("^[A-Za-z0-9_-]{22}$");
      link.GetProperty("origins").EnumerateArray().Select(o => o.GetString()).ShouldBe(["https://example.com", "https://*.example.org:8443"]);
      (link.GetProperty("enabledBy").GetString(), link.GetProperty("works").GetBoolean()).ShouldBe(("kim", false), "nothing is published yet");
      made = await PostAsync(manager, made, "publish");
      made.GetProperty("public").GetProperty("works").GetBoolean().ShouldBeTrue();
      (await ListedAsync(admin)).ShouldBe(["Sales (kim, private, public)"], "administrators see public dashboards");

      // Administrators revoke links, and give them new tokens, but don't make others' dashboards public.
      int version = made.GetProperty("version").GetInt32();
      await (await admin.PutAsync($"/api/dashboards/{id}/public", new { enabled = true, origins = new string[0], version })).ProblemAsync(403, ProblemCodes.Forbidden);
      JsonElement regenerated = await PostAsync(admin, made, "public/regenerate");
      regenerated.GetProperty("public").GetProperty("token").GetString().ShouldNotBe(token);
      JsonElement revoked = await (await admin.PutAsync($"/api/dashboards/{id}/public", new { enabled = false, version = regenerated.GetProperty("version").GetInt32() }))
         .JsonAsync(HttpStatusCode.OK);
      revoked.GetProperty("public").ValueKind.ShouldBe(JsonValueKind.Null);
      (await ListedAsync(admin)).ShouldBeEmpty();

      // A link works while whoever made it public may: demoted, it stops.
      JsonElement again = await (await manager.PutAsync($"/api/dashboards/{id}/public", new { enabled = true, version = revoked.GetProperty("version").GetInt32() }))
         .JsonAsync(HttpStatusCode.OK);
      again.GetProperty("public").GetProperty("works").GetBoolean().ShouldBeTrue();
      JsonElement user = await (await admin.GetAsync($"/api/users/{managerId}")).JsonAsync(HttpStatusCode.OK);
      (await admin.PutAsync($"/api/users/{managerId}", new { role = "read", isDisabled = false, version = user.GetProperty("version").GetInt32() }))
         .StatusCode.ShouldBe(HttpStatusCode.OK);
      (await (await admin.GetAsync($"/api/dashboards/{id}")).JsonAsync(HttpStatusCode.OK)).GetProperty("public").GetProperty("works").GetBoolean().ShouldBeFalse();

      JsonElement audit = await (await admin.GetAsync("/api/audit/admin-events")).JsonAsync(HttpStatusCode.OK);
      List<string> actions = [.. audit.EnumerateArray().Select(e => e.GetProperty("action").GetString()!).Where(a => a.StartsWith("dashboard.", System.StringComparison.Ordinal))];
      actions.ShouldBe(["dashboard.public.enabled", "dashboard.public.revoked", "dashboard.public.regenerated", "dashboard.public.enabled"]);
      audit.GetRawText().ShouldNotContain(token);
   }

   [Fact]
   public async Task PublicDashboardsCanBeTurnedOff()
   {
      await using WebAppFactory factory = new() { Settings = new Dictionary<string, string?> { ["GalaxyData:Dashboards:AllowPublic"] = "false" } };
      TestApi admin = await TestApi.SignedInAsync(factory);
      JsonElement dashboard = await CreateAsync(admin, "Sales");
      dashboard.GetProperty("can").GetProperty("makePublic").GetBoolean().ShouldBeFalse();
      await (await admin.PutAsync($"/api/dashboards/{dashboard.GetProperty("id").GetInt32()}/public", new { enabled = true, version = 0 }))
         .ProblemAsync(403, ProblemCodes.PublicDashboardsDisabled);
   }

   /// <summary>A deleted user's dashboards stay, for administrators (and, shared, for everyone); dashboards shared with them forget them.</summary>
   [Fact]
   public async Task ADeletedOwnersDashboardsStay()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      (TestApi lee, int leeId) = await UserAsync(factory, admin, "lee");
      (TestApi kim, int kimId) = await UserAsync(factory, admin, "kim");
      JsonElement leesOwn = await CreateAsync(lee, "Sales", Definitions.Sales());
      JsonElement kims = await CreateAsync(kim, "Kim's", Definitions.Sales());
      kims = await PostAsync(kim, kims, "publish");
      await (await kim.PutAsync($"/api/dashboards/{kims.GetProperty("id").GetInt32()}/sharing", new { everyone = false, users = new[] { leeId }, version = kims.GetProperty("version").GetInt32() }))
         .JsonAsync(HttpStatusCode.OK);
      (await admin.DeleteAsync($"/api/users/{leeId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

      (await ListedAsync(admin)).ShouldBe(["Sales (lee, private, changed)"]);
      int orphan = leesOwn.GetProperty("id").GetInt32();
      JsonElement adopted = await (await admin.GetAsync($"/api/dashboards/{orphan}")).JsonAsync(HttpStatusCode.OK);
      adopted.GetProperty("can").GetProperty("edit").GetBoolean().ShouldBeTrue("administrators look after orphans");
      adopted = await PostAsync(admin, await SaveAsync(admin, adopted, Definitions.Blank()), "publish");
      adopted.GetProperty("publishedBy").GetString().ShouldBe("admin");
      JsonElement kimsNow = await (await kim.GetAsync($"/api/dashboards/{kims.GetProperty("id").GetInt32()}")).JsonAsync(HttpStatusCode.OK);
      kimsNow.GetProperty("sharing").GetProperty("users").GetArrayLength().ShouldBe(0, "lee's share went with lee");
      _ = kimId;
   }

   [Fact]
   public async Task PeopleAreFoundByName()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      (TestApi lee, _) = await UserAsync(factory, admin, "lee");
      await admin.CreateUserAsync("kim_1", "read", "first-password-of-a-user", displayName: "Kim Park");
      int gone = await admin.CreateUserAsync("kimberly", "read", "first-password-of-a-user");
      JsonElement user = await (await admin.GetAsync($"/api/users/{gone}")).JsonAsync(HttpStatusCode.OK);
      (await admin.PutAsync($"/api/users/{gone}", new { role = "read", isDisabled = true, version = user.GetProperty("version").GetInt32() }))
         .StatusCode.ShouldBe(HttpStatusCode.OK);

      async Task<List<string>> FindAsync(string query) =>
         [.. (await (await lee.GetAsync("/api/dashboards/people" + query)).JsonAsync(HttpStatusCode.OK)).EnumerateArray().Select(p => p.GetProperty("userName").GetString()!)];
      (await FindAsync("?text=KIM")).ShouldBe(["kim_1"], "ignoring case; disabled users aren't offered");
      (await FindAsync("?text=park")).ShouldBe(["kim_1"], "by display name too");
      (await FindAsync("?text=_")).ShouldBe(["kim_1"], "_ is a character, not a pattern");
      (await FindAsync("?take=2")).ShouldBe(["admin", "kim_1"]);
      await (await lee.GetAsync("/api/dashboards/people?take=0")).ProblemAsync(400, ProblemCodes.InvalidRequest);
   }
}
