using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Web.Catalog;
using GalaxyData.Web.Problems;
using GalaxyData.Web.Tests.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Overlay;

public sealed class OverlayApiTests
{
   private static async Task<(WebAppFactory Factory, TestApi Admin)> ShopAsync(ListLogger? logs = null)
   {
      WebAppFactory factory = new() { ServiceChanges = logs == null ? null : services => services.AddSingleton<ILoggerProvider>(logs) };
      TestApi admin = await TestApi.SignedInAsync(factory);
      await TestSources.AddSqliteAsync(admin, "shop", await TestSources.ShopAsync(factory), readOnly: false);
      return (factory, admin);
   }

   /// <summary>A second database, wh, whose shipments refer to the shop's orders by a column no foreign key declares.</summary>
   private static async Task<(int Id, string Path)> WarehouseAsync(WebAppFactory factory, TestApi admin)
   {
      string path = Path.Combine(TestSources.Files(factory), "wh.db");
      await TestSources.SqliteAsync(path, "CREATE TABLE shipments (id INTEGER PRIMARY KEY, order_ref INTEGER, carrier TEXT); " +
         "INSERT INTO shipments VALUES (1, 1001, 'DHL'), (2, 1001, 'UPS'), (3, 1002, 'DHL')");
      return (await TestSources.AddSqliteAsync(admin, "wh", path), path);
   }

   private static readonly object Shipments = new { from = "wh.shipments", fromColumns = new[] { "order_ref" }, to = "shop.orders", toColumns = new[] { "id" }, inverseName = "shipments" };

   private static async Task<JsonElement> PageAsync(TestApi api, object source) =>
      await (await api.PostAsync("/api/browse/page", new { source })).JsonAsync(HttpStatusCode.OK);

   private static List<string> Firsts(JsonElement page) => page.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("v")[0].ToString()).ToList();

   private static List<string> Issues(JsonElement item) =>
      item.GetProperty("issues").EnumerateArray().Select(i => $"{i.GetProperty("code").GetString()} {i.GetProperty("severity").GetString()}: {i.GetProperty("message").GetString()}").ToList();

   private static async Task<Dictionary<string, string>> InvalidAsync(HttpResponseMessage response) =>
      (await response.ProblemAsync(400, ProblemCodes.InvalidRequest)).GetProperty("errors").EnumerateObject().ToDictionary(e => e.Name, e => e.Value[0].GetString()!);

   private static async Task<JsonElement> EntityAsync(TestApi api, string name) =>
      await (await api.GetAsync($"/api/catalog/entity?name={Uri.EscapeDataString(name)}")).JsonAsync(HttpStatusCode.OK);

   [Fact]
   public async Task RelationsLinkSourcesAndAreNavigated()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      await WarehouseAsync(factory, admin);
      string before = (await (await admin.GetAsync("/api/catalog")).JsonAsync(HttpStatusCode.OK)).GetProperty("version").GetString()!;

      HttpResponseMessage response = await admin.PostAsync("/api/overlay/relations", Shipments);
      JsonElement relation = await response.JsonAsync(HttpStatusCode.Created);
      int id = relation.GetProperty("id").GetInt32();
      response.Headers.Location!.ToString().ShouldBe($"/api/overlay/relations/{id}");
      response.Headers.GetValues(CatalogService.VersionHeader).Single().ShouldNotBe(before, "the catalog is built again with the relation");
      Issues(relation).ShouldBeEmpty();
      relation.GetProperty("navigations").GetProperty("forward").GetString().ShouldBe("order", "order_ref, as the convention names it");
      relation.GetProperty("navigations").GetProperty("inverse").GetString().ShouldBe("shipments");
      relation.GetProperty("version").GetInt32().ShouldBe(0);

      Firsts(await PageAsync(admin, new { from = new { entity = "shop.orders", key = new[] { "1001" } }, navigation = "shipments" })).ShouldBe(["1", "2"]);
      Firsts(await PageAsync(admin, new { from = new { entity = "wh.shipments", key = new[] { 3 } }, navigation = "order" })).ShouldBe(["1002"]);
      NavigationOf(await EntityAsync(admin, "shop.orders"), "shipments").GetProperty("isCrossSource").GetBoolean().ShouldBeTrue();

      JsonElement read = await (await admin.GetAsync($"/api/overlay/relations/{id}")).JsonAsync(HttpStatusCode.OK);
      read.GetProperty("from").GetString().ShouldBe("wh.shipments");
      JsonElement audit = (await (await admin.GetAsync("/api/audit/admin-events")).JsonAsync(HttpStatusCode.OK))[0];
      audit.GetProperty("action").GetString().ShouldBe("overlay.relation.created");
      audit.GetProperty("target").GetString().ShouldBe("relation:wh.shipments(order_ref) -> shop.orders(id)");

      string exported = await (await admin.GetAsync("/api/overlay/export")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      CatalogOverlay overlay = CatalogOverlay.FromJson(exported);
      overlay.Relations.ShouldHaveSingleItem().InverseName.ShouldBe("shipments");
      overlay.Relations[0].FromColumns.ShouldBe(["order_ref"]);
   }

   private static JsonElement NavigationOf(JsonElement entity, string name) =>
      entity.GetProperty("navigations").EnumerateArray().Single(n => n.GetProperty("name").GetString() == name);

   /// <summary>A schema that changes under an item breaks it: it is kept, says why, and is told of; changed to fit, it works again.</summary>
   [Fact]
   public async Task ItemsThatStopWorkingSaySoAfterARefresh()
   {
      ListLogger logs = new();
      (WebAppFactory factory, TestApi admin) = await ShopAsync(logs);
      await using WebAppFactory _ = factory;
      (int whId, string whPath) = await WarehouseAsync(factory, admin);
      JsonElement relation = await (await admin.PostAsync("/api/overlay/relations", Shipments)).JsonAsync(HttpStatusCode.Created);
      int id = relation.GetProperty("id").GetInt32();

      await TestSources.SqliteAsync(whPath, "ALTER TABLE shipments RENAME COLUMN order_ref TO order_id");
      await TestSources.RefreshAsync(admin, whId);
      JsonElement overlay = await (await admin.GetAsync("/api/overlay")).JsonAsync(HttpStatusCode.OK);
      (overlay.GetProperty("errors").GetInt32(), overlay.GetProperty("warnings").GetInt32()).ShouldBe((1, 0));
      JsonElement broken = overlay.GetProperty("relations")[0];
      Issues(broken).ShouldBe(["GDQ5005 error: There is no column 'order_ref'"]);
      broken.GetProperty("navigations").ValueKind.ShouldBe(JsonValueKind.Null, "it isn't in the catalog");
      JsonElement diagnostic = (await (await admin.GetAsync("/api/catalog")).JsonAsync(HttpStatusCode.OK)).GetProperty("diagnostics").EnumerateArray()
         .Single(d => d.GetProperty("item").ValueKind != JsonValueKind.Null);
      diagnostic.GetProperty("item").GetRawText().ShouldBe($$"""{"kind":"relation","id":{{id}}}""");
      logs.Messages.ShouldContain($"The overlay's Relation {id} doesn't work: There is no column 'order_ref'");

      HttpResponseMessage stale = await admin.PutAsync($"/api/overlay/relations/{id}", new { relation = Shipments, version = 5 });
      await stale.ProblemAsync(409, ProblemCodes.ConcurrencyConflict);
      JsonElement fixedUp = await (await admin.PutAsync($"/api/overlay/relations/{id}", new
      {
         relation = new { from = "wh.shipments", fromColumns = new[] { "order_id" }, to = "shop.orders", toColumns = new[] { "id" }, inverseName = "shipments" },
         version = 0,
      })).JsonAsync(HttpStatusCode.OK);
      Issues(fixedUp).ShouldBeEmpty();
      fixedUp.GetProperty("version").GetInt32().ShouldBe(1);
      fixedUp.GetProperty("navigations").GetProperty("forward").GetString().ShouldBe("order");
      JsonElement audit = (await (await admin.GetAsync("/api/audit/admin-events")).JsonAsync(HttpStatusCode.OK))[0];
      audit.GetProperty("action").GetString().ShouldBe("overlay.relation.updated");
      audit.GetProperty("details").GetString().ShouldBe("""{"fromColumns":{"from":["order_ref"],"to":["order_id"]}}""");
      (await (await admin.GetAsync("/api/overlay")).JsonAsync(HttpStatusCode.OK)).GetProperty("errors").GetInt32().ShouldBe(0);

      // The same again changes nothing, and isn't audited.
      JsonElement same = await (await admin.PutAsync($"/api/overlay/relations/{id}", new
      {
         relation = new { from = " wh.shipments ", fromColumns = new[] { "order_id" }, to = "shop.orders", toColumns = new[] { "id" }, inverseName = "shipments" },
         version = 1,
      })).JsonAsync(HttpStatusCode.OK);
      same.GetProperty("version").GetInt32().ShouldBe(1);
   }

   [Fact]
   public async Task VirtualEntitiesAreTriedSavedAndBrowsed()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;

      JsonElement tried = await (await admin.PostAsync("/api/overlay/virtual-entities/validate", new { name = "reports.big_orders", query = "shop.orders.where(totl > 50)" }))
         .JsonAsync(HttpStatusCode.OK);
      Issues(tried).ShouldHaveSingleItem().ShouldStartWith("GDQ2025 error: The definition doesn't work: There is no 'totl' here");
      tried.GetProperty("entity").GetProperty("problem").GetString()!.ShouldStartWith("There is no 'totl' here");
      tried.GetProperty("entity").GetProperty("columns").GetArrayLength().ShouldBe(0);
      JsonElement placed = tried.GetProperty("diagnostics").EnumerateArray().Single();
      (placed.GetProperty("code").GetString(), placed.GetProperty("start").GetInt32(), placed.GetProperty("end").GetInt32()).ShouldBe(("GDQ2001", 18, 22));

      JsonElement fine = await (await admin.PostAsync("/api/overlay/virtual-entities/validate", new { name = "reports.big_orders", query = "shop.orders.where(total > 50)" }))
         .JsonAsync(HttpStatusCode.OK);
      Issues(fine).ShouldBeEmpty();
      fine.GetProperty("diagnostics").GetArrayLength().ShouldBe(0);
      JsonElement entity = fine.GetProperty("entity");
      entity.GetProperty("kind").GetString().ShouldBe("virtual");
      entity.GetProperty("key").GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ShouldBe(["id"]);
      entity.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ShouldContain("total");
      (await (await admin.GetAsync("/api/overlay")).JsonAsync(HttpStatusCode.OK)).GetProperty("virtualEntities").GetArrayLength().ShouldBe(0, "trying saves nothing");

      JsonElement created = await (await admin.PostAsync("/api/overlay/virtual-entities", new { name = "reports.big_orders", query = "shop.orders.where(total > 50)", description = "Orders over 50" }))
         .JsonAsync(HttpStatusCode.Created);
      Issues(created).ShouldBeEmpty();
      Firsts(await PageAsync(admin, new { entity = "reports.big_orders" })).ShouldBe(["1001", "1002"]);
      Firsts(await PageAsync(admin, new { from = new { entity = "reports.big_orders", key = new[] { "1001" } }, navigation = "customer" })).ShouldBe(["1"]);
      JsonElement roots = await (await admin.GetAsync("/api/catalog/tree/children")).JsonAsync(HttpStatusCode.OK);
      roots.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetString()).ShouldContain("reports");

      await (await admin.PostAsync("/api/overlay/virtual-entities", new { name = "reports.big_orders", query = "shop.orders" })).ProblemAsync(409, ProblemCodes.OverlayItemExists);
      JsonElement clash = await (await admin.PostAsync("/api/overlay/virtual-entities", new { name = "shop.orders", query = "shop.customers" })).JsonAsync(HttpStatusCode.Created);
      Issues(clash).ShouldBe(["GDQ5015 error: shop.orders already exists"], "kept, with what is wrong with it");
      // A virtual entity built on a broken one says so.
      JsonElement onTop = await (await admin.PostAsync("/api/overlay/virtual-entities", new { name = "reports.on_top", query = "reports.nothing.take(1)" }))
         .JsonAsync(HttpStatusCode.Created);
      Issues(onTop).ShouldHaveSingleItem().ShouldStartWith("GDQ2025 error:");

      (await InvalidAsync(await admin.PostAsync("/api/overlay/virtual-entities", new { name = "lonely", query = "shop.orders" })))["name"]
         .ShouldBe("A virtual entity's name has a namespace and a name, as in reports.lonely");
      (await InvalidAsync(await admin.PostAsync("/api/overlay/virtual-entities", new { name = "reports.x", query = "  " }))).Keys.ShouldBe(["query"]);
      (await InvalidAsync(await admin.PostAsync("/api/overlay/virtual-entities", new { name = "reports.x", query = "shop.orders", key = new[] { "id", "id" } })))["key[1]"]
         .ShouldBe("'id' is named twice");
   }

   [Fact]
   public async Task EntitySettingsShapeEntities()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      JsonElement settings = await (await admin.PostAsync("/api/overlay/entity-settings", new
      {
         entity = "shop.customers",
         displayColumn = "city",
         columns = new object[]
         {
            new { name = "credit_limit", label = " Credit ", type = "Decimal(12, 2)" },
            new { name = "created_at", hidden = true },
         },
      })).JsonAsync(HttpStatusCode.Created);
      Issues(settings).ShouldBeEmpty();
      settings.GetProperty("columns")[0].GetRawText().ShouldBe("""{"name":"credit_limit","hidden":false,"label":"Credit","type":"decimal(12,2)"}""");
      JsonElement customers = await EntityAsync(admin, "shop.customers");
      customers.GetProperty("displayColumn").GetString().ShouldBe("city");
      JsonElement credit = customers.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "credit_limit");
      credit.GetProperty("label").GetString().ShouldBe("Credit");
      credit.GetProperty("type").GetProperty("text").GetString().ShouldBe("decimal(12,2)?", "the type given, as nullable as the column");
      customers.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "created_at").GetProperty("hidden").GetBoolean().ShouldBeTrue();

      JsonElement log = await (await admin.PostAsync("/api/overlay/entity-settings", new { entity = "shop.audit_log", key = new[] { "at" }, hidden = true }))
         .JsonAsync(HttpStatusCode.Created);
      Issues(log).ShouldBeEmpty();
      JsonElement shop = await (await admin.GetAsync("/api/catalog/tree/children?parent=shop")).JsonAsync(HttpStatusCode.OK);
      shop.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetString()).ShouldNotContain("shop.audit_log", "hidden");
      JsonElement key = (await EntityAsync(admin, "shop.audit_log")).GetProperty("key");
      (key.GetProperty("columns")[0].GetString(), key.GetProperty("isDeclared").GetBoolean()).ShouldBe(("at", true));

      // The same entity by another path: kept, and left out of the catalog.
      JsonElement twice = await (await admin.PostAsync("/api/overlay/entity-settings", new { entity = "shop.main.customers", hidden = true })).JsonAsync(HttpStatusCode.Created);
      Issues(twice).ShouldBe(["GDQ5016 error: 'shop.main.customers' is shop.customers, which has settings already (as 'shop.customers'), so these are left out"]);
      await (await admin.PostAsync("/api/overlay/entity-settings", new { entity = "shop.customers" })).ProblemAsync(409, ProblemCodes.OverlayItemExists);
      JsonElement unknown = await (await admin.PostAsync("/api/overlay/entity-settings", new { entity = "shop.orders", displayColumn = "nope", key = new[] { "id" } }))
         .JsonAsync(HttpStatusCode.Created);
      Issues(unknown).ShouldBe(["GDQ5013 warning: The entity has a primary key already, so the declared key is ignored", "GDQ5005 error: There is no column 'nope' to display"]);

      Dictionary<string, string> errors = await InvalidAsync(await admin.PostAsync("/api/overlay/entity-settings", new
      {
         entity = "shop.addresses",
         columns = new object?[] { new { name = "city", type = "decimal(x)" }, new { name = "city" }, null },
      }));
      errors["columns[0].type"].ShouldStartWith("'decimal(x)' isn't a type");
      errors["columns[1].name"].ShouldBe("'city' has settings already, before");
      errors["columns[2]"].ShouldBe("A column's settings can't be null");
      // The framework's own checks name fields as the JSON does.
      (await InvalidAsync(await admin.PostAsync("/api/overlay/entity-settings", new { entity = "shop.addresses", columns = new[] { new { name = " " } } })))
         .Keys.ShouldBe(["columns[0].name"]);
   }

   [Fact]
   public async Task NavigationsAreRenamedAndHidden()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      JsonElement buyer = await (await admin.PostAsync("/api/overlay/navigations", new { entity = "shop.orders", navigation = " customer ", renameTo = "buyer" }))
         .JsonAsync(HttpStatusCode.Created);
      buyer.GetProperty("navigation").GetString().ShouldBe("customer");
      Issues(buyer).ShouldBeEmpty();
      Firsts(await PageAsync(admin, new { from = new { entity = "shop.orders", key = new[] { "1001" } }, navigation = "buyer" })).ShouldBe(["1"]);
      (await admin.PostAsync("/api/browse/page", new { source = new { from = new { entity = "shop.orders", key = new[] { "1001" } }, navigation = "customer" } }))
         .StatusCode.ShouldBe(HttpStatusCode.BadRequest, "it is called buyer now");

      await (await admin.PostAsync("/api/overlay/navigations", new { entity = "shop.customers", navigation = "addresses", hidden = true })).JsonAsync(HttpStatusCode.Created);
      NavigationOf(await EntityAsync(admin, "shop.customers"), "addresses").GetProperty("hidden").GetBoolean().ShouldBeTrue();
      JsonElement missing = await (await admin.PostAsync("/api/overlay/navigations", new { entity = "shop.orders", navigation = "nope", hidden = true }))
         .JsonAsync(HttpStatusCode.Created);
      Issues(missing).ShouldBe(["GDQ5011 error: There is no navigation 'nope'"]);
      await (await admin.PostAsync("/api/overlay/navigations", new { entity = "shop.orders", navigation = "customer", hidden = true })).ProblemAsync(409, ProblemCodes.OverlayItemExists);
      (await InvalidAsync(await admin.PostAsync("/api/overlay/navigations", new { entity = "shop.orders", navigation = "status" })))["renameTo"]
         .ShouldBe("Rename the navigation, or hide it");

      // Tried in place of the override it changes, it is no second override of the navigation; as a new one, it is.
      int id = buyer.GetProperty("id").GetInt32();
      object client = new { entity = "shop.orders", navigation = "customer", renameTo = "client" };
      JsonElement instead = await (await admin.PostAsync($"/api/overlay/navigations/validate?id={id}", client)).JsonAsync(HttpStatusCode.OK);
      Issues(instead).ShouldBeEmpty();
      instead.GetProperty("entity").GetProperty("navigations").EnumerateArray().Select(n => n.GetProperty("name").GetString()).ShouldContain("client");
      JsonElement added = await (await admin.PostAsync("/api/overlay/navigations/validate", client)).JsonAsync(HttpStatusCode.OK);
      Issues(added).ShouldHaveSingleItem().ShouldStartWith("GDQ5016 error:");
      await (await admin.PostAsync("/api/overlay/navigations/validate?id=999", client)).ProblemAsync(404, ProblemCodes.NotFound);
   }

   [Fact]
   public async Task DeletingAnItemTakesItOut()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      int id = (await (await admin.PostAsync("/api/overlay/navigations", new { entity = "shop.orders", navigation = "customer", renameTo = "buyer" }))
         .JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetInt32();
      await (await admin.DeleteAsync($"/api/overlay/navigations/{id}?version=3")).ProblemAsync(409, ProblemCodes.ConcurrencyConflict);
      (await admin.DeleteAsync($"/api/overlay/navigations/{id}?version=0")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
      await (await admin.GetAsync($"/api/overlay/navigations/{id}")).ProblemAsync(404, ProblemCodes.NotFound);
      await (await admin.DeleteAsync($"/api/overlay/navigations/{id}")).ProblemAsync(404, ProblemCodes.NotFound);
      (await (await admin.GetAsync("/api/overlay")).JsonAsync(HttpStatusCode.OK)).GetProperty("navigations").GetArrayLength().ShouldBe(0);
      NavigationOf(await EntityAsync(admin, "shop.orders"), "customer").GetProperty("name").GetString().ShouldBe("customer", "named as before");
      JsonElement audit = (await (await admin.GetAsync("/api/audit/admin-events")).JsonAsync(HttpStatusCode.OK))[0];
      (audit.GetProperty("action").GetString(), audit.GetProperty("target").GetString()).ShouldBe(("overlay.navigation.deleted", "navigation:shop.orders.customer"));
   }

   [Fact]
   public async Task RequestsThatArentItemsAreRefusedByField()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      Dictionary<string, string> errors = await InvalidAsync(await admin.PostAsync("/api/overlay/relations",
         new { from = "shop orders", fromColumns = Array.Empty<string>(), to = "shop.customers", toColumns = new[] { " " } }));
      errors["from"].ShouldStartWith("'shop orders' isn't an entity path, as queries write them");
      errors["fromColumns"].ShouldBe("Name at least one column");
      errors["toColumns[0]"].ShouldBe("A column's name can't be blank");
      (await InvalidAsync(await admin.PostAsync("/api/overlay/relations",
         new { from = "shop.orders", fromColumns = new[] { "customer_id", "status" }, to = "shop.customers", toColumns = new[] { "id" } })))["toColumns"]
         .ShouldBe("A relation has as many columns on each side: 2 from, 1 to");
      (await InvalidAsync(await admin.PostAsync("/api/overlay/relations/validate",
         new { from = "shop.orders", fromColumns = new[] { "x" }, to = "shop.customers" }))).Keys.ShouldContain("toColumns");
      await (await admin.PutAsync("/api/overlay/relations/77", new { relation = Shipments, version = 0 })).ProblemAsync(404, ProblemCodes.NotFound);

      // An update's item is a field of its request, and its problems are named so, whoever finds them.
      int id = (await (await admin.PostAsync("/api/overlay/relations",
         new { from = "shop.orders", fromColumns = new[] { "status" }, to = "shop.customers", toColumns = new[] { "name" } })).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetInt32();
      (await InvalidAsync(await admin.PutAsync($"/api/overlay/relations/{id}",
         new { relation = new { from = "shop orders", fromColumns = new[] { " " }, to = "shop.customers", toColumns = new[] { "name" } }, version = 0 }))).Keys
         .ShouldBe(["relation.from", "relation.fromColumns[0]"], ignoreOrder: true);
      (await InvalidAsync(await admin.PutAsync($"/api/overlay/relations/{id}",
         new { relation = new { from = "shop.orders", to = "shop.customers", toColumns = new[] { "name" } }, version = 0 }))).Keys.ShouldBe(["relation.fromColumns"]);
   }

   [Fact]
   public async Task ARelationIsMadeOnce()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      object relation = new { from = "shop.orders", fromColumns = new[] { " status " }, to = "shop.customers", toColumns = new[] { "name" } };
      JsonElement made = await (await admin.PostAsync("/api/overlay/relations", relation)).JsonAsync(HttpStatusCode.Created);
      made.GetProperty("fromColumns")[0].GetString().ShouldBe("status", "names are trimmed");
      await (await admin.PostAsync("/api/overlay/relations", relation)).ProblemAsync(409, ProblemCodes.OverlayItemExists);
      JsonElement byAnotherPath = await (await admin.PostAsync("/api/overlay/relations",
         new { from = "shop.main.orders", fromColumns = new[] { "status" }, to = "shop.customers", toColumns = new[] { "name" } })).JsonAsync(HttpStatusCode.Created);
      Issues(byAnotherPath).ShouldBe(["GDQ5016 error: The overlay has this relation already, so this one is left out"]);
      JsonElement declared = await (await admin.PostAsync("/api/overlay/relations",
         new { from = "shop.orders", fromColumns = new[] { "customer_id" }, to = "shop.customers", toColumns = new[] { "id" } })).JsonAsync(HttpStatusCode.Created);
      Issues(declared).ShouldHaveSingleItem().ShouldStartWith("GDQ5016 error: The database declares this relation already");
   }

   /// <summary>Trying a change says which items that work now it would break.</summary>
   [Fact]
   public async Task TryingAChangeSaysWhatItBreaks()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      int big = (await (await admin.PostAsync("/api/overlay/virtual-entities", new { name = "reports.big", query = "shop.orders.where(total > 50)" }))
         .JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetInt32();
      int top = (await (await admin.PostAsync("/api/overlay/virtual-entities", new { name = "reports.top", query = "reports.big.take(1)" }))
         .JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetInt32();
      int label = (await (await admin.PostAsync("/api/overlay/entity-settings", new { entity = "reports.big", columns = new[] { new { name = "total", label = "Total" } } }))
         .JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetInt32();

      JsonElement renamed = await (await admin.PostAsync($"/api/overlay/virtual-entities/validate?id={big}", new { name = "reports.large", query = "shop.orders.where(total > 50)" }))
         .JsonAsync(HttpStatusCode.OK);
      Issues(renamed).ShouldBeEmpty();
      renamed.GetProperty("entity").GetProperty("name").GetString().ShouldBe("reports.large");
      List<JsonElement> breaks = [.. renamed.GetProperty("breaks").EnumerateArray()];
      breaks.Select(b => (b.GetProperty("kind").GetString(), b.GetProperty("id").GetInt32())).ShouldBe([("virtualEntity", top), ("entitySettings", label)], ignoreOrder: true);
      Issues(breaks.Single(b => b.GetProperty("kind").GetString() == "entitySettings")).ShouldBe(["GDQ5004 error: There is no entity 'reports.big' (entity settings)"]);
      JsonElement unchanged = await (await admin.PostAsync($"/api/overlay/virtual-entities/validate?id={big}", new { name = "reports.big", query = "shop.orders.where(total > 60)" }))
         .JsonAsync(HttpStatusCode.OK);
      unchanged.GetProperty("breaks").GetArrayLength().ShouldBe(0);
   }

   /// <summary>An item about a source whose schema isn't read says that is why its entity isn't there.</summary>
   [Fact]
   public async Task EntitiesOfSourcesNotReadAreSaidToBe()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      string path = Path.Combine(TestSources.Files(factory), "broken.db");
      await File.WriteAllTextAsync(path, "this isn't a database", TestContext.Current.CancellationToken);
      int id = await TestSources.AddAsync(admin, "broken", "sqlite", new { settings = new { DataSource = path }, isReadOnly = true });
      await TestSources.SettledAsync(admin, id, "failed");
      JsonElement relation = await (await admin.PostAsync("/api/overlay/relations",
         new { from = "shop.orders", fromColumns = new[] { "customer_id" }, to = "Broken.things", toColumns = new[] { "id" } })).JsonAsync(HttpStatusCode.Created);
      Issues(relation).ShouldBe(["GDQ5004 error: There is no entity 'Broken.things' (relation target): Broken's schema isn't read"]);
   }

   /// <summary>Log messages, as written.</summary>
   internal sealed class ListLogger : ILoggerProvider, ILogger
   {
      public ConcurrentQueue<string> Queue { get; } = new();

      public IReadOnlyList<string> Messages => [.. Queue];

      public ILogger CreateLogger(string categoryName) => this;

      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

      public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
      {
         if (IsEnabled(logLevel)) { Queue.Enqueue(formatter(state, exception)); }
      }

      public void Dispose()
      {
      }
   }
}
