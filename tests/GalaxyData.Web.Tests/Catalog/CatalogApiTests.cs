using System;
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
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Catalog;

/// <summary>The catalog through the API: its tree, search, entities and what users may do with them, and its version.</summary>
public sealed class CatalogApiTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   private static async Task<JsonElement> ChildrenAsync(TestApi api, string? parent = null) =>
      (await (await api.GetAsync("/api/catalog/tree/children" + (parent == null ? string.Empty : "?parent=" + Uri.EscapeDataString(parent)))).JsonAsync(HttpStatusCode.OK))
         .GetProperty("nodes");

   private static List<string> Nodes(JsonElement nodes) =>
      nodes.EnumerateArray().Select(n => $"{n.GetProperty("kind").GetString()} {n.GetProperty("id").GetString()}").ToList();

   private static async Task<JsonElement> EntityAsync(TestApi api, string name) =>
      await (await api.GetAsync("/api/catalog/entity?name=" + Uri.EscapeDataString(name))).JsonAsync(HttpStatusCode.OK);

   private static async Task<JsonElement> SearchAsync(TestApi api, string text, int? take = null) =>
      await (await api.GetAsync($"/api/catalog/tree/search?text={Uri.EscapeDataString(text)}{(take == null ? string.Empty : $"&take={take}")}")).JsonAsync(HttpStatusCode.OK);

   /// <summary>A SQLite shop, a DuckDB warehouse with a second schema (and a table whose name is a schema's), a folder of workbooks, and a server that isn't there.</summary>
   private static async Task<TestApi> SourcesAsync(WebAppFactory factory)
   {
      TestApi admin = await TestApi.SignedInAsync(factory);
      await TestSources.AddSqliteAsync(admin, "shop", await TestSources.ShopAsync(factory), readOnly: false);
      string warehouse = Path.Combine(TestSources.Files(factory), "wh.duckdb");
      await TestSources.DuckDbAsync(warehouse, "CREATE SCHEMA sales; CREATE TABLE sales.facts (id INTEGER PRIMARY KEY, customer_id INTEGER, amount DECIMAL(10,2)); " +
         "CREATE TABLE products (code VARCHAR PRIMARY KEY, name VARCHAR); CREATE TABLE main.sales (id INTEGER); " +
         "INSERT INTO sales.facts VALUES (1, 1, 10.5), (2, 1, 4.5), (3, 2, 7)");
      await TestSources.SettledAsync(admin, await TestSources.AddAsync(admin, "wh", "duckdb", new { settings = new { DataSource = warehouse } }), "ready");
      string books = Path.Combine(TestSources.Files(factory), "books");
      Directory.CreateDirectory(books);
      new XlsxBuilder()
         .Sheet("Sheet 1", XlsxBuilder.Row("Item", "Amount"), XlsxBuilder.Row("Rent", 1200), XlsxBuilder.Row("Food", 450.5))
         .Sheet("Notes", XlsxBuilder.Row("Note"), XlsxBuilder.Row("Draft"))
         .Save(Path.Combine(books, "Budget 2024.xlsx"));
      await TestSources.SettledAsync(admin, await TestSources.AddAsync(admin, "xl", "excel", new { settings = new { Folder = books } }), "ready");
      await TestSources.SettledAsync(admin, await TestSources.AddAsync(admin, "far", "postgres", new { mode = "raw", connectionString = "Host=127.0.0.1;Port=1;Database=x" }), "failed");
      return admin;
   }

   [Fact]
   public async Task TheTreeShowsTheSourcesAndWhatIsInThem()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await SourcesAsync(factory);
      JsonElement roots = await ChildrenAsync(admin);
      Nodes(roots).ShouldBe(["source far", "source shop", "source wh", "source xl"]);
      JsonElement far = roots[0];
      far.GetProperty("status").GetString().ShouldBe("failed");
      far.GetProperty("sourceKind").GetString().ShouldBe("postgres");
      far.GetProperty("hasChildren").GetBoolean().ShouldBeFalse();
      roots[1].GetProperty("isReadOnly").GetBoolean().ShouldBeFalse();
      roots[3].GetProperty("isReadOnly").GetBoolean().ShouldBeTrue("folders of workbooks always are");

      Nodes(await ChildrenAsync(admin, "shop")).ShouldBe(
      [
         "table shop.addresses", "table shop.audit_log", "table shop.customers", "table shop.employees", "view shop.open_orders", "table shop.orders", "table shop.order_lines",
      ]);
      // The default schema's tables are the source's own, as queries name them; a table whose name is a schema's is under its schema.
      Nodes(await ChildrenAsync(admin, "wh")).ShouldBe(["schema wh.main", "schema wh.sales", "table wh.products"]);
      Nodes(await ChildrenAsync(admin, "wh.main")).ShouldBe(["table wh.main.sales"]);
      Nodes(await ChildrenAsync(admin, "wh.sales")).ShouldBe(["table wh.sales.facts"]);
      Nodes(await ChildrenAsync(admin, "xl")).ShouldBe(["schema xl['Budget 2024']"]);
      JsonElement sheets = await ChildrenAsync(admin, "xl['Budget 2024']");
      Nodes(sheets).ShouldBe(["table xl['Budget 2024'].Notes", "table xl['Budget 2024']['Sheet 1']"]);
      sheets[1].GetProperty("name").GetString().ShouldBe("Sheet 1");
      sheets[1].GetProperty("editable").GetBoolean().ShouldBeFalse();
      (await ChildrenAsync(admin, "far")).GetArrayLength().ShouldBe(0);

      JsonElement orders = (await ChildrenAsync(admin, "shop")).EnumerateArray().Single(n => n.GetProperty("name").GetString() == "orders");
      orders.GetProperty("editable").GetBoolean().ShouldBeTrue();
      orders.GetProperty("hasChildren").GetBoolean().ShouldBeFalse();
      await (await admin.GetAsync("/api/catalog/tree/children?parent=shop.nothing")).ProblemAsync(404, ProblemCodes.NotFound);

      JsonElement catalog = await (await admin.GetAsync("/api/catalog")).JsonAsync(HttpStatusCode.OK);
      catalog.GetProperty("sources").EnumerateArray().Select(s => $"{s.GetProperty("alias").GetString()} {s.GetProperty("status").GetString()} {s.GetProperty("entities").GetInt32()}")
         .ShouldBe(["shop ready 7", "wh ready 3", "xl ready 2", "far failed 0"]);
      catalog.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("subject").GetString()).ShouldContain("wh.main.sales");
   }

   [Fact]
   public async Task SearchFindsByNamePathAndColumn()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await SourcesAsync(factory);
      static List<string> Ids(JsonElement found) => found.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("node").GetProperty("id").GetString()!).ToList();

      JsonElement found = await SearchAsync(admin, " order ");
      found.GetProperty("text").GetString().ShouldBe("order");
      Ids(found).ShouldBe(["shop.orders", "shop.order_lines", "shop.open_orders"], "named so first, then the shorter");
      found.GetProperty("more").GetBoolean().ShouldBeFalse();
      Ids(await SearchAsync(admin, "SHOP.ORD")).ShouldBe(["shop.orders", "shop.order_lines"]);
      Ids(await SearchAsync(admin, "wh.sales")).ShouldBe(["wh.sales", "wh.sales.facts"], "the node at the path first, then those under it");
      Ids(await SearchAsync(admin, "h.sales")).ShouldBe(["wh.sales", "wh.sales.facts"]);

      JsonElement byColumn = (await SearchAsync(admin, "credit")).GetProperty("hits").EnumerateArray().Single();
      byColumn.GetProperty("node").GetProperty("id").GetString().ShouldBe("shop.customers");
      byColumn.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ShouldBe(["credit_limit"]);
      byColumn.GetProperty("path").EnumerateArray().Select(p => p.GetString()).ShouldBe(["shop"]);

      JsonElement facts = (await SearchAsync(admin, "facts")).GetProperty("hits")[0];
      facts.GetProperty("node").GetProperty("id").GetString().ShouldBe("wh.sales.facts");
      facts.GetProperty("path").EnumerateArray().Select(p => p.GetString()).ShouldBe(["wh", "wh.sales"]);
      facts.TryGetProperty("columns", out JsonElement none).ShouldBeTrue();
      none.ValueKind.ShouldBe(JsonValueKind.Null);

      JsonElement sales = await SearchAsync(admin, "sales");
      Ids(sales).ShouldBe(["wh.main.sales", "wh.sales"]);
      Ids(await SearchAsync(admin, "Sheet")).ShouldBe(["xl['Budget 2024']['Sheet 1']"]);

      JsonElement first = await SearchAsync(admin, "o", take: 2);
      first.GetProperty("hits").GetArrayLength().ShouldBe(2);
      first.GetProperty("more").GetBoolean().ShouldBeTrue();

      await (await admin.GetAsync("/api/catalog/tree/search?text=%20")).ProblemAsync(400, ProblemCodes.InvalidRequest);
      await (await admin.GetAsync("/api/catalog/tree/search?text=x&take=0")).ProblemAsync(400, ProblemCodes.InvalidRequest);
      await (await admin.GetAsync($"/api/catalog/tree/search?text={new string('x', 201)}")).ProblemAsync(400, ProblemCodes.InvalidRequest);
   }

   [Fact]
   public async Task AnEntityIsDescribedWithWhatTheUserMayDo()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await SourcesAsync(factory);
      JsonElement orders = await EntityAsync(admin, "shop.orders");
      orders.GetProperty("name").GetString().ShouldBe("shop.orders");
      orders.GetProperty("qualifiedName").GetString().ShouldBe("shop.main.orders");
      orders.GetProperty("kind").GetString().ShouldBe("table");
      orders.GetProperty("source").GetString().ShouldBe("shop");
      orders.GetProperty("schema").GetString().ShouldBe("main");
      orders.GetProperty("table").GetString().ShouldBe("orders");
      orders.GetProperty("hasTriggers").GetBoolean().ShouldBeTrue();
      orders.GetProperty("key").GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ShouldBe(["id"]);
      orders.GetProperty("capabilities").GetRawText().ShouldBe("""{"canInsert":true,"canUpdate":true,"canDelete":true,"insertReason":null,"changeReason":null}""");
      Dictionary<string, JsonElement> columns = orders.GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!);
      columns.Keys.ShouldBe(["id", "customer_id", "ship_address_id", "bill_address_id", "status", "total", "order_date", "placed_at"]);
      string Can(string name) =>
         $"{columns[name].GetProperty("canUpdate").GetBoolean()} {columns[name].GetProperty("insert").GetString()} {columns[name].GetProperty("readOnlyReason").GetString()}";
      Can("id").ShouldBe("False optional 'id' is part of the key, which can't change: delete the row and insert it again");
      Can("customer_id").ShouldBe("True required ");
      Can("ship_address_id").ShouldBe("True optional ");
      Can("status").ShouldBe("True optional ", "it has a default");
      columns["total"].GetProperty("type").GetRawText().ShouldBe("""{"kind":"decimal","nullable":false,"text":"decimal(10,2)","precision":10,"scale":2,"length":null,"isAnsi":null}""");
      columns["placed_at"].GetProperty("type").GetProperty("kind").GetString().ShouldBe("dateTimeOffset");
      columns["id"].GetProperty("isIdentity").GetBoolean().ShouldBeTrue("SQLite's rowid");
      orders.GetProperty("navigations").EnumerateArray().Select(n => $"{n.GetProperty("name").GetString()} {n.GetProperty("target").GetString()} {n.GetProperty("multiplicity").GetString()}")
         .ShouldBe(["customer shop.customers zeroOrOne", "ship_address shop.addresses zeroOrOne", "bill_address shop.addresses zeroOrOne", "order_lines shop.order_lines many"],
            ignoreOrder: true);

      JsonElement log = await EntityAsync(admin, "shop.audit_log");
      log.GetProperty("key").ValueKind.ShouldBe(JsonValueKind.Null);
      log.GetProperty("capabilities").GetProperty("canInsert").GetBoolean().ShouldBeTrue();
      log.GetProperty("capabilities").GetProperty("changeReason").GetString()
         .ShouldBe("shop.audit_log has no primary key, so its rows can't be told apart: they can be inserted, but not changed or deleted");
      (await EntityAsync(admin, "shop.open_orders")).GetProperty("capabilities").GetProperty("insertReason").GetString()
         .ShouldBe("shop.open_orders is a view: only tables can be changed");
      JsonElement sheet = await EntityAsync(admin, "xl[\"Budget 2024\"][\"Sheet 1\"]");
      sheet.GetProperty("capabilities").GetProperty("insertReason").GetString().ShouldBe("xl['Budget 2024']['Sheet 1'] can't be changed: xl (excel) takes no changes");
      sheet.GetProperty("columns").EnumerateArray().Select(c => $"{c.GetProperty("name").GetString()} {c.GetProperty("type").GetProperty("text").GetString()}")
         .ShouldBe(["Item string?", "Amount double?"]);
      (await EntityAsync(admin, "wh.sales.facts")).GetProperty("capabilities").GetProperty("changeReason").GetString().ShouldBe("wh.sales.facts can't be changed: wh is read-only");
      (await EntityAsync(admin, "SHOP.Orders")).GetProperty("name").GetString().ShouldBe("shop.orders", "names ignore case where they are unique");

      await (await admin.GetAsync("/api/catalog/entity?name=shop.nothing")).ProblemAsync(404, ProblemCodes.NotFound);
      await (await admin.GetAsync("/api/catalog/entity?name=shop")).ProblemAsync(404, ProblemCodes.NotFound);
      await (await admin.GetAsync("/api/catalog/entity?name=1%2B")).ProblemAsync(400, ProblemCodes.InvalidRequest);

      // Readers read; data managers change.
      await admin.CreateUserAsync("rae", nameof(UserRole.Read), "first-password-of-a-user");
      TestApi rae = await TestApi.SignedInAsync(factory, "rae", "first-password-of-a-user", changeTo: "second-password-of-a-user");
      JsonElement read = await EntityAsync(rae, "shop.orders");
      read.GetProperty("capabilities").GetRawText()
         .ShouldBe($$"""{"canInsert":false,"canUpdate":false,"canDelete":false,"insertReason":"{{EntityCapabilities.ReadOnlyRole}}","changeReason":"{{EntityCapabilities.ReadOnlyRole}}"}""");
      read.GetProperty("columns").EnumerateArray().ShouldAllBe(c => !c.GetProperty("canUpdate").GetBoolean() && c.GetProperty("insert").GetString() == "never");
      (await ChildrenAsync(rae, "shop")).EnumerateArray().ShouldAllBe(n => !n.GetProperty("editable").GetBoolean());
      await admin.CreateUserAsync("dee", nameof(UserRole.DataManager), "first-password-of-a-user");
      TestApi dee = await TestApi.SignedInAsync(factory, "dee", "first-password-of-a-user", changeTo: "second-password-of-a-user");
      (await EntityAsync(dee, "shop.orders")).GetProperty("capabilities").GetProperty("canUpdate").GetBoolean().ShouldBeTrue();
   }

   /// <summary>Binary values aren't edited here; a table that needs one takes no new rows.</summary>
   [Fact]
   public async Task BinaryColumnsAreReadOnly()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      string path = Path.Combine(TestSources.Files(factory), "files.db");
      await TestSources.SqliteAsync(path, "CREATE TABLE blobs (id INTEGER PRIMARY KEY, name TEXT, body BLOB NOT NULL, " +
         "size INTEGER GENERATED ALWAYS AS (length(body)) VIRTUAL)");
      await TestSources.AddSqliteAsync(admin, "f", path, readOnly: false);
      JsonElement blobs = await EntityAsync(admin, "f.blobs");
      blobs.GetProperty("capabilities").GetProperty("canInsert").GetBoolean().ShouldBeFalse();
      blobs.GetProperty("capabilities").GetProperty("insertReason").GetString()
         .ShouldBe("'body' holds binary values, which can't be edited here, and every new row needs a value for it");
      blobs.GetProperty("capabilities").GetProperty("canUpdate").GetBoolean().ShouldBeTrue();
      Dictionary<string, JsonElement> columns = blobs.GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!);
      columns["name"].GetProperty("canUpdate").GetBoolean().ShouldBeTrue();
      columns["body"].GetProperty("canUpdate").GetBoolean().ShouldBeFalse();
      columns["size"].GetProperty("readOnlyReason").GetString().ShouldBe("'size' is computed: the database works out its value");
   }

   /// <summary>Answers carry the catalog's version: the same while nothing changes, and after a restart; another after a change.</summary>
   [Fact]
   public async Task TheCatalogsVersionChangesWithIt()
   {
      await using SharedData data = new();
      string version;
      await using (WebAppFactory first = new() { DataDirectory = data.Path })
      {
         TestApi admin = await TestApi.SignedInAsync(first);
         int id = await TestSources.AddSqliteAsync(admin, "shop", await TestSources.ShopAsync(first));
         HttpResponseMessage response = await admin.GetAsync("/api/catalog");
         version = response.Headers.GetValues(CatalogService.VersionHeader).Single();
         (await response.JsonAsync(HttpStatusCode.OK)).GetProperty("version").GetString().ShouldBe(version);
         version.Length.ShouldBe(16);
         string? other = null;
         for (int attempt = 0; attempt < 50 && other == null; attempt++)
         {
            // The reading of the schema may make it stale after it said it was done: its version is the same once built again.
            (await admin.GetAsync("/api/catalog/tree/children")).Headers.GetValues(CatalogService.VersionHeader).Single().ShouldBe(version);
            other = (await admin.GetAsync("/api/users")).Headers.TryGetValues(CatalogService.VersionHeader, out IEnumerable<string>? values) ? values.Single() : null;
         }
         other.ShouldBe(version, "answers that don't read the catalog carry its version too, while it is up to date");

         await (await admin.PutAsync($"/api/connections/{id}", new { version = 0, displayName = "The shop", connection = new { settings = new { DataSource = Path.Combine(TestSources.Files(first), "shop.db") } } }))
            .JsonAsync(HttpStatusCode.OK);
         (await admin.GetAsync("/api/users")).Headers.Contains(CatalogService.VersionHeader).ShouldBeFalse("the catalog isn't built again until it is read");
         string changed = (await admin.GetAsync("/api/catalog")).Headers.GetValues(CatalogService.VersionHeader).Single();
         changed.ShouldNotBe(version);
         version = changed;
      }
      await WebAppFactory.ReleasedAsync(data.Path);
      await using WebAppFactory second = new() { DataDirectory = data.Path };
      TestApi again = await TestApi.SignedInAsync(second);
      (await again.GetAsync("/api/catalog")).Headers.GetValues(CatalogService.VersionHeader).Single().ShouldBe(version);
   }
}
