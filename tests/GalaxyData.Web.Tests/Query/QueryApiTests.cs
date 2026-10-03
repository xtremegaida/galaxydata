using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using GalaxyData.Web.Problems;
using GalaxyData.Web.Tests.Catalog;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Query;

public sealed class QueryApiTests
{
   private static async Task<(WebAppFactory Factory, TestApi Admin)> ShopAsync()
   {
      WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      await TestSources.AddSqliteAsync(admin, "shop", await TestSources.ShopAsync(factory), readOnly: false);
      return (factory, admin);
   }

   private static async Task<JsonElement> ExecuteAsync(TestApi api, object request) =>
      await (await api.PostAsync("/api/query/execute", request)).JsonAsync(HttpStatusCode.OK);

   /// <summary>The visible columns' values of each row, as text.</summary>
   private static List<string> Rows(JsonElement page)
   {
      List<int> visible = [.. page.GetProperty("schema").GetProperty("columns").EnumerateArray().Where(c => !c.GetProperty("hidden").GetBoolean())
         .Select(c => c.GetProperty("ordinal").GetInt32())];
      return [.. page.GetProperty("rows").EnumerateArray().Select(r => string.Join(" ", visible.Select(o => r.GetProperty("v")[o].ToString())))];
   }

   private static JsonElement Column(JsonElement page, string name) =>
      page.GetProperty("schema").GetProperty("columns").EnumerateArray().First(c => c.GetProperty("name").GetString() == name);

   private static async Task<TestApi> ReaderAsync(WebAppFactory factory, TestApi admin, string name = "lee")
   {
      await admin.CreateUserAsync(name, "read", "first-password-of-a-user");
      return await TestApi.SignedInAsync(factory, name, "first-password-of-a-user", changeTo: "second-password-of-a-user");
   }

   [Fact]
   public async Task QueriesAreCheckedAsTheyAreWritten()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      JsonElement fine = await (await admin.PostAsync("/api/query/validate", new { text = "shop.orders.where(total > $min).select(id, total)" })).JsonAsync(HttpStatusCode.OK);
      fine.GetProperty("success").GetBoolean().ShouldBeTrue("a parameter without a value is taken as null");
      fine.GetProperty("complete").GetBoolean().ShouldBeTrue();
      fine.GetProperty("parameters").GetRawText().ShouldBe("""[{"name":"min","given":false,"type":"decimal(10,2)"}]""", "the type it takes, to ask for");
      fine.GetProperty("columns").EnumerateArray().Select(c => $"{c.GetProperty("name").GetString()} {c.GetProperty("type").GetProperty("text").GetString()}")
         .ShouldBe(["id int64", "total decimal(10,2)"]);

      JsonElement wrong = await (await admin.PostAsync("/api/query/validate", new
      {
         text = "shop.orders.where(total > $min and statuss == 'open')",
         parameters = new[] { new { name = "$min", value = (object)50 } },
      })).JsonAsync(HttpStatusCode.OK);
      wrong.GetProperty("success").GetBoolean().ShouldBeFalse();
      wrong.GetProperty("parameters").GetRawText().ShouldBe("""[{"name":"min","given":true,"type":"int64"}]""", "as given");
      JsonElement diagnostic = wrong.GetProperty("diagnostics")[0];
      (diagnostic.GetProperty("code").GetString(), diagnostic.GetProperty("start").GetInt32(), diagnostic.GetProperty("end").GetInt32()).ShouldBe(("GDQ2001", 35, 42));
      wrong.GetProperty("columns").ValueKind.ShouldBe(JsonValueKind.Null);

      JsonElement unread = await (await admin.PostAsync("/api/query/validate", new { text = "shop.orders.where(" })).JsonAsync(HttpStatusCode.OK);
      unread.GetProperty("diagnostics")[0].GetProperty("code").GetString().ShouldBe("GDQ1001");

      // Where a null doesn't fit, the parameter wants a value before the query can be checked further: no error.
      JsonElement negated = await (await admin.PostAsync("/api/query/validate", new { text = "shop.orders.select(id, x: -$n, d: $since.year())" })).JsonAsync(HttpStatusCode.OK);
      (negated.GetProperty("success").GetBoolean(), negated.GetProperty("complete").GetBoolean()).ShouldBe((true, false));
      JsonElement info = negated.GetProperty("diagnostics").EnumerateArray().Single();
      info.GetProperty("severity").GetString().ShouldBe("info");
      info.GetProperty("message").GetString()!.ShouldStartWith("$n has no value, so this can't be checked yet: ");
      JsonElement given = await (await admin.PostAsync("/api/query/validate", new
      {
         text = "shop.orders.select(id, x: -$n, d: $since.year())",
         parameters = new object[] { new { name = "n", value = 2 }, new { name = "since", type = "date", value = "2026-01-01" } },
      })).JsonAsync(HttpStatusCode.OK);
      (given.GetProperty("success").GetBoolean(), given.GetProperty("complete").GetBoolean()).ShouldBe((true, true));

      // A text too long to run is told so, at once, however many parameters it has.
      string many = "shop.orders.where(" + string.Join(" + ", Enumerable.Range(0, 30_000).Select(i => "$p" + i)) + " > 0)";
      JsonElement tooLong = await (await admin.PostAsync("/api/query/validate", new { text = many })).JsonAsync(HttpStatusCode.OK);
      tooLong.GetProperty("diagnostics")[0].GetProperty("code").GetString().ShouldBe("GDQ1002");
      tooLong.GetProperty("parameters").GetArrayLength().ShouldBe(0);

      JsonElement problem = await (await admin.PostAsync("/api/query/validate", new { text = "shop.orders", parameters = new[] { new { name = "min", type = "dollars", value = (object)1 } } }))
         .ProblemAsync(400, ProblemCodes.InvalidRequest);
      problem.GetProperty("errors").GetProperty("parameters[0].type")[0].GetString()!.ShouldStartWith("'dollars' isn't a type");
      JsonElement untyped = await (await admin.PostAsync("/api/query/validate", new { text = "shop.orders", parameters = new[] { new { name = "v", type = "unknown", value = (object)"x" } } }))
         .ProblemAsync(400, ProblemCodes.InvalidRequest);
      untyped.GetProperty("errors").GetProperty("parameters[0].value")[0].GetString()!.ShouldStartWith("A value of no type can't be given");
   }

   /// <summary>A page of a query's rows: every column (hidden keys too, for links and edits), what each leads to and where a change of it goes.</summary>
   [Fact]
   public async Task PagesOfAQueryHaveTheirColumnsLinksAndEditTargets()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      object request = new
      {
         text = "shop.orders.where(total > $min).select(id, total, customer_id, customer.name)",
         parameters = new[] { new { name = "min", value = (object)10 } },
         grid = new { sort = new[] { new { column = "total", desc = true } }, limit = 2 },
         includeCount = true,
      };
      JsonElement page = await ExecuteAsync(admin, request);
      page.GetProperty("queryText").GetString().ShouldBe("(shop.orders.where(total > $min).select(id, total, customer_id, customer.name)).orderBy(desc(total))");
      Rows(page).ShouldBe(["1001 250.00 1 Acme Ltd", "1002 99.50 1 Acme Ltd"]);
      (page.GetProperty("hasMore").GetBoolean(), page.GetProperty("total").GetInt64()).ShouldBe((true, 3));
      page.GetProperty("parameters").GetRawText().ShouldBe("""[{"name":"min","type":"int64","value":"10"}]""");
      page.GetProperty("stats").GetProperty("fragments").GetArrayLength().ShouldBe(0, "one source's query runs in it, without fragments");
      page.GetProperty("stats").GetProperty("elapsedMs").GetDouble().ShouldBeGreaterThan(0);

      page.GetProperty("schema").GetProperty("rowIdentity").ValueKind.ShouldBe(JsonValueKind.Null, "selected values aren't the orders' rows");
      page.GetProperty("rows")[0].GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null);

      JsonElement name = Column(page, "name");
      JsonElement edit = name.GetProperty("editTarget");
      (edit.GetProperty("entity").GetString(), edit.GetProperty("column").GetString(), edit.GetProperty("canUpdate").GetBoolean()).ShouldBe(("shop.customers", "name", true));
      int customerKey = edit.GetProperty("keyOrdinals")[0].GetInt32();
      JsonElement hidden = page.GetProperty("schema").GetProperty("columns")[customerKey];
      hidden.GetProperty("hidden").GetBoolean().ShouldBeTrue("the customer's key, to change its name by");
      page.GetProperty("rows")[0].GetProperty("v")[customerKey].GetString().ShouldBe("1");
      Column(page, "id").GetProperty("editTarget").GetProperty("canUpdate").GetBoolean().ShouldBeFalse("keys aren't changed");
      JsonElement link = Column(page, "customer_id").GetProperty("link");
      (link.GetProperty("kind").GetString(), link.GetProperty("target").GetString(), link.GetProperty("navigation").GetString()).ShouldBe(("row", "shop.customers", "customer"));

      // Rows that are an entity's are identified by its key, and lead to the rows that refer to each.
      JsonElement orders = await ExecuteAsync(admin, new { text = "shop.orders.where(total > 10)", grid = new { limit = 1 } });
      JsonElement identity = orders.GetProperty("schema").GetProperty("rowIdentity");
      identity.GetProperty("entity").GetString().ShouldBe("shop.orders");
      identity.GetProperty("keyOrdinals").EnumerateArray().Select(o => o.GetInt32()).ShouldBe([Column(orders, "id").GetProperty("ordinal").GetInt32()]);
      identity.GetProperty("related").EnumerateArray().Select(r => r.GetProperty("navigation").GetString()).ShouldContain("order_lines");
      identity.GetProperty("capabilities").GetProperty("canUpdate").GetBoolean().ShouldBeTrue();
      orders.GetProperty("rows")[0].GetProperty("id").GetString().ShouldBe("""["1001"]""");

      TestApi lee = await ReaderAsync(factory, admin);
      Column(await ExecuteAsync(lee, request), "name").GetProperty("editTarget").GetProperty("canUpdate").GetBoolean().ShouldBeFalse("readers change nothing");
   }

   [Theory]
   [InlineData("shop.orders.where(total > $v).select(id)", null, 50, "1001,1002")]
   [InlineData("shop.orders.where(total > $v).select(id)", "decimal(10,2)", "99.5", "1001")]
   [InlineData("shop.orders.where(order_date >= $v).select(id)", null, "2026-02-01", "1003,1004")]
   [InlineData("shop.orders.where(id == $v).select(id)", "int64", "1003", "1003")]
   [InlineData("shop.customers.where(city == $v).select(id)", null, null, "")]
   [InlineData("shop.orders.where(($v ?? 'open') == status).select(id)", null, null, "1001,1003")]
   [InlineData("shop.orders.where(($v ?? 'open') == status).select(id)", "unknown?", null, "1001,1003")]
   public async Task ParametersAreReadAsTheyAreSent(string text, string? type, object? value, string expected)
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      JsonElement page = await ExecuteAsync(admin, new { text, parameters = new[] { new { name = "v", type, value } } });
      string.Join(",", Rows(page)).ShouldBe(expected);
   }

   [Fact]
   public async Task QueriesThatCantRunSayWhy()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      JsonElement syntax = await (await admin.PostAsync("/api/query/execute", new { text = "shop.orders.where(" })).ProblemAsync(400, ProblemCodes.QuerySyntax);
      syntax.GetProperty("diagnostics")[0].GetProperty("code").GetString().ShouldBe("GDQ1001");
      JsonElement missing = await (await admin.PostAsync("/api/query/execute", new { text = "shop.orders.where(total > $min)" })).ProblemAsync(422, ProblemCodes.QueryInvalid);
      missing.GetProperty("diagnostics")[0].GetProperty("code").GetString().ShouldBe("GDQ2012");
      JsonElement where = await (await admin.PostAsync("/api/query/execute", new { text = "shop.orders.select(id, total)", grid = new { where = "status == 'open'" } }))
         .ProblemAsync(422, ProblemCodes.QueryInvalid);
      where.GetProperty("field").GetString().ShouldBe("grid.where", "the rows have no status: the where is the user's");
      await (await admin.PostAsync("/api/query/execute", new { text = "shop.orders", grid = new { limit = 5000 } })).ProblemAsync(400, ProblemCodes.InvalidRequest);
      JsonElement twice = await (await admin.PostAsync("/api/query/execute", new { text = "shop.orders", parameters = new[] { new { name = "a" }, new { name = "$a" } } }))
         .ProblemAsync(400, ProblemCodes.InvalidRequest);
      twice.GetProperty("errors").GetProperty("parameters[1].name")[0].GetString().ShouldBe("$a is given twice");
      await (await admin.PostAsync("/api/query/explain", new { text = "shop.nothing" })).ProblemAsync(422, ProblemCodes.QueryInvalid);
   }

   [Fact]
   public async Task ParametersAreNamedAsTheLanguageNamesThem()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      Rows(await ExecuteAsync(admin, new { text = "shop.orders.where(id == $1).select(id)", parameters = new[] { new { name = "$1", value = 1002 } } })).ShouldBe(["1002"]);
   }

   /// <summary>Warnings are placed in the query as written, whatever the grid composes onto it.</summary>
   [Fact]
   public async Task WarningsArePlacedInTheQueryAsWritten()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      const string text = "total := 5; shop.orders.where(total > 100).select(id, total)";
      JsonElement validated = (await (await admin.PostAsync("/api/query/validate", new { text })).JsonAsync(HttpStatusCode.OK)).GetProperty("diagnostics")[0];
      JsonElement warning = (await ExecuteAsync(admin, new { text, grid = new { sort = new[] { new { column = "id" } } } })).GetProperty("warnings")[0];
      (warning.GetProperty("code").GetString(), warning.GetProperty("start").GetInt32(), warning.GetProperty("end").GetInt32())
         .ShouldBe((validated.GetProperty("code").GetString(), validated.GetProperty("start").GetInt32(), validated.GetProperty("end").GetInt32()));
      text[warning.GetProperty("start").GetInt32()..warning.GetProperty("end").GetInt32()].ShouldBe("total");
   }

   /// <summary>A query of one value, or of the first row, is a page of one row.</summary>
   [Fact]
   public async Task ValuesAndFirstRowsArePagesOfOneRow()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      Rows(await ExecuteAsync(admin, new { text = "shop.orders.count()", includeCount = true })).ShouldBe(["4"]);
      Rows(await ExecuteAsync(admin, new { text = "shop.orders.orderBy(id).select(id, total).first()" })).ShouldBe(["1001 250.00"]);
   }

   [Fact]
   public async Task AggregatesDrillDownToTheirRows()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      string text = "shop.orders.where(total >= $min).groupBy(customer_id).select(customer_id, n: count(), spend: sum(total))";
      object[] parameters = [new { name = "min", value = (object)10 }];
      HttpResponseMessage response = await admin.PostAsync("/api/query/execute", new { text, parameters, grid = new { sort = new[] { new { column = "customer_id" } }, filters = new[] { new { column = "n", conditions = new[] { new { op = "gt", value = 0 } } } } } });
      string version = response.Headers.GetValues(Web.Catalog.CatalogService.VersionHeader).Single();
      JsonElement page = await response.JsonAsync(HttpStatusCode.OK);
      Rows(page).ShouldBe(["1 2 349.50", "2 1 12.25"]);
      JsonElement n = Column(page, "n");
      n.GetProperty("link").GetProperty("kind").GetString().ShouldBe("drillDown");
      JsonElement first = page.GetProperty("rows")[0].GetProperty("v");

      JsonElement drill = await (await admin.PostAsync("/api/query/link", new
      {
         text = page.GetProperty("queryText").GetString(),
         parameters = page.GetProperty("parameters"),
         row = first,
         column = n.GetProperty("ordinal").GetInt32(),
         catalogVersion = version,
      })).JsonAsync(HttpStatusCode.OK);
      drill.GetProperty("browse").ValueKind.ShouldBe(JsonValueKind.Null);
      drill.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ShouldBe(["min", "key1"], "not the grid's filter's");
      await (await admin.PostAsync("/api/query/link", new
      {
         text = page.GetProperty("queryText").GetString(),
         parameters = page.GetProperty("parameters"),
         row = first,
         column = n.GetProperty("ordinal").GetInt32(),
         catalogVersion = "0123456789abcdef",
      })).ProblemAsync(409, ProblemCodes.ConcurrencyConflict);
      JsonElement rows = await ExecuteAsync(admin, new { text = drill.GetProperty("queryText").GetString(), parameters = drill.GetProperty("parameters"), grid = new { sort = new[] { new { column = "id" } } } });
      Rows(rows).Select(r => r.Split(' ')[0]).ShouldBe(["1001", "1002"], "the rows the group of customer 1 was worked out from, with the query's $min");

      JsonElement customer = await (await admin.PostAsync("/api/query/link", new
      {
         text = page.GetProperty("queryText").GetString(),
         parameters = page.GetProperty("parameters"),
         row = first,
         column = Column(page, "customer_id").GetProperty("ordinal").GetInt32(),
      })).JsonAsync(HttpStatusCode.OK);
      customer.GetProperty("browse").GetProperty("entity").GetString().ShouldBe("shop.customers");
      customer.GetProperty("key").GetRawText().ShouldBe("""["1"]""");
   }

   [Fact]
   public async Task CollectionsAndRelatedRowsCanBeBrowsed()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      string text = "shop.customers.select(id, name, orders: orders.count())";
      JsonElement page = await ExecuteAsync(admin, new { text, grid = new { sort = new[] { new { column = "id" } } } });
      Rows(page).ShouldBe(["1 Acme Ltd 2", "2 Beta Corp 1", "3 Gamma Inc 1"]);
      JsonElement orders = Column(page, "orders");
      orders.GetProperty("link").GetProperty("kind").GetString().ShouldBe("collection");
      object Link(int? column, int? related) => new { text = page.GetProperty("queryText").GetString(), row = page.GetProperty("rows")[0].GetProperty("v"), column, related };

      JsonElement followed = await (await admin.PostAsync("/api/query/link", Link(orders.GetProperty("ordinal").GetInt32(), null))).JsonAsync(HttpStatusCode.OK);
      followed.GetProperty("browse").GetRawText().ShouldBe("""{"entity":null,"from":{"entity":"shop.customers","key":["1"]},"navigation":"orders"}""");
      Rows(await ExecuteAsync(admin, new { text = followed.GetProperty("queryText").GetString(), parameters = followed.GetProperty("parameters") })).Count.ShouldBe(2);

      page.GetProperty("schema").GetProperty("rowIdentity").ValueKind.ShouldBe(JsonValueKind.Null, "rows with a count of their orders aren't customers' rows");
      await (await admin.PostAsync("/api/query/link", Link(null, 0))).ProblemAsync(400, ProblemCodes.InvalidRequest);

      // The rows of an entity lead to the rows that refer to each.
      JsonElement customers = await ExecuteAsync(admin, new { text = "shop.customers.where(id == 1)" });
      int addresses = customers.GetProperty("schema").GetProperty("rowIdentity").GetProperty("related").EnumerateArray().ToList()
         .FindIndex(r => r.GetProperty("navigation").GetString() == "addresses");
      JsonElement related = await (await admin.PostAsync("/api/query/link", new
      {
         text = customers.GetProperty("queryText").GetString(),
         row = customers.GetProperty("rows")[0].GetProperty("v"),
         related = addresses,
      })).JsonAsync(HttpStatusCode.OK);
      related.GetProperty("browse").GetProperty("navigation").GetString().ShouldBe("addresses");
      related.GetProperty("browse").GetProperty("from").GetProperty("key").GetRawText().ShouldBe("""["1"]""");

      await (await admin.PostAsync("/api/query/link", Link(null, null))).ProblemAsync(400, ProblemCodes.InvalidRequest);
      await (await admin.PostAsync("/api/query/link", Link(Column(page, "name").GetProperty("ordinal").GetInt32(), null))).ProblemAsync(400, ProblemCodes.InvalidRequest);
      await (await admin.PostAsync("/api/query/link", new { text, row = new[] { "1" }, column = 0 })).ProblemAsync(400, ProblemCodes.InvalidRequest);
   }

   [Fact]
   public async Task ANullForeignKeyLeadsNowhere()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      JsonElement page = await ExecuteAsync(admin, new { text = "shop.orders.where(id == 1004).select(id, ship_address_id)" });
      JsonElement link = await (await admin.PostAsync("/api/query/link", new
      {
         text = page.GetProperty("queryText").GetString(),
         row = page.GetProperty("rows")[0].GetProperty("v"),
         column = Column(page, "ship_address_id").GetProperty("ordinal").GetInt32(),
      })).JsonAsync(HttpStatusCode.OK);
      link.GetProperty("queryText").ValueKind.ShouldBe(JsonValueKind.Null);
   }

   [Fact]
   public async Task QueriesAreExplainedWithoutRunning()
   {
      (WebAppFactory factory, TestApi admin) = await ShopAsync();
      await using WebAppFactory _ = factory;
      JsonElement explain = await (await admin.PostAsync("/api/query/explain", new
      {
         text = "shop.orders.where(total > $min).select(id, customer.name)",
         parameters = new[] { new { name = "min", value = (object)10 } },
         grid = new { sort = new[] { new { column = "id" } }, limit = 2 },
         verbose = true,
      })).JsonAsync(HttpStatusCode.OK);
      explain.GetProperty("queryText").GetString().ShouldBe("(shop.orders.where(total > $min).select(id, customer.name)).orderBy(id)");
      List<JsonElement> nodes = [.. explain.GetProperty("nodes").EnumerateArray()];
      int root = explain.GetProperty("plan").GetInt32();
      nodes[root].GetProperty("id").GetInt32().ShouldBe(root);
      nodes.SelectMany(n => n.GetProperty("inputs").EnumerateArray().Select(i => i.GetInt32())).ShouldAllBe(i => i < root, "inputs come before what reads them");
      JsonElement fragment = explain.GetProperty("fragments").EnumerateArray().Single();
      fragment.GetProperty("source").GetString().ShouldBe("shop");
      fragment.GetProperty("sql").GetString()!.ShouldContain("LIMIT");
      explain.GetProperty("phases").GetArrayLength().ShouldBeGreaterThan(0);
      explain.GetProperty("text").GetString()!.ShouldContain("SELECT");
      explain.GetProperty("schema").GetProperty("columns").GetArrayLength().ShouldBeGreaterThan(2, "hidden keys too");
   }
}
