using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Testing;
using GalaxyData.Web.Catalog;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Problems;
using GalaxyData.Web.Tests.Catalog;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Browse;

/// <summary>Browsing the shop through the API: pages, filters, where, navigations, counts, trails and who may change what.</summary>
public sealed class BrowseApiTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   private static async Task<(WebAppFactory Factory, TestApi Admin)> ShopAsync(Dictionary<string, string?>? settings = null)
   {
      WebAppFactory factory = new() { Settings = settings ?? [] };
      TestApi admin = await TestApi.SignedInAsync(factory);
      await TestSources.AddSqliteAsync(admin, "shop", await TestSources.ShopAsync(factory), readOnly: false);
      return (factory, admin);
   }

   private static async Task<JsonElement> PageAsync(TestApi api, object request) =>
      await (await api.PostAsync("/api/browse/page", request)).JsonAsync(HttpStatusCode.OK);

   private static object Entity(string entity, object? grid = null, bool schema = false, bool count = false) =>
      new { source = new { entity }, grid, includeSchema = schema, includeCount = count };

   private static object Navigation(string entity, object[] key, string navigation, object? grid = null, bool schema = false) =>
      new { source = new { from = new { entity, key }, navigation }, grid, includeSchema = schema };

   /// <summary>The first value of each row (the key, for the shop's tables).</summary>
   private static List<string> Firsts(JsonElement page) => page.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("v")[0].ToString()).ToList();

   private static async Task<List<string>> FilteredAsync(TestApi api, string entity, string column, string op, object? value = null, object? valueTo = null) =>
      Firsts(await PageAsync(api, Entity(entity, new { filters = new[] { new { column, conditions = new[] { new { op, value, valueTo } } } } })));

   [Fact]
   public async Task APageOfAnEntitysRows()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      HttpResponseMessage response = await admin.PostAsync("/api/browse/page", Entity("shop.orders", new { sort = new[] { new { column = "total", desc = true } }, limit = 2 }, schema: true, count: true));
      response.Headers.GetValues(CatalogService.VersionHeader).Single().Length.ShouldBe(16);
      JsonElement page = await response.JsonAsync(HttpStatusCode.OK);
      page.GetProperty("queryText").GetString().ShouldBe("(shop.orders).orderBy(desc(total))");
      page.GetProperty("entity").GetString().ShouldBe("shop.orders");
      page.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
      page.GetProperty("total").GetInt64().ShouldBe(4);
      JsonElement first = page.GetProperty("rows")[0];
      first.GetProperty("id").GetString().ShouldBe("[\"1001\"]");
      first.GetProperty("k").GetRawText().ShouldBe("""["1001"]""");
      first.GetProperty("v").GetRawText().ShouldBe("""["1001","1","1","1","open","250.00","2026-01-05","2026-01-05T08:30:00+00:00"]""");
      first.GetProperty("r").GetRawText().ShouldBe("""["Acme Ltd","1 Main Rd","1 Main Rd"]""");
      page.GetProperty("rows")[1].GetProperty("r").GetRawText().ShouldBe("""["Acme Ltd","1 Main Rd",null]""", "an order without a billing address refers to none");

      JsonElement schema = page.GetProperty("schema");
      schema.GetProperty("key").GetRawText().ShouldBe("""["id"]""");
      schema.GetProperty("capabilities").GetProperty("canUpdate").GetBoolean().ShouldBeTrue();
      schema.GetProperty("columns").EnumerateArray().Select(c => $"{c.GetProperty("name").GetString()} {c.GetProperty("type").GetProperty("text").GetString()} {c.GetProperty("reference")}")
         .ShouldBe(["id int64 ", "customer_id int64 0", "ship_address_id int64? 1", "bill_address_id int64? 2", "status string ", "total decimal(10,2) ",
                    "order_date date ", "placed_at datetimeoffset? "]);
      schema.GetProperty("references").EnumerateArray().Select(r => $"{r.GetProperty("navigation").GetString()} {r.GetProperty("target").GetString()} {r.GetProperty("columns").GetRawText()}")
         .ShouldBe(["customer shop.customers [1]", "ship_address shop.addresses [2]", "bill_address shop.addresses [3]"]);
      schema.GetProperty("collections").EnumerateArray().Select(c => $"{c.GetProperty("navigation").GetString()} {c.GetProperty("multiplicity").GetString()}")
         .ShouldBe(["order_lines many"]);
      JsonElement id = schema.GetProperty("columns")[0];
      id.GetProperty("isKey").GetBoolean().ShouldBeTrue();
      id.GetProperty("canUpdate").GetBoolean().ShouldBeFalse();
      id.GetProperty("lineage").GetRawText().ShouldBe("""{"kind":"direct","sources":[{"column":"shop.orders.id","path":null}],"expression":null}""");

      JsonElement next = await PageAsync(admin, Entity("shop.orders", new { sort = new[] { new { column = "total", desc = true } }, offset = 2, limit = 2 }));
      Firsts(next).ShouldBe(["1003", "1004"]);
      next.GetProperty("hasMore").GetBoolean().ShouldBeFalse();
      next.GetProperty("schema").ValueKind.ShouldBe(JsonValueKind.Null);
      next.GetProperty("total").ValueKind.ShouldBe(JsonValueKind.Null, "not asked for");
   }

   /// <summary>Pages of rows that sort the same come in the same order each time: by their key after the grid's sort.</summary>
   [Fact]
   public async Task PagesAreStable()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      List<string> seen = [];
      for (int offset = 0; offset < 4; offset++)
      {
         seen.AddRange(Firsts(await PageAsync(admin, Entity("shop.orders", new { sort = new[] { new { column = "status", desc = false } }, offset, limit = 1 }))));
      }
      seen.ShouldBe(["1004", "1001", "1003", "1002"], "cancelled, open (by id), shipped");
   }

   [Fact]
   public async Task FiltersFindTheRows()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      (await FilteredAsync(admin, "shop.orders", "status", "eq", "open")).ShouldBe(["1001", "1003"]);
      (await FilteredAsync(admin, "shop.orders", "status", "ne", "open")).ShouldBe(["1002", "1004"]);
      (await FilteredAsync(admin, "shop.orders", "total", "lt", "50")).ShouldBe(["1003", "1004"]);
      (await FilteredAsync(admin, "shop.orders", "total", "le", 12.25)).ShouldBe(["1003", "1004"]);
      (await FilteredAsync(admin, "shop.orders", "total", "gt", "99.5")).ShouldBe(["1001"]);
      (await FilteredAsync(admin, "shop.orders", "total", "ge", "99.50")).ShouldBe(["1001", "1002"]);
      (await FilteredAsync(admin, "shop.orders", "total", "between", "10", "100")).ShouldBe(["1002", "1003"]);
      (await FilteredAsync(admin, "shop.orders", "order_date", "eq", "2026-01-09")).ShouldBe(["1002"]);
      // Offsets are instants: a day is a day in UTC.
      (await FilteredAsync(admin, "shop.orders", "placed_at", "eq", "2026-01-05")).ShouldBe(["1001", "1003"]);
      (await FilteredAsync(admin, "shop.orders", "placed_at", "gt", "2026-01-05")).ShouldBe(["1002"]);
      (await FilteredAsync(admin, "shop.orders", "placed_at", "blank")).ShouldBe(["1004"]);
      (await FilteredAsync(admin, "shop.customers", "name", "contains", "CORP")).ShouldBe(["2"], "ignoring case");
      (await FilteredAsync(admin, "shop.customers", "name", "notContains", "corp")).ShouldBe(["1", "3"]);
      (await FilteredAsync(admin, "shop.customers", "name", "startsWith", "ac")).ShouldBe(["1"]);
      (await FilteredAsync(admin, "shop.customers", "name", "endsWith", "INC")).ShouldBe(["3"]);
      (await FilteredAsync(admin, "shop.customers", "name", "startsWith", "_")).ShouldBeEmpty("'_' is itself, not any character");
      (await FilteredAsync(admin, "shop.customers", "city", "blank")).ShouldBe(["3"]);
      (await FilteredAsync(admin, "shop.customers", "city", "notBlank")).ShouldBe(["1", "2"]);

      var either = new { column = "status", any = true, conditions = new[] { new { op = "eq", value = (object)"shipped" }, new { op = "eq", value = (object)"cancelled" } } };
      var big = new { column = "total", any = false, conditions = new[] { new { op = "gt", value = (object)"1" } } };
      Firsts(await PageAsync(admin, Entity("shop.orders", new { filters = new object[] { either, big } }))).ShouldBe(["1002"]);

      JsonElement problem = await (await admin.PostAsync("/api/browse/page", Entity("shop.orders", new { filters = new[] { new { column = "totl", conditions = new[] { new { op = "eq", value = "1" } } } } })))
         .ProblemAsync(400, ProblemCodes.InvalidRequest);
      problem.GetProperty("errors").GetProperty("grid.filters[0].column")[0].GetString().ShouldBe("The rows have no column 'totl'");
   }

   [Fact]
   public async Task TheWhereExpressionFiltersToo()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      JsonElement page = await PageAsync(admin, Entity("shop.orders", new { where = "total > 50 and customer.city == 'Cape Town' // big local ones" }));
      Firsts(page).ShouldBe(["1001", "1002"]);
      page.GetProperty("queryText").GetString().ShouldBe("(shop.orders).where((total > 50 and customer.city == 'Cape Town' // big local ones\n))");

      JsonElement problem = await (await admin.PostAsync("/api/browse/page", Entity("shop.orders", new { where = "   total > 50 and customr.city == 'x'" })))
         .ProblemAsync(422, ProblemCodes.QueryInvalid);
      problem.GetProperty("field").GetString().ShouldBe("grid.where");
      JsonElement diagnostic = problem.GetProperty("diagnostics")[0];
      (diagnostic.GetProperty("start").GetInt32(), diagnostic.GetProperty("end").GetInt32()).ShouldBe((18, 25), "where 'customr' is in the where expression as sent");

      problem = await (await admin.PostAsync("/api/browse/page", Entity("shop.orders", new { where = "x := 1; true" }))).ProblemAsync(400, ProblemCodes.InvalidRequest);
      problem.GetProperty("errors").GetProperty("grid.where")[0].GetString().ShouldBe("Write one condition, without ';'");
      await (await admin.PostAsync("/api/browse/page", Entity("shop.orders", new { where = "total" }))).ProblemAsync(422, ProblemCodes.QueryInvalid);
   }

   [Fact]
   public async Task NavigationsLeadToTheirRows()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      Firsts(await PageAsync(admin, Navigation("shop.customers", ["1"], "orders"))).ShouldBe(["1001", "1002"]);
      JsonElement customer = await PageAsync(admin, Navigation("shop.orders", [1003], "customer", schema: true));
      Firsts(customer).ShouldBe(["2"]);
      customer.GetProperty("entity").GetString().ShouldBe("shop.customers");
      customer.GetProperty("schema").GetProperty("capabilities").GetProperty("canUpdate").GetBoolean().ShouldBeTrue("the rows a navigation leads to are the target's own");
      JsonElement lines = await PageAsync(admin, Navigation("shop.orders", ["1001"], "order_lines"));
      lines.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ShouldBe(["[\"1001\",\"1\"]", "[\"1001\",\"2\"]"]);
      lines.GetProperty("parameters").GetRawText().ShouldBe("""[{"name":"key1","type":"int64","value":"1001"}]""");
      Firsts(await PageAsync(admin, Navigation("shop.employees", ["2"], "manager"))).ShouldBe(["1"]);
      Firsts(await PageAsync(admin, Navigation("shop.employees", ["1"], "employees_by_manager", new { sort = new[] { new { column = "name", desc = true } } }))).ShouldBe(["3", "2"]);
      Firsts(await PageAsync(admin, Navigation("shop.orders", ["1004"], "ship_address"))).ShouldBeEmpty("an order shipped nowhere leads to no address");
      Firsts(await PageAsync(admin, Navigation("shop.orders", ["9999"], "customer"))).ShouldBeEmpty();

      async Task<string> Invalid(object request, string field) =>
         (await (await admin.PostAsync("/api/browse/page", request)).ProblemAsync(400, ProblemCodes.InvalidRequest)).GetProperty("errors").GetProperty(field)[0].GetString()!;
      (await Invalid(Navigation("shop.orders", ["1001"], "nothing"), "source.navigation")).ShouldBe("shop.orders has no navigation 'nothing'");
      (await Invalid(Navigation("shop.orders", ["x"], "customer"), "source.from.key")).ShouldBe("'id': \"x\" isn't a whole number");
      (await Invalid(Navigation("shop.audit_log", ["x"], "anything"), "source.from.key")).ShouldBe("shop.audit_log has no key, so its rows can't be told apart");
      (await Invalid(new { source = new { entity = "shop.orders", navigation = "customer" } }, "source")).ShouldBe("Browse an entity, or a navigation from a row: not both");
      await (await admin.PostAsync("/api/browse/page", Entity("shop.nothing"))).ProblemAsync(404, ProblemCodes.NotFound);
      await (await admin.PostAsync("/api/browse/page", Entity("shop.orders", new { limit = 1001 }))).ProblemAsync(400, ProblemCodes.InvalidRequest);
      // Nulls in lists, which validation doesn't look at.
      (await Invalid(Entity("shop.orders", new { filters = new object?[] { null } }), "grid.filters[0]")).ShouldBe("A filter can't be null");
      (await Invalid(Entity("shop.orders", new { filters = new[] { new { column = "id", conditions = new object?[] { null } } } }), "grid.filters[0].conditions[0]"))
         .ShouldBe("A condition can't be null");
      (await Invalid(Entity("shop.orders", new { sort = new object?[] { null } }), "grid.sort[0]")).ShouldBe("A sort key can't be null");
      (await (await admin.PostAsync("/api/browse/trail", new { crumbs = new object?[] { new { entity = "shop.orders" }, null } })).ProblemAsync(400, ProblemCodes.InvalidRequest))
         .GetProperty("errors").GetProperty("crumbs[1]")[0].GetString().ShouldBe("A crumb can't be null");
   }

   /// <summary>A foreign key a database doesn't make unique (SQLite takes one to any columns) leads to more than one row: its rows aren't repeated for it.</summary>
   [Fact]
   public async Task AReferenceToRowsThatArentOneIsntShown()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      string path = Path.Combine(TestSources.Files(factory), "tags.db");
      await TestSources.SqliteAsync(path, "PRAGMA foreign_keys = OFF; CREATE TABLE labels (id INTEGER PRIMARY KEY, tag TEXT); CREATE TABLE tagged (id INTEGER PRIMARY KEY, tag TEXT REFERENCES labels(tag)); " +
         "INSERT INTO labels VALUES (1, 'x'), (2, 'x'); INSERT INTO tagged VALUES (1, 'x')");
      await TestSources.AddSqliteAsync(admin, "tags", path);
      JsonElement page = await PageAsync(admin, Entity("tags.tagged", schema: true, count: true));
      Firsts(page).ShouldBe(["1"]);
      page.GetProperty("total").GetInt64().ShouldBe(1);
      page.GetProperty("schema").GetProperty("references").GetArrayLength().ShouldBe(0);
   }

   [Fact]
   public async Task PagesAreAsLargeAsAllowed()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync(new Dictionary<string, string?> { ["GalaxyData:Query:MaxPageSize"] = "3" });
      await using WebAppFactory _ = factory;
      JsonElement page = await PageAsync(admin, Entity("shop.orders", count: true));
      page.GetProperty("rows").GetArrayLength().ShouldBe(3, "the page the settings allow, when the grid doesn't say");
      page.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
      page.GetProperty("total").GetInt64().ShouldBe(4);
      JsonElement last = await PageAsync(admin, Entity("shop.orders", new { offset = 3 }, count: true));
      last.GetProperty("total").GetInt64().ShouldBe(4, "the last page tells the count");
      last.GetProperty("hasMore").GetBoolean().ShouldBeFalse();
   }

   /// <summary>Readers browse, and are told they can't change what they see.</summary>
   [Fact]
   public async Task ReadersBrowseButDontChange()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      await admin.CreateUserAsync("rae", nameof(UserRole.Read), "first-password-of-a-user");
      TestApi rae = await TestApi.SignedInAsync(factory, "rae", "first-password-of-a-user", changeTo: "second-password-of-a-user");
      JsonElement schema = (await PageAsync(rae, Entity("shop.customers", schema: true))).GetProperty("schema");
      schema.GetProperty("capabilities").GetProperty("canInsert").GetBoolean().ShouldBeFalse();
      schema.GetProperty("columns").EnumerateArray().ShouldAllBe(c => !c.GetProperty("canUpdate").GetBoolean() && c.GetProperty("insert").GetString() == "never");
      (await rae.SendAsync(HttpMethod.Post, "/api/browse/page", Entity("shop.customers"), withToken: false)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "a POST needs the anti-forgery token");
   }

   [Fact]
   public async Task ATrailSaysWhereItLeads()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      async Task<string> Trail(params object[] crumbs)
      {
         JsonElement trail = await (await admin.PostAsync("/api/browse/trail", new { crumbs })).JsonAsync(HttpStatusCode.OK);
         return string.Join(" / ", trail.GetProperty("crumbs").EnumerateArray().Select(c =>
            $"{c.GetProperty("label").GetString()}: {c.GetProperty("entity")} {c.GetProperty("title")} {c.GetProperty("found")} {c.GetProperty("problem")}".TrimEnd()));
      }
      (await Trail(new { entity = "shop.customers", key = new[] { "1" } }, new { navigation = "orders", key = new[] { "1002" } }, new { navigation = "order_lines" }))
         .ShouldBe("shop.customers: shop.customers Acme Ltd True / orders: shop.orders shipped True / order_lines: shop.order_lines");
      (await Trail(new { entity = "shop.customers", key = new[] { "77" } })).ShouldBe("shop.customers: shop.customers  False");
      // An order of another customer isn't one of this one's.
      (await Trail(new { entity = "shop.customers", key = new[] { "1" } }, new { navigation = "orders", key = new[] { "1003" } }))
         .ShouldBe("shop.customers: shop.customers Acme Ltd True / orders: shop.orders  False");
      (await Trail(new { entity = "shop.customers", key = new[] { "1" } }, new { navigation = "nothing" }, new { navigation = "more" }))
         .ShouldBe("shop.customers: shop.customers Acme Ltd True / nothing:    shop.customers has no navigation 'nothing'");
      (await Trail(new { entity = "shop.customers" }, new { navigation = "orders" })).ShouldBe("shop.customers: shop.customers / orders:    No row is chosen in the crumb before");
      (await Trail(new { entity = "shop.nothing" })).ShouldBe("shop.nothing:    There is no entity shop.nothing");
   }

   /// <summary>Counting that takes longer than allowed leaves the total out; the rows come all the same.</summary>
   [Fact]
   public async Task CountingThatTakesTooLongIsLeftOut()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync(new Dictionary<string, string?> { ["GalaxyData:Query:CountTimeout"] = "00:00:00.200" });
      await using WebAppFactory _ = factory;
      string warehouse = Path.Combine(TestSources.Files(factory), "big.duckdb");
      await TestSources.DuckDbAsync(warehouse, "CREATE VIEW numbers AS SELECT r FROM range(5000000000) t(r) WHERE r % 7 = 3");
      await TestSources.SettledAsync(admin, await TestSources.AddAsync(admin, "big", "duckdb", new { settings = new { DataSource = warehouse } }), "ready");
      JsonElement page = await PageAsync(admin, Entity("big.numbers", new { limit = 3 }, count: true));
      page.GetProperty("rows").GetArrayLength().ShouldBe(3);
      page.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
      page.GetProperty("total").ValueKind.ShouldBe(JsonValueKind.Null);
      page.GetProperty("rows")[0].GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null, "a view's rows have no key");
   }

   [Fact]
   public async Task ASheetIsBrowsedButNotChanged()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      string books = Path.Combine(TestSources.Files(factory), "books");
      Directory.CreateDirectory(books);
      new XlsxBuilder().Sheet("Budget", XlsxBuilder.Row("Item", "Amount"), XlsxBuilder.Row("Rent", 1200), XlsxBuilder.Row("Food", 450.5)).Save(Path.Combine(books, "home.xlsx"));
      await TestSources.SettledAsync(admin, await TestSources.AddAsync(admin, "xl", "excel", new { settings = new { Folder = books } }), "ready");
      JsonElement page = await PageAsync(admin, Entity("xl.home.Budget", new { filters = new[] { new { column = "Amount", conditions = new[] { new { op = "gt", value = 500 } } } } },
         schema: true, count: true));
      page.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("v").GetRawText()).ShouldBe(["""["Rent",1200]"""]);
      page.GetProperty("total").GetInt64().ShouldBe(1);
      page.GetProperty("schema").GetProperty("key").ValueKind.ShouldBe(JsonValueKind.Null);
      page.GetProperty("schema").GetProperty("capabilities").GetProperty("insertReason").GetString().ShouldBe("xl.home.Budget can't be changed: xl (excel) takes no changes");
   }
}
