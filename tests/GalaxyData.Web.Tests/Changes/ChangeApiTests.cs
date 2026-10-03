using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Problems;
using GalaxyData.Web.Tests.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Changes;

public sealed class ChangeApiTests
{
   private static System.Threading.CancellationToken Token => TestContext.Current.CancellationToken;

   private static async Task<(WebAppFactory Factory, TestApi Admin)> ShopAsync(ManualClock? clock = null)
   {
      WebAppFactory factory = clock == null ? new() : new() { ServiceChanges = services => services.AddSingleton<TimeProvider>(clock) };
      TestApi admin = await TestApi.SignedInAsync(factory);
      await TestSources.AddSqliteAsync(admin, "shop", await TestSources.ShopAsync(factory), readOnly: false);
      return (factory, admin);
   }

   private static string Shop(WebAppFactory factory) => Path.Combine(TestSources.Files(factory), "shop.db");

   private static async Task<TestApi> UserAsync(WebAppFactory factory, TestApi admin, string name, string role)
   {
      await admin.CreateUserAsync(name, role, "first-password-of-a-user");
      return await TestApi.SignedInAsync(factory, name, "first-password-of-a-user", changeTo: "second-password-of-a-user");
   }

   private static Task<HttpResponseMessage> PostOpsAsync(TestApi api, params object[] ops) => api.PostAsync("/api/changes/ops", new { ops });

   private static async Task<JsonElement> OpsAsync(TestApi api, params object[] ops) => await (await PostOpsAsync(api, ops)).JsonAsync(HttpStatusCode.OK);

   private static async Task<JsonElement> ChangesAsync(TestApi api) => await (await api.GetAsync("/api/changes")).JsonAsync(HttpStatusCode.OK);

   private static async Task<JsonElement> PreviewAsync(TestApi api) => await (await api.PostAsync("/api/changes/preview")).JsonAsync(HttpStatusCode.OK);

   private static Task<HttpResponseMessage> PostCommitAsync(TestApi api, JsonElement preview, object[]? scripts = null, bool allowAnyStatement = false) =>
      api.PostAsync("/api/changes/commit", new
      {
         planId = preview.GetProperty("planId").GetString(),
         version = preview.GetProperty("version").GetInt32(),
         scripts,
         allowAnyStatement,
      });

   private static async Task<JsonElement> CommitAsync(TestApi api, JsonElement preview, object[]? scripts = null, bool allowAnyStatement = false) =>
      await (await PostCommitAsync(api, preview, scripts, allowAnyStatement)).JsonAsync(HttpStatusCode.OK);

   /// <summary>The visible values of a query's rows, each row as text.</summary>
   private static async Task<List<string>> RowsAsync(TestApi api, string text)
   {
      JsonElement page = await (await api.PostAsync("/api/query/execute", new { text })).JsonAsync(HttpStatusCode.OK);
      List<int> visible = [.. page.GetProperty("schema").GetProperty("columns").EnumerateArray().Where(c => !c.GetProperty("hidden").GetBoolean())
         .Select(c => c.GetProperty("ordinal").GetInt32())];
      return [.. page.GetProperty("rows").EnumerateArray().Select(r => string.Join(" ", visible.Select(o => r.GetProperty("v")[o].ToString())))];
   }

   private static object Set(string entity, object[] key, object values, object original, object? display = null) =>
      new { op = "set", entity, key, values, original, display };

   private static object Insert(string entity, string tempId, object values) => new { op = "insert", entity, tempId, values };

   private static object Delete(string entity, object[] key, object original) => new { op = "delete", entity, key, original };

   private static List<JsonElement> Changes(JsonElement set) => [.. set.GetProperty("changes").EnumerateArray()];

   private static string Raw(JsonElement change, string property) => change.GetProperty(property).GetRawText();

   /// <summary>
   /// Changes are kept on the server as they are made: a row has one change, whose originals are the values first
   /// read; a value set back to its original is no change, nor is a row without any; deleting drops new values, and a
   /// new row just goes. Rows are named as browsing names them.
   /// </summary>
   [Fact]
   public async Task ChangesMergeAsTheyAreMade()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      (await ChangesAsync(admin)).GetRawText().ShouldBe("""{"version":0,"updatedAt":null,"changes":[]}""");

      JsonElement set = await OpsAsync(admin, Set("shop.customers", ["1"], new { city = "Durban" }, new { city = "Cape Town" }));
      set.GetProperty("version").GetInt32().ShouldBe(1);
      JsonElement change = Changes(set).Single();
      (change.GetProperty("kind").GetString(), change.GetProperty("entity").GetString(), change.GetProperty("source").GetString()).ShouldBe(("update", "shop.customers", "shop"));
      (Raw(change, "key"), Raw(change, "values"), Raw(change, "original")).ShouldBe(("""["1"]""", """{"city":"Durban"}""", """{"city":"Cape Town"}"""));
      JsonElement page = await (await admin.PostAsync("/api/browse/page", new { source = new { entity = "shop.customers" } })).JsonAsync(HttpStatusCode.OK);
      change.GetProperty("rowId").GetString().ShouldBe(page.GetProperty("rows")[0].GetProperty("id").GetString(), "rows are named as browsing names them");
      long id = change.GetProperty("id").GetInt64();

      // Keys and values may be JSON's numbers; they are kept as rows' values are sent.
      set = await OpsAsync(admin, Set("shop.customers", [1], new { city = "Paarl" }, new { city = "Durban" }));
      change = Changes(set).Single();
      (change.GetProperty("id").GetInt64(), Raw(change, "values"), Raw(change, "original")).ShouldBe((id, """{"city":"Paarl"}""", """{"city":"Cape Town"}"""),
         "the original is the value first read");
      set.GetProperty("version").GetInt32().ShouldBe(2);
      // Once kept, an original needn't be given again.
      set = await OpsAsync(admin, new { op = "set", entity = "shop.customers", key = new[] { "1" }, values = new { city = "George" } });
      (Raw(Changes(set).Single(), "values"), Raw(Changes(set).Single(), "original")).ShouldBe(("""{"city":"George"}""", """{"city":"Cape Town"}"""));

      // 5000 is the 5000.00 it had: no change; nor is the city it had.
      set = await OpsAsync(admin, Set("shop.customers", ["1"], new { city = "Cape Town", credit_limit = 5000 }, new { city = "George", credit_limit = "5000.00" }));
      Changes(set).ShouldBeEmpty();
      set.GetProperty("version").GetInt32().ShouldBe(4);
      (await OpsAsync(admin, new { op = "revert", entity = "shop.customers", key = new[] { "1" } })).GetProperty("version").GetInt32().ShouldBe(4, "reverting nothing changes nothing");

      // New rows are named by the client, and changed and dropped by that name.
      set = await OpsAsync(admin, Insert("shop.customers", "n1", new { name = "Zeta" }), new { op = "set", entity = "shop.customers", tempId = "n1", values = new { city = "Pretoria" } });
      change = Changes(set).Single();
      (change.GetProperty("kind").GetString(), change.GetProperty("tempId").GetString(), Raw(change, "key"), Raw(change, "values"))
         .ShouldBe(("insert", "n1", "null", """{"city":"Pretoria","name":"Zeta"}"""));
      Changes(await OpsAsync(admin, new { op = "delete", entity = "shop.customers", tempId = "n1" })).ShouldBeEmpty();

      // Deleting a changed row drops its new values, and keeps the values it had when first changed.
      await OpsAsync(admin, Set("shop.orders", ["1001"], new { status = "shipped" }, new { status = "open" }));
      change = Changes(await OpsAsync(admin, Delete("shop.orders", ["1001"], new { status = "shipped", total = "250.00" }))).Single();
      (change.GetProperty("kind").GetString(), Raw(change, "values"), Raw(change, "original")).ShouldBe(("delete", "{}", """{"status":"open","total":"250.00"}"""));
      JsonElement problem = await (await PostOpsAsync(admin, Set("shop.orders", ["1001"], new { total = 1 }, new { total = "250.00" }))).ProblemAsync(400, ProblemCodes.InvalidRequest);
      problem.GetProperty("errors").GetProperty("ops[0].key")[0].GetString().ShouldBe("The row is to be deleted: revert that to change it");
      Changes(await OpsAsync(admin, new { op = "revert", change = change.GetProperty("id").GetInt64() })).ShouldBeEmpty();

      // What to show for the row a new foreign key refers to goes with the key's value.
      change = Changes(await OpsAsync(admin, Set("shop.orders", ["1003"], new { customer_id = "1", status = "held" }, new { customer_id = "2", status = "open" },
         new { customer = "Acme Ltd" }))).Single();
      Raw(change, "display").ShouldBe("""{"customer":"Acme Ltd"}""");
      change = Changes(await OpsAsync(admin, new { op = "revert", entity = "shop.orders", key = new[] { "1003" }, columns = new[] { "CUSTOMER_ID" } })).Single();
      (Raw(change, "values"), Raw(change, "original"), Raw(change, "display")).ShouldBe(("""{"status":"held"}""", """{"status":"open"}""", "{}"));

      // Changes kept are the user's: another's are their own.
      TestApi kim = await UserAsync(factory, admin, "kim", "dataManager");
      Changes(await ChangesAsync(kim)).ShouldBeEmpty();
      // A version says which changes the operations are for.
      int version = (await ChangesAsync(admin)).GetProperty("version").GetInt32();
      await (await admin.PostAsync("/api/changes/ops", new { ops = new[] { Set("shop.orders", ["1003"], new { status = "x" }, new { status = "open" }) }, version = version - 1 }))
         .ProblemAsync(409, ProblemCodes.ConcurrencyConflict);
      await (await admin.DeleteAsync($"/api/changes?version={version - 1}")).ProblemAsync(409, ProblemCodes.ConcurrencyConflict);
      Changes(await (await admin.DeleteAsync("/api/changes?source=SHOP")).JsonAsync(HttpStatusCode.OK)).ShouldBeEmpty();
   }

   /// <summary>Operations are checked against the catalog, and applied together or not at all; what is wrong is told by field.</summary>
   [Fact]
   public async Task OpsAreCheckedAndAppliedTogether()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      await TestSources.AddSqliteAsync(admin, "copy", Shop(factory));

      async Task<string> RefusedAsync(string field, params object[] ops)
      {
         JsonElement problem = await (await PostOpsAsync(admin, ops)).ProblemAsync(400, ProblemCodes.InvalidRequest);
         JsonElement errors = problem.GetProperty("errors");
         errors.TryGetProperty(field, out JsonElement messages).ShouldBeTrue(errors.GetRawText());
         return messages[0].GetString()!;
      }

      (await RefusedAsync("ops[1].values.colour", Set("shop.customers", ["1"], new { city = "Durban" }, new { city = "Cape Town" }),
         Set("shop.customers", ["2"], new { colour = "red" }, new { colour = "blue" }))).ShouldBe("shop.customers has no column 'colour'");
      Changes(await ChangesAsync(admin)).ShouldBeEmpty("nothing is applied when anything is wrong");

      (await RefusedAsync("ops[0].entity", Set("shop.nothing", ["1"], new { a = 1 }, new { a = 2 }))).ShouldBe("There is no entity shop.nothing");
      (await RefusedAsync("ops[0].entity", Set("shop.open_orders", ["1"], new { total = 1 }, new { total = 2 }))).ShouldBe("shop.open_orders is a view: only tables can be changed");
      (await RefusedAsync("ops[0].entity", Insert("copy.customers", "n1", new { name = "Zeta" }))).ShouldBe("copy.customers can't be changed: copy is read-only");
      (await RefusedAsync("ops[0].key", Set("shop.order_lines", ["1001"], new { qty = 3 }, new { qty = 2 })))
         .ShouldBe("The key of shop.order_lines is order_id, line_no: give a value of each, in that order");
      (await RefusedAsync("ops[0].key[0]", Set("shop.orders", ["x"], new { total = 1 }, new { total = 2 }))).ShouldBe("\"x\" isn't a whole number");
      (await RefusedAsync("ops[0].key[0]", Set("shop.orders", [null!], new { total = 1 }, new { total = 2 }))).ShouldBe("A key has no nulls: 'id' needs a value");
      (await RefusedAsync("ops[0].values.id", Set("shop.customers", ["1"], new { id = 9 }, new { id = 1 })))
         .ShouldBe("'id' is part of the key, which can't change: delete the row and insert it again");
      (await RefusedAsync("ops[0].values.status", Set("shop.orders", ["1001"], new { status = (string?)null }, new { status = "open" }))).ShouldBe("'status' can't be null");
      (await RefusedAsync("ops[0].values.total", Set("shop.orders", ["1001"], new { total = "12,5" }, new { total = "250.00" }))).ShouldBe("\"12,5\" isn't a number");
      (await RefusedAsync("ops[0].original.Status", Set("shop.orders", ["1001"], new Dictionary<string, object> { ["Status"] = "held" }, new { total = "250.00" })))
         .ShouldStartWith("Give the value 'status' had when the row was read");
      (await RefusedAsync("ops[0].original", Delete("shop.orders", ["1001"], new { }))).ShouldStartWith("Give the values the row had when it was read");
      (await RefusedAsync("ops[0].values", new { op = "delete", entity = "shop.orders", key = new[] { "1001" }, values = new { total = 1 }, original = new { total = "250.00" } }))
         .ShouldStartWith("A row to delete takes no values");
      (await RefusedAsync("ops[0].values.city", Set("shop.customers", ["1"], new { city = new string('x', 1_000_001) }, new { city = "Cape Town" })))
         .ShouldBe("A value has at most 1000000 characters");
      (await RefusedAsync("ops[0].values", Set("shop.orders", ["1001"], new { }, new { }))).ShouldBe("Give a value for at least one column");
      (await RefusedAsync("ops[1].tempId", Insert("shop.customers", "n1", new { name = "A" }), Insert("shop.customers", "n1", new { name = "B" })))
         .ShouldBe("A new row is named n1 already");
      (await RefusedAsync("ops[0].tempId", new { op = "set", entity = "shop.customers", tempId = "n9", values = new { name = "A" } })).ShouldBe("There is no new row n9 of shop.customers");
      (await RefusedAsync("ops[0].key", new { op = "insert", entity = "shop.customers", key = new[] { "1" }, values = new { name = "A" } }))
         .ShouldStartWith("A new row is named by its tempId");
      (await RefusedAsync("ops[0].key", new { op = "set", entity = "shop.customers", tempId = "n1", key = new[] { "1" }, values = new { name = "A" } }))
         .ShouldBe("Name the row by its key, or a new row by its tempId: not both");
      (await RefusedAsync("ops[0].change", new { op = "revert", change = 1, entity = "shop.customers" })).ShouldBe("Name the change by its id, or by its row: not both");
      (await RefusedAsync("ops[0].op", new { entity = "shop.customers" })).ShouldStartWith("The Op field is required");
      (await RefusedAsync("ops[0].display.orders", Set("shop.customers", ["1"], new { city = "Durban" }, new { city = "Cape Town" }, new { orders = "x" })))
         .ShouldStartWith("'orders' leads to the rows that refer to this one");

      // Readers change nothing; those who edit data do.
      TestApi lee = await UserAsync(factory, admin, "lee", "read");
      await (await lee.GetAsync("/api/changes")).ProblemAsync(403, ProblemCodes.Forbidden);
      await (await PostOpsAsync(lee, Set("shop.customers", ["1"], new { city = "Durban" }, new { city = "Cape Town" }))).ProblemAsync(403, ProblemCodes.Forbidden);
      TestApi kim = await UserAsync(factory, admin, "kim", "dataManager");
      Changes(await OpsAsync(kim, Set("shop.customers", ["1"], new { city = "Durban" }, new { city = "Cape Town" }))).Count.ShouldBe(1);

      // A deleted user's changes go with them.
      int kimId = (await (await admin.GetAsync("/api/users")).JsonAsync(HttpStatusCode.OK)).EnumerateArray().Single(u => u.GetProperty("userName").GetString() == "kim")
         .GetProperty("id").GetInt32();
      JsonElement user = await (await admin.GetAsync($"/api/users/{kimId}")).JsonAsync(HttpStatusCode.OK);
      (await admin.DeleteAsync($"/api/users/{kimId}?version={user.GetProperty("version").GetInt32()}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
      using IServiceScope scope = factory.Services.CreateScope();
      (await scope.ServiceProvider.GetRequiredService<MetadataDb>().PendingChanges.CountAsync(Token)).ShouldBe(0);
   }

   /// <summary>A user keeps at most so many changes, and gives at most so many operations at once.</summary>
   [Fact]
   public async Task ChangesAreLimited()
   {
      await using WebAppFactory factory = new() { Settings = new Dictionary<string, string?> { ["GalaxyData:Changes:MaxChanges"] = "2" } };
      TestApi admin = await TestApi.SignedInAsync(factory);
      await TestSources.AddSqliteAsync(admin, "shop", await TestSources.ShopAsync(factory), readOnly: false);
      await OpsAsync(admin, Insert("shop.customers", "n1", new { name = "A" }), Insert("shop.customers", "n2", new { name = "B" }));
      (await (await PostOpsAsync(admin, Insert("shop.customers", "n3", new { name = "C" }))).ProblemAsync(400, ProblemCodes.InvalidRequest))
         .GetProperty("errors").GetProperty("ops")[0].GetString().ShouldBe("A user may have at most 2 changes pending: commit or revert some first");
      (await (await PostOpsAsync(admin, new { op = "revert", tempId = "n1", entity = "shop.customers" }, new { op = "revert", tempId = "n2", entity = "shop.customers" },
         new { op = "revert", tempId = "n3", entity = "shop.customers" })).ProblemAsync(400, ProblemCodes.InvalidRequest))
         .GetProperty("errors").GetProperty("ops")[0].GetString().ShouldBe("At most 2 operations are applied at once");
   }

   /// <summary>A row has one id however its key is given, the same browsing gives it.</summary>
   [Fact]
   public async Task RowsHaveOneId()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      string shop = await TestSources.ShopAsync(factory);
      await TestSources.SqliteAsync(shop, "CREATE TABLE prices (amount DECIMAL(10,2) PRIMARY KEY, label TEXT); INSERT INTO prices VALUES (1.50, 'a')");
      await TestSources.AddSqliteAsync(admin, "shop", shop, readOnly: false);
      JsonElement row = (await (await admin.PostAsync("/api/browse/page", new { source = new { entity = "shop.prices" } })).JsonAsync(HttpStatusCode.OK)).GetProperty("rows")[0];
      JsonElement set = await OpsAsync(admin,
         Set("shop.prices", ["1.50"], new { label = "b" }, new { label = "a" }),
         Set("shop.prices", [1.5], new { label = "c" }, new { label = "a" }),
         Set("shop.prices", [.. row.GetProperty("k").EnumerateArray().Select(k => (object)k)], new { label = "d" }, new { label = "a" }));
      JsonElement change = Changes(set).Single();
      (change.GetProperty("rowId").GetString(), Raw(change, "values")).ShouldBe((row.GetProperty("id").GetString(), """{"label":"d"}"""));
      change.GetProperty("rowId").GetString().ShouldBe("""["1.5"]""");
      (await CommitAsync(admin, await PreviewAsync(admin))).GetProperty("outcome").GetString().ShouldBe("committed");
      (await RowsAsync(admin, "shop.prices.select(label)")).ShouldBe(["d"]);
   }

   /// <summary>A preview is the statements each connection would run, with the changes they carry out, and the issues that keep them from running.</summary>
   [Fact]
   public async Task PreviewsShowTheStatementsAndWhatIsWrong()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      JsonElement set = await OpsAsync(admin,
         Delete("shop.order_lines", ["1001", 2], new { qty = 1, price = "50.00" }),
         Set("shop.orders", ["1001"], new { status = "shipped" }, new { status = "open" }),
         Insert("shop.customers", "n1", new { name = "Zeta", city = "Pretoria" }));
      List<long> ids = [.. Changes(set).Select(c => c.GetProperty("id").GetInt64())];

      JsonElement preview = await PreviewAsync(admin);
      preview.GetProperty("planId").GetString()!.Length.ShouldBe(32);
      preview.GetProperty("version").GetInt32().ShouldBe(set.GetProperty("version").GetInt32());
      preview.GetProperty("catalogVersion").GetString()!.Length.ShouldBe(16);
      preview.GetProperty("multiConnection").GetBoolean().ShouldBeFalse();
      preview.GetProperty("issues").GetArrayLength().ShouldBe(0);
      JsonElement script = preview.GetProperty("scripts").EnumerateArray().Single();
      (script.GetProperty("source").GetString(), script.GetProperty("kind").GetString(), script.GetProperty("dialect").GetString()).ShouldBe(("shop", "sqlite", "SQLite"));
      // Inserts first, then updates, then deletes.
      List<JsonElement> statements = [.. script.GetProperty("statements").EnumerateArray()];
      statements.Select(s => (s.GetProperty("change").GetInt64(), s.GetProperty("kind").GetString())).ShouldBe([(ids[2], "insert"), (ids[1], "update"), (ids[0], "delete")]);
      statements[1].GetProperty("text").GetString().ShouldNotBeNull().ShouldContain("'shipped'");
      script.GetProperty("text").GetString().ShouldBe(string.Join(Environment.NewLine, statements.Select(s => s.GetProperty("text").GetString() + ";" + Environment.NewLine)));

      // A new order without the values it needs can't be inserted; the other changes are shown all the same.
      set = await OpsAsync(admin, Insert("shop.orders", "n2", new { customer_id = 1 }));
      preview = await PreviewAsync(admin);
      preview.GetProperty("planId").ValueKind.ShouldBe(JsonValueKind.Null);
      preview.GetProperty("expiresAt").ValueKind.ShouldBe(JsonValueKind.Null);
      long order = Changes(set).Single(c => c.GetProperty("tempId").ValueKind == JsonValueKind.String && c.GetProperty("tempId").GetString() == "n2").GetProperty("id").GetInt64();
      preview.GetProperty("issues").EnumerateArray().Select(i => $"{i.GetProperty("change").GetInt64() == order} {i.GetProperty("column").GetString()}: {i.GetProperty("message").GetString()}")
         .ShouldBe(["True total: 'total' needs a value: it can't be null, and has no default", "True order_date: 'order_date' needs a value: it can't be null, and has no default"]);
      preview.GetProperty("scripts")[0].GetProperty("statements").GetArrayLength().ShouldBe(3);
   }

   /// <summary>A commit runs the plan, clears the changes it wrote, says what the new rows are, and is in the audit.</summary>
   [Fact]
   public async Task CommitsWriteTheChangesAndClearThem()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      await OpsAsync(admin,
         Set("shop.orders", ["1001"], new { status = "shipped" }, new { status = "open" }),
         Insert("shop.customers", "n1", new { name = "Zeta", city = "Pretoria" }),
         Delete("shop.order_lines", ["1001", "2"], new { qty = 1, price = "50.00" }));
      JsonElement preview = await PreviewAsync(admin);
      JsonElement result = await CommitAsync(admin, preview);
      result.GetProperty("outcome").GetString().ShouldBe("committed");
      result.GetProperty("failure").ValueKind.ShouldBe(JsonValueKind.Null);
      JsonElement script = result.GetProperty("scripts").EnumerateArray().Single();
      (script.GetProperty("source").GetString(), script.GetProperty("edited").GetBoolean(), script.GetProperty("status").GetString()).ShouldBe(("shop", false, "committed"));
      script.GetProperty("statements").EnumerateArray().Select(s => s.GetProperty("rowsChanged").GetInt64()).ShouldBe([1L, 1L, 1L]);
      JsonElement inserted = result.GetProperty("inserted").EnumerateArray().Single();
      (inserted.GetProperty("tempId").GetString(), inserted.GetProperty("entity").GetString(), Raw(inserted, "key"), inserted.GetProperty("rowId").GetString())
         .ShouldBe(("n1", "shop.customers", """["4"]""", """["4"]"""));
      inserted.GetProperty("values").GetProperty("name").GetString().ShouldBe("Zeta");
      Changes(result.GetProperty("changes")).ShouldBeEmpty();
      result.GetProperty("changes").GetProperty("version").GetInt32().ShouldBeGreaterThan(preview.GetProperty("version").GetInt32());
      result.GetProperty("warnings").GetArrayLength().ShouldBe(0);

      (await RowsAsync(admin, "shop.orders.where(id == 1001).select(status)")).ShouldBe(["shipped"]);
      (await RowsAsync(admin, "shop.customers.where(id == 4).select(name, city)")).ShouldBe(["Zeta Pretoria"]);
      (await RowsAsync(admin, "shop.order_lines.where(order_id == 1001).select(line_no)")).ShouldBe(["1"]);

      JsonElement commit = (await (await admin.GetAsync("/api/audit/commits")).JsonAsync(HttpStatusCode.OK)).EnumerateArray().Single();
      (commit.GetProperty("id").GetInt64(), commit.GetProperty("user").GetString(), commit.GetProperty("status").GetString(), commit.GetProperty("changes").GetInt32())
         .ShouldBe((result.GetProperty("auditId").GetInt64(), "admin", "committed", 3));
      Raw(commit, "sources").ShouldBe("""["shop"]""");
      JsonElement audit = await (await admin.GetAsync($"/api/audit/commits/{commit.GetProperty("id").GetInt64()}")).JsonAsync(HttpStatusCode.OK);
      audit.GetProperty("catalogVersion").GetString().ShouldBe(preview.GetProperty("catalogVersion").GetString());
      JsonElement written = audit.GetProperty("scripts").EnumerateArray().Single();
      (written.GetProperty("status").GetString(), written.GetProperty("statements").GetInt32(), written.GetProperty("rowsChanged").GetInt64()).ShouldBe(("committed", 3, 3L));
      written.GetProperty("text").GetString().ShouldBe(preview.GetProperty("scripts")[0].GetProperty("text").GetString());
      await (await admin.GetAsync("/api/audit/commits/999")).ProblemAsync(404, ProblemCodes.NotFound);
      TestApi kim = await UserAsync(factory, admin, "kim", "dataManager");
      await (await kim.GetAsync("/api/audit/commits")).ProblemAsync(403, ProblemCodes.Forbidden);
   }

   /// <summary>A row changed by someone else since it was read rolls every change back, and the changes stay to be looked at.</summary>
   [Fact]
   public async Task AConflictRollsEverythingBackAndKeepsTheChanges()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      JsonElement set = await OpsAsync(admin,
         Set("shop.orders", ["1003"], new { status = "shipped" }, new { status = "open" }),
         Set("shop.customers", ["2"], new { city = "Durban" }, new { city = "Johannesburg" }));
      long customer = Changes(set)[1].GetProperty("id").GetInt64();
      await TestSources.SqliteAsync(Shop(factory), "UPDATE customers SET city = 'Soweto' WHERE id = 2");

      JsonElement result = await CommitAsync(admin, await PreviewAsync(admin));
      result.GetProperty("outcome").GetString().ShouldBe("rolledBack");
      JsonElement failure = result.GetProperty("failure");
      (failure.GetProperty("kind").GetString(), failure.GetProperty("source").GetString(), failure.GetProperty("change").GetInt64()).ShouldBe(("conflict", "shop", customer));
      failure.GetProperty("statement").GetString().ShouldNotBeNull().ShouldContain("shop.customers");
      result.GetProperty("scripts")[0].GetProperty("status").GetString().ShouldBe("rolledBack");
      Changes(result.GetProperty("changes")).Count.ShouldBe(2, "nothing was written, so every change stays");
      (await RowsAsync(admin, "shop.orders.where(id == 1003).select(status)")).ShouldBe(["open"]);

      JsonElement commit = (await (await admin.GetAsync("/api/audit/commits")).JsonAsync(HttpStatusCode.OK))[0];
      commit.GetProperty("status").GetString().ShouldBe("rolledBack");
      commit.GetProperty("failure").GetString().ShouldBe(failure.GetProperty("message").GetString());
      JsonElement audit = await (await admin.GetAsync($"/api/audit/commits/{commit.GetProperty("id").GetInt64()}")).JsonAsync(HttpStatusCode.OK);
      (audit.GetProperty("failureKind").GetString(), audit.GetProperty("scripts")[0].GetProperty("status").GetString()).ShouldBe(("conflict", "rolledBack"));

      // Reverted to what the row has now, the rest commits.
      await OpsAsync(admin, new { op = "revert", change = customer });
      (await CommitAsync(admin, await PreviewAsync(admin))).GetProperty("outcome").GetString().ShouldBe("committed");
      (await RowsAsync(admin, "shop.orders.where(id == 1003).select(status)")).ShouldBe(["shipped"]);
   }

   /// <summary>
   /// While a user's commit runs, their changes can't be previewed or committed again: until they are cleared, a plan of
   /// them would write them twice.
   /// </summary>
   [Fact]
   public async Task ChangesAreCommittedOnceAtATime()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      await OpsAsync(admin, Insert("shop.addresses", "n1", new { customer_id = 3, line1 = "3 Long St" }));
      JsonElement preview = await PreviewAsync(admin);
      Task<HttpResponseMessage> commit;
      await using (Microsoft.Data.Sqlite.SqliteConnection holder = new($"Data Source={Shop(factory)};Pooling=False"))
      {
         // The commit waits for the database, which another holds.
         await holder.OpenAsync(Token);
         await using (Microsoft.Data.Sqlite.SqliteCommand exclusive = new("BEGIN EXCLUSIVE", holder)) { await exclusive.ExecuteNonQueryAsync(Token); }
         commit = PostCommitAsync(admin, preview);
         System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();
         while (true)
         {
            JsonElement commits = await (await admin.GetAsync("/api/audit/commits")).JsonAsync(HttpStatusCode.OK);
            if (commits.GetArrayLength() > 0 && commits[0].GetProperty("status").GetString() == "inProgress") { break; }
            if (waited.Elapsed > TimeSpan.FromSeconds(20)) { Assert.Fail("The commit didn't start"); }
            await Task.Delay(20, Token);
         }
         await (await admin.PostAsync("/api/changes/preview")).ProblemAsync(409, ProblemCodes.CommitInProgress);
         await (await PostCommitAsync(admin, preview)).ProblemAsync(409, ProblemCodes.PlanStale);
         await using (Microsoft.Data.Sqlite.SqliteCommand rollback = new("ROLLBACK", holder)) { await rollback.ExecuteNonQueryAsync(Token); }
      }
      (await (await commit).JsonAsync(HttpStatusCode.OK)).GetProperty("outcome").GetString().ShouldBe("committed");
      JsonElement after = await PreviewAsync(admin);
      (after.GetProperty("planId").ValueKind, after.GetProperty("scripts").GetArrayLength()).ShouldBe((JsonValueKind.Null, 0), "the changes written are cleared");
      (await RowsAsync(admin, "shop.addresses.where(line1 == '3 Long St').count()")).ShouldBe(["1"]);
   }

   /// <summary>A plan is committed once, while the changes and the catalog are as they were previewed, and before it expires.</summary>
   [Fact]
   public async Task StalePlansAreRefused()
   {
      ManualClock clock = new();
      (WebAppFactory factory, TestApi admin) = await ShopAsync(clock);
      await using WebAppFactory _ = factory;
      await OpsAsync(admin, Set("shop.orders", ["1001"], new { status = "shipped" }, new { status = "open" }));

      JsonElement preview = await PreviewAsync(admin);
      await OpsAsync(admin, Set("shop.orders", ["1002"], new { status = "held" }, new { status = "shipped" }));
      (await (await PostCommitAsync(admin, preview)).ProblemAsync(409, ProblemCodes.PlanStale)).GetProperty("detail").GetString()
         .ShouldBe("The changes were changed since they were previewed: preview the changes again");

      preview = await PreviewAsync(admin);
      await (await admin.PostAsync("/api/changes/commit", new { planId = "nothing", version = preview.GetProperty("version").GetInt32() })).ProblemAsync(409, ProblemCodes.PlanStale);
      await (await admin.PostAsync("/api/changes/commit", new { planId = preview.GetProperty("planId").GetString(), version = 0 })).ProblemAsync(409, ProblemCodes.PlanStale);
      JsonElement replaced = await PreviewAsync(admin);
      (await (await PostCommitAsync(admin, preview)).ProblemAsync(409, ProblemCodes.PlanStale)).GetProperty("detail").GetString()
         .ShouldBe("The preview expired, or another replaced it: preview the changes again");

      clock.Advance(TimeSpan.FromMinutes(31));
      await (await PostCommitAsync(admin, replaced)).ProblemAsync(409, ProblemCodes.PlanStale);

      preview = await PreviewAsync(admin);
      int shop = (await (await admin.GetAsync("/api/connections")).JsonAsync(HttpStatusCode.OK))[0].GetProperty("id").GetInt32();
      await TestSources.RefreshAsync(admin, shop);
      (await (await PostCommitAsync(admin, preview)).ProblemAsync(409, ProblemCodes.PlanStale)).GetProperty("detail").GetString()
         .ShouldBe("The catalog changed since the changes were previewed: preview the changes again");

      preview = await PreviewAsync(admin);
      (await CommitAsync(admin, preview)).GetProperty("outcome").GetString().ShouldBe("committed");
      await (await PostCommitAsync(admin, preview)).ProblemAsync(409, ProblemCodes.PlanStale);
   }

   /// <summary>
   /// An edited script runs as edited: statements that change data only, unless an administrator allows any; its
   /// rows changed aren't counted. A script that can't run says where, and the plan can still be committed.
   /// </summary>
   [Fact]
   public async Task EditedScriptsRunAsEdited()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      await OpsAsync(admin, Set("shop.orders", ["1001"], new { status = "shipped" }, new { status = "open" }));
      JsonElement preview = await PreviewAsync(admin);
      string text = preview.GetProperty("scripts")[0].GetProperty("text").GetString()!;

      JsonElement problem = await (await PostCommitAsync(admin, preview, [new { source = "shop", text = "DROP TABLE orders;" }])).ProblemAsync(422, ProblemCodes.ScriptInvalid);
      problem.GetProperty("problems")[0].GetProperty("message").GetString().ShouldBe("DROP statements can't run here: only those that change data (INSERT, UPDATE, DELETE and MERGE)");
      (await (await PostCommitAsync(admin, preview, [new { source = "nowhere", text }])).ProblemAsync(400, ProblemCodes.InvalidRequest))
         .GetProperty("errors").GetProperty("scripts[0].source")[0].GetString().ShouldBe("The changes write nothing to nowhere: a script is for a connection they write to");
      TestApi kim = await UserAsync(factory, admin, "kim", "dataManager");
      await OpsAsync(kim, Set("shop.orders", ["1002"], new { status = "held" }, new { status = "shipped" }));
      await (await PostCommitAsync(kim, await PreviewAsync(kim), [new { source = "shop", text = "DELETE FROM audit_log;" }], allowAnyStatement: true))
         .ProblemAsync(403, ProblemCodes.Forbidden);

      // Unchanged but for its line breaks, it is the plan.
      JsonElement same = await CommitAsync(admin, preview, [new { source = "SHOP", text = text.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n\n" }]);
      same.GetProperty("scripts")[0].GetProperty("edited").GetBoolean().ShouldBeFalse();

      await OpsAsync(admin, Set("shop.orders", ["1003"], new { status = "shipped" }, new { status = "open" }));
      preview = await PreviewAsync(admin);
      string edited = preview.GetProperty("scripts")[0].GetProperty("text").GetString()!.Replace("'shipped'", "'held'", StringComparison.Ordinal) +
         "UPDATE orders SET status = 'held' WHERE id = 1004;" + Environment.NewLine;
      JsonElement result = await CommitAsync(admin, preview, [new { source = "shop", text = edited }]);
      result.GetProperty("outcome").GetString().ShouldBe("committed");
      JsonElement script = result.GetProperty("scripts")[0];
      script.GetProperty("edited").GetBoolean().ShouldBeTrue();
      script.GetProperty("statements").EnumerateArray().Select(s => $"{Raw(s, "change")} {s.GetProperty("description").GetString()} {s.GetProperty("rowsChanged").GetInt64()}")
         .ShouldBe(["null statement 1 (line 1) 1", "null statement 2 (line 4) 1"], "the planned update has three lines");
      (await RowsAsync(admin, "shop.orders.where(id in [1003, 1004]).select(status)")).ShouldBe(["held", "held"]);
      Changes(result.GetProperty("changes")).ShouldBeEmpty("an edited script stands for the changes of its connection");
      result.GetProperty("warnings").GetArrayLength().ShouldBe(0);
      JsonElement audit = await (await admin.GetAsync($"/api/audit/commits/{result.GetProperty("auditId").GetInt64()}")).JsonAsync(HttpStatusCode.OK);
      (audit.GetProperty("edited").GetBoolean(), audit.GetProperty("anyStatement").GetBoolean()).ShouldBe((true, false));
      audit.GetProperty("scripts")[0].GetProperty("text").GetString().ShouldBe(edited);

      // An administrator may run other statements, but never control the transaction.
      await OpsAsync(admin, Set("shop.orders", ["1001"], new { status = "open" }, new { status = "shipped" }));
      preview = await PreviewAsync(admin);
      string other = "CREATE TABLE notes (id INTEGER PRIMARY KEY);" + Environment.NewLine + preview.GetProperty("scripts")[0].GetProperty("text").GetString();
      await (await PostCommitAsync(admin, preview, [new { source = "shop", text = other }])).ProblemAsync(422, ProblemCodes.ScriptInvalid);
      await (await PostCommitAsync(admin, preview, [new { source = "shop", text = "COMMIT;" + Environment.NewLine + other }], allowAnyStatement: true))
         .ProblemAsync(422, ProblemCodes.ScriptInvalid);
      result = await CommitAsync(admin, preview, [new { source = "shop", text = other }], allowAnyStatement: true);
      result.GetProperty("outcome").GetString().ShouldBe("committed");
      audit = await (await admin.GetAsync($"/api/audit/commits/{result.GetProperty("auditId").GetInt64()}")).JsonAsync(HttpStatusCode.OK);
      audit.GetProperty("anyStatement").GetBoolean().ShouldBeTrue();

      // An edited statement that changes no row is told of.
      await OpsAsync(admin, Set("shop.orders", ["1002"], new { status = "held" }, new { status = "shipped" }));
      preview = await PreviewAsync(admin);
      result = await CommitAsync(admin, preview, [new { source = "shop", text = "UPDATE orders SET status = 'held' WHERE id = 999999;" }]);
      Raw(result, "warnings").ShouldBe("""["statement 1 (line 1) of shop changed no rows"]""");
   }

   /// <summary>Changes to two connections commit together, or (a conflict on either) not at all.</summary>
   [Fact]
   public async Task ChangesAcrossConnectionsCommitTogether()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      string warehouse = Path.Combine(TestSources.Files(factory), "wh.duckdb");
      await TestSources.DuckDbAsync(warehouse, "CREATE TABLE stock (code VARCHAR PRIMARY KEY, qty INTEGER NOT NULL); INSERT INTO stock VALUES ('P-100', 10), ('P-200', 4)");
      await TestSources.SettledAsync(admin, await TestSources.AddAsync(admin, "wh", "duckdb", new { settings = new { DataSource = warehouse }, isReadOnly = false }), "ready");

      await OpsAsync(admin,
         Set("shop.orders", ["1001"], new { status = "shipped" }, new { status = "open" }),
         Set("wh.stock", ["P-100"], new { qty = 8 }, new { qty = 10 }));
      JsonElement preview = await PreviewAsync(admin);
      preview.GetProperty("multiConnection").GetBoolean().ShouldBeTrue();
      preview.GetProperty("scripts").EnumerateArray().Select(s => $"{s.GetProperty("source").GetString()} {s.GetProperty("dialect").GetString()}").ShouldBe(["shop SQLite", "wh DuckDB"]);
      JsonElement result = await CommitAsync(admin, preview);
      result.GetProperty("outcome").GetString().ShouldBe("committed");
      result.GetProperty("scripts").EnumerateArray().Select(s => s.GetProperty("status").GetString()).ShouldBe(["committed", "committed"]);
      (await RowsAsync(admin, "wh.stock.where(code == 'P-100').select(qty)")).ShouldBe(["8"]);

      // DuckDB runs in the application, and reads any file a statement names: only administrators edit its scripts.
      TestApi kim = await UserAsync(factory, admin, "kim", "dataManager");
      await OpsAsync(kim, Set("wh.stock", ["P-100"], new { qty = 7 }, new { qty = 8 }));
      JsonElement kims = await PreviewAsync(kim);
      string text = kims.GetProperty("scripts")[0].GetProperty("text").GetString()!;
      (await (await PostCommitAsync(kim, kims, [new { source = "wh", text = text + "INSERT INTO stock SELECT 'X', 1;" }])).ProblemAsync(403, ProblemCodes.Forbidden))
         .GetProperty("title").GetString().ShouldBe("Only administrators may edit the script for wh");
      (await CommitAsync(kim, kims, [new { source = "wh", text }])).GetProperty("outcome").GetString().ShouldBe("committed", "as planned, it may be committed");

      // The stock's original is stale now: nothing is written, the order's change included.
      await OpsAsync(admin,
         Set("shop.orders", ["1003"], new { status = "shipped" }, new { status = "open" }),
         Set("wh.stock", ["P-200"], new { qty = 3 }, new { qty = 5 }));
      result = await CommitAsync(admin, await PreviewAsync(admin));
      result.GetProperty("outcome").GetString().ShouldBe("rolledBack");
      result.GetProperty("failure").GetProperty("source").GetString().ShouldBe("wh");
      result.GetProperty("scripts").EnumerateArray().Select(s => s.GetProperty("status").GetString()).ShouldBe(["rolledBack", "rolledBack"]);
      (await RowsAsync(admin, "shop.orders.where(id == 1003).select(status)")).ShouldBe(["open"]);

      // Changes to a connection's rows go with it: a connection given its alias later would take them.
      JsonElement connection = (await (await admin.GetAsync("/api/connections")).JsonAsync(HttpStatusCode.OK)).EnumerateArray().Single(c => c.GetProperty("alias").GetString() == "wh");
      int version = (await ChangesAsync(admin)).GetProperty("version").GetInt32();
      (await admin.DeleteAsync($"/api/connections/{connection.GetProperty("id").GetInt32()}?version={connection.GetProperty("version").GetInt32()}"))
         .StatusCode.ShouldBe(HttpStatusCode.NoContent);
      JsonElement left = await ChangesAsync(admin);
      Changes(left).Select(c => c.GetProperty("source").GetString()).ShouldBe(["shop"]);
      left.GetProperty("version").GetInt32().ShouldBe(version + 1);
   }

   /// <summary>A commit the application didn't live to finish is of unknown outcome once it starts again.</summary>
   [Fact]
   public async Task CommitsCutShortAreUnknown()
   {
      await using SharedData data = new();
      await using (WebAppFactory first = new() { DataDirectory = data.Path })
      {
         first.CreateClient();
         using IServiceScope scope = first.Services.CreateScope();
         MetadataDb db = scope.ServiceProvider.GetRequiredService<MetadataDb>();
         db.CommitAudits.Add(new CommitAudit
         {
            StartedAt = DateTime.UtcNow,
            UserName = "kim",
            Status = CommitStatus.InProgress,
            ChangeCount = 1,
            Scripts = [new CommitAuditScript { Source = "shop", Kind = "sqlite", Dialect = "SQLite", Text = "UPDATE x SET y = 1;", Statements = 1, Status = CommitScriptStatus.Pending }],
         });
         await db.SaveChangesAsync(Token);
      }
      await using WebAppFactory second = new() { DataDirectory = data.Path };
      TestApi admin = await TestApi.SignedInAsync(second);
      JsonElement commit = (await (await admin.GetAsync("/api/audit/commits")).JsonAsync(HttpStatusCode.OK)).EnumerateArray().Single();
      (commit.GetProperty("status").GetString(), commit.GetProperty("user").GetString()).ShouldBe(("unknown", "kim"));
      JsonElement audit = await (await admin.GetAsync($"/api/audit/commits/{commit.GetProperty("id").GetInt64()}")).JsonAsync(HttpStatusCode.OK);
      audit.GetProperty("scripts")[0].GetProperty("status").GetString().ShouldBe("unknown");
      audit.GetProperty("failure").GetString()!.ShouldStartWith("The application stopped as the changes were written");
   }
}
