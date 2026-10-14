using System;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Driver.ADO;
using GalaxyData.Query.ClickHouse;
using GalaxyData.Query.Providers;
using GalaxyData.Testing;
using GalaxyData.Web.Tests.Catalog;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Connections;

/// <summary>
/// A HyperWhirlpool connection, to a stand-in for the warehouse's query service in front of the ClickHouse server the
/// container tests use (<c>servers.sh up</c>, or <c>GDQ_TEST_CLICKHOUSE</c>): tried with its query key and a wrong one,
/// which isn't repeated, and its model read into the catalog from the warehouse's schema document. Skipped when the
/// server can't be reached, unless it was named.
/// </summary>
public sealed class HyperWhirlpoolConnectionTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   [Fact]
   public async Task AConnectionIsTriedWithItsKeyAndTheModelRead()
   {
      string server = TestServers.ClickHouse;
      await using ClickHouseDataSource admin = ClickHouseSourceProvider.CreateDataSource(server);
      try
      {
         await using DbConnection probe = await admin.OpenConnectionAsync(Token);
         await probe.ExecuteAsync("SELECT 1", Token);
      }
      catch (DbException e)
      {
         if (TestServers.Named("GDQ_TEST_CLICKHOUSE") != null) { Assert.Fail($"clickhouse can't be reached: {e.Message}"); }
         Assert.Skip($"clickhouse isn't running ({e.Message}); start the servers with tests/GalaxyData.Query.ContainerTests/servers.sh up");
      }
      // The application reads the catalog the warehouse names hw_catalog.
      await using HyperWhirlpoolWarehouse warehouse = await HyperWhirlpoolWarehouse.CreateAsync(admin.CreateConnection, "hw_catalog", Token);
      await using FakeQueryService service = await FakeQueryService.StartAsync(server);
      string connectionString = $"Host=127.0.0.1;Port={service.Address.Port};Protocol=http;Database={warehouse.Web};Password={FakeQueryService.Key}";

      await using WebAppFactory factory = new();
      TestApi api = await TestApi.SignedInAsync(factory);
      int id = await TestSources.AddAsync(api, "hw", "hyperwhirlpool", new { mode = "raw", connectionString });
      JsonElement tried = await (await api.PostAsync($"/api/connections/{id}/test")).JsonAsync(HttpStatusCode.OK);
      tried.GetProperty("ok").GetBoolean().ShouldBeTrue(tried.GetRawText());
      tried.GetProperty("message").GetString()!.ShouldStartWith($"Connected to HyperWhirlpool model {HyperWhirlpoolWarehouse.Version} (ClickHouse ");
      tried.GetProperty("message").GetString()!.ShouldEndWith($", default domain {warehouse.Web}");

      const string wrongKey = "rk_wrong_00000000000000";
      tried = await (await api.PostAsync("/api/connections/test", new
      {
         kind = "hyperwhirlpool",
         connection = new { mode = "raw", connectionString = connectionString.Replace(FakeQueryService.Key, wrongKey, StringComparison.Ordinal) },
      })).JsonAsync(HttpStatusCode.OK);
      tried.GetProperty("ok").GetBoolean().ShouldBeFalse();
      tried.GetProperty("message").GetString()!.ShouldContain("Authentication failed");
      tried.GetProperty("message").GetString()!.ShouldNotContain(wrongKey);

      // A user name is the query service's to give.
      JsonElement refused = await (await api.PostAsync("/api/connections/test", new
      {
         kind = "hyperwhirlpool",
         connection = new { mode = "raw", connectionString = connectionString + ";Username=default" },
      })).JsonAsync(HttpStatusCode.BadRequest);
      refused.GetRawText().ShouldContain("take no keyword 'Username'");

      await TestSources.SettledAsync(api, id, "ready");
      JsonElement nodes = (await (await api.GetAsync("/api/catalog/tree/children")).JsonAsync(HttpStatusCode.OK)).GetProperty("nodes");
      JsonElement source = nodes.EnumerateArray().Single(n => n.GetProperty("id").GetString() == "hw");
      (source.GetProperty("isReadOnly").GetBoolean(), source.GetProperty("sourceKindName").GetString()).ShouldBe((true, "HyperWhirlpool"));
      nodes = (await (await api.GetAsync("/api/catalog/tree/children?parent=hw")).JsonAsync(HttpStatusCode.OK)).GetProperty("nodes");
      nodes.EnumerateArray().Select(n => $"{n.GetProperty("kind").GetString()} {n.GetProperty("id").GetString()}").ShouldBe(
         [$"schema hw.{warehouse.Crm}", "view hw.device", "view hw.link_device_contact", "view hw.page_view", "view hw.page_view_events", "view hw.session"]);

      JsonElement events = await (await api.GetAsync("/api/catalog/entity?name=hw.page_view_events")).JsonAsync(HttpStatusCode.OK);
      events.GetProperty("comment").GetString().ShouldBe("A page shown in a browser.");
      events.GetProperty("key").GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ShouldBe(["event_id"]);
      // References the warehouse doesn't enforce: a navigation may find no row.
      events.GetProperty("navigations").EnumerateArray().Select(n => $"{n.GetProperty("name").GetString()} {n.GetProperty("multiplicity").GetString()} {n.GetProperty("target").GetString()}")
         .Order(StringComparer.Ordinal)
         .ShouldBe([$"crm_contact zeroOrOne hw.{warehouse.Crm}.contact", "device zeroOrOne hw.device", "session zeroOrOne hw.session"]);
      events.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "url").GetProperty("comment").GetString().ShouldBe("The page's address.");
      events.GetProperty("capabilities").GetProperty("canUpdate").GetBoolean().ShouldBeFalse();
      events.GetProperty("timeColumn").GetString().ShouldBe("ts");

      // What the model says of the columns, for the dashboard editor.
      JsonElement sessions = await (await api.GetAsync("/api/catalog/entity?name=hw.session")).JsonAsync(HttpStatusCode.OK);
      JsonElement traffic = sessions.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "traffic_type");
      traffic.GetProperty("values").EnumerateArray().Select(v => v.GetString()).ShouldBe(["paid", "social", "search", "referral", "direct"]);
      sessions.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "page_views").GetProperty("aggregate").GetString().ShouldBe("sum");
      traffic.GetProperty("aggregate").ValueKind.ShouldBe(JsonValueKind.Null);
      api.Responses.Where(r => r.Contains(FakeQueryService.Key, StringComparison.Ordinal)).ShouldBeEmpty();
   }
}
