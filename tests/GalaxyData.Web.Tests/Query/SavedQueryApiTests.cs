using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using GalaxyData.Web.Problems;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Query;

public sealed class SavedQueryApiTests
{
   private static async Task<TestApi> UserAsync(WebAppFactory factory, TestApi admin, string name, string role = "read")
   {
      await admin.CreateUserAsync(name, role, "first-password-of-a-user");
      return await TestApi.SignedInAsync(factory, name, "first-password-of-a-user", changeTo: "second-password-of-a-user");
   }

   private static async Task<JsonElement> SaveAsync(TestApi api, object query) =>
      await (await api.PostAsync("/api/saved-queries", query)).JsonAsync(HttpStatusCode.Created);

   private static async Task<List<string>> NamesAsync(TestApi api) =>
      [.. (await (await api.GetAsync("/api/saved-queries")).JsonAsync(HttpStatusCode.OK)).EnumerateArray()
         .Select(q => $"{q.GetProperty("name").GetString()} ({q.GetProperty("owner").GetString()}{(q.GetProperty("canEdit").GetBoolean() ? ", editable" : string.Empty)})")];

   [Fact]
   public async Task QueriesAreTheirOwnersUnlessShared()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      TestApi lee = await UserAsync(factory, admin, "lee");
      TestApi kim = await UserAsync(factory, admin, "kim");

      JsonElement big = await SaveAsync(lee, new
      {
         name = " Big orders ",
         description = "Over the minimum",
         text = "shop.orders.where(total > $min)",
         parameters = new object[] { new { name = "$min", value = 50 }, new { name = "since", type = "date", value = "2026-01-01" } },
      });
      (big.GetProperty("name").GetString(), big.GetProperty("owner").GetString(), big.GetProperty("isMine").GetBoolean()).ShouldBe(("Big orders", "lee", true));
      big.GetProperty("parameters").GetRawText().ShouldBe("""[{"name":"min","type":null,"value":50},{"name":"since","type":"date","value":"2026-01-01"}]""");
      int id = big.GetProperty("id").GetInt32();
      await SaveAsync(lee, new { name = "Mine", text = "shop.customers" });

      (await NamesAsync(lee)).ShouldBe(["Big orders (lee, editable)", "Mine (lee, editable)"]);
      (await NamesAsync(kim)).ShouldBeEmpty("lee's aren't shared");
      (await NamesAsync(admin)).ShouldBeEmpty("nor are they administrators'");
      await (await kim.GetAsync($"/api/saved-queries/{id}")).ProblemAsync(404, ProblemCodes.NotFound);

      JsonElement shared = await (await lee.PutAsync($"/api/saved-queries/{id}", new
      {
         query = new { name = "Big orders", description = "Over the minimum", text = "shop.orders.where(total > $min)", parameters = big.GetProperty("parameters"), isShared = true },
         version = 0,
      })).JsonAsync(HttpStatusCode.OK);
      shared.GetProperty("version").GetInt32().ShouldBe(1);
      (await NamesAsync(kim)).ShouldBe(["Big orders (lee)"]);
      (await NamesAsync(admin)).ShouldBe(["Big orders (lee, editable)"], "administrators tend shared queries");
      JsonElement read = await (await kim.GetAsync($"/api/saved-queries/{id}")).JsonAsync(HttpStatusCode.OK);
      (read.GetProperty("text").GetString(), read.GetProperty("isMine").GetBoolean(), read.GetProperty("canEdit").GetBoolean()).ShouldBe(("shop.orders.where(total > $min)", false, false));
      await (await kim.PutAsync($"/api/saved-queries/{id}", new { query = new { name = "Mine now", text = "shop.orders" }, version = 1 })).ProblemAsync(403, ProblemCodes.Forbidden);
      await (await kim.DeleteAsync($"/api/saved-queries/{id}")).ProblemAsync(403, ProblemCodes.Forbidden);

      // Names are the owner's: another may use one, the owner can't twice (ignoring case).
      await SaveAsync(kim, new { name = "big ORDERS", text = "shop.orders" });
      await (await lee.PostAsync("/api/saved-queries", new { name = "big ORDERS", text = "shop.orders" })).ProblemAsync(409, ProblemCodes.QueryNameTaken);
      await (await lee.PutAsync($"/api/saved-queries/{id}", new { query = new { name = "MINE", text = "x" }, version = 1 })).ProblemAsync(409, ProblemCodes.QueryNameTaken);
      await (await lee.PutAsync($"/api/saved-queries/{id}", new { query = new { name = "Big orders", text = "x" }, version = 0 })).ProblemAsync(409, ProblemCodes.ConcurrencyConflict);

      // An administrator changes a shared query under its name: renaming it would tell the names the owner's others have.
      await (await admin.PutAsync($"/api/saved-queries/{id}", new { query = new { name = "Mine", text = "shop.orders", isShared = true }, version = 1 }))
         .ProblemAsync(403, ProblemCodes.Forbidden);
      JsonElement tidied = await (await admin.PutAsync($"/api/saved-queries/{id}", new { query = new { name = "Big orders", text = "shop.orders.take(10)", isShared = true }, version = 1 }))
         .JsonAsync(HttpStatusCode.OK);
      (tidied.GetProperty("owner").GetString(), tidied.GetProperty("isMine").GetBoolean()).ShouldBe(("lee", false));
   }

   [Fact]
   public async Task SavingNothingNewChangesNothing()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      object query = new { name = "All", text = "shop.orders", parameters = new object[] { new { name = "x", type = "int32", value = 1 }, new { name = "y", value = 50.0 }, new { name = "z", value = 1.50 } } };
      JsonElement saved = await SaveAsync(admin, query);
      int id = saved.GetProperty("id").GetInt32();
      saved.GetProperty("parameters").GetRawText().ShouldBe("""[{"name":"x","type":"int32","value":1},{"name":"y","type":null,"value":50},{"name":"z","type":null,"value":1.5}]""",
         "numbers as a browser writes them back");
      JsonElement again = await (await admin.PutAsync($"/api/saved-queries/{id}", new { query, version = 0 })).JsonAsync(HttpStatusCode.OK);
      again.GetProperty("version").GetInt32().ShouldBe(0);
      JsonElement asRead = await (await admin.PutAsync($"/api/saved-queries/{id}", new
      {
         query = new { name = "All", text = "shop.orders", parameters = saved.GetProperty("parameters") },
         version = 0,
      })).JsonAsync(HttpStatusCode.OK);
      asRead.GetProperty("version").GetInt32().ShouldBe(0, "saved as it was read");
      (await admin.DeleteAsync($"/api/saved-queries/{id}?version=0")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
      await (await admin.GetAsync($"/api/saved-queries/{id}")).ProblemAsync(404, ProblemCodes.NotFound);
   }

   [Fact]
   public async Task RequestsThatArentQueriesAreRefusedByField()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      JsonElement errors = (await (await admin.PostAsync("/api/saved-queries", new
      {
         name = "Odd",
         text = "shop.orders",
         parameters = new object?[] { new { name = "a-b", value = 1 }, new { name = "d", type = "date", value = "soon" }, null },
      })).ProblemAsync(400, ProblemCodes.InvalidRequest)).GetProperty("errors");
      errors.EnumerateObject().Select(e => e.Name).ShouldBe(["parameters[0].name", "parameters[1].value", "parameters[2]"], ignoreOrder: true);
      int id = (await SaveAsync(admin, new { name = "Fine", text = "shop.orders" })).GetProperty("id").GetInt32();
      (await (await admin.PutAsync($"/api/saved-queries/{id}", new { query = new { name = "Fine", text = "shop.orders", parameters = new[] { new { name = "a-b" } } }, version = 0 }))
         .ProblemAsync(400, ProblemCodes.InvalidRequest)).GetProperty("errors").EnumerateObject().Select(e => e.Name).ShouldBe(["query.parameters[0].name"]);
      (await (await admin.PostAsync("/api/saved-queries", new { name = " ", text = "shop.orders" })).ProblemAsync(400, ProblemCodes.InvalidRequest))
         .GetProperty("errors").EnumerateObject().Select(e => e.Name).ShouldBe(["name"]);
   }

   /// <summary>A deleted user's queries stay: shared ones for everyone, the others for administrators to tidy away.</summary>
   [Fact]
   public async Task ADeletedOwnersQueriesStay()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      TestApi lee = await UserAsync(factory, admin, "lee");
      TestApi kim = await UserAsync(factory, admin, "kim");
      await SaveAsync(lee, new { name = "Shared", text = "shop.orders", isShared = true });
      await SaveAsync(lee, new { name = "Private", text = "shop.orders" });
      int leeId = (await (await admin.GetAsync("/api/users")).JsonAsync(HttpStatusCode.OK)).EnumerateArray().Single(u => u.GetProperty("userName").GetString() == "lee")
         .GetProperty("id").GetInt32();
      (await admin.DeleteAsync($"/api/users/{leeId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

      (await NamesAsync(kim)).ShouldBe(["Shared (lee)"]);
      (await NamesAsync(admin)).ShouldBe(["Private (lee, editable)", "Shared (lee, editable)"]);
      // A deleted owner's queries are no one's: names don't clash between them.
      int privateId = (await (await admin.GetAsync("/api/saved-queries")).JsonAsync(HttpStatusCode.OK)).EnumerateArray().First().GetProperty("id").GetInt32();
      (await admin.PutAsync($"/api/saved-queries/{privateId}", new { query = new { name = "Shared", text = "shop.orders" }, version = 0 })).StatusCode.ShouldBe(HttpStatusCode.OK);
      (await NamesAsync(admin)).ShouldBe(["Shared (lee, editable)", "Shared (lee, editable)"]);
      JsonElement orphan = (await (await admin.GetAsync("/api/saved-queries")).JsonAsync(HttpStatusCode.OK)).EnumerateArray().First();
      (await admin.DeleteAsync($"/api/saved-queries/{orphan.GetProperty("id").GetInt32()}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
   }
}
