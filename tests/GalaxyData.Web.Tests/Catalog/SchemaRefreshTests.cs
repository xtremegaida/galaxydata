using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Problems;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Catalog;

/// <summary>Connections' schemas: read when made and when asked, kept as snapshots with what changed, and how a read that fails is told.</summary>
public sealed class SchemaRefreshTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   private static async Task<T> WithDbAsync<T>(WebAppFactory factory, Func<MetadataDb, Task<T>> use)
   {
      await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
      return await use(scope.ServiceProvider.GetRequiredService<MetadataDb>());
   }

   private static async Task<JsonElement> SnapshotsAsync(TestApi admin, int id) =>
      await (await admin.GetAsync($"/api/connections/{id}/snapshots")).JsonAsync(HttpStatusCode.OK);

   private static async Task<List<string>> ChangesAsync(TestApi admin, int id, long snapshot)
   {
      JsonElement detail = await (await admin.GetAsync($"/api/connections/{id}/snapshots/{snapshot}")).JsonAsync(HttpStatusCode.OK);
      return detail.GetProperty("changes").EnumerateArray()
         .Select(c => $"{c.GetProperty("change").GetString()} {c.GetProperty("object").GetString()} {c.GetProperty("table").GetString()}" +
                      (c.GetProperty("name").ValueKind == JsonValueKind.String ? "." + c.GetProperty("name").GetString() : string.Empty))
         .ToList();
   }

   [Fact]
   public async Task AConnectionsSchemaIsReadWhenItIsMade()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      string path = await TestSources.ShopAsync(factory);
      int id = await TestSources.AddAsync(admin, "shop", "sqlite", new { settings = new { DataSource = path } });
      JsonElement connection = await TestSources.SettledAsync(admin, id, "ready");
      connection.GetProperty("schemaError").ValueKind.ShouldBe(JsonValueKind.Null);
      connection.GetProperty("schemaRefreshedAt").GetDateTime().ShouldBeGreaterThan(connection.GetProperty("createdAt").GetDateTime().AddSeconds(-1));
      connection.GetProperty("version").GetInt32().ShouldBe(0, "reading the schema isn't a change an edit can conflict with");

      JsonElement snapshot = (await SnapshotsAsync(admin, id)).EnumerateArray().Single();
      snapshot.GetProperty("tableCount").GetInt32().ShouldBe(7);
      snapshot.GetProperty("hash").GetString()!.Length.ShouldBe(64);
      snapshot.GetProperty("changes").ValueKind.ShouldBe(JsonValueKind.Null, "a connection's first snapshot changes nothing");
      JsonElement detail = await (await admin.GetAsync($"/api/connections/{id}/snapshots/{snapshot.GetProperty("id").GetInt64()}")).JsonAsync(HttpStatusCode.OK);
      detail.GetProperty("schema").GetProperty("providerKind").GetString().ShouldBe("sqlite");
      detail.GetProperty("schema").GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("name").GetString())
         .ShouldBe(["addresses", "audit_log", "customers", "employees", "open_orders", "order_lines", "orders"]);
      detail.GetProperty("changes").ValueKind.ShouldBe(JsonValueKind.Null);

      (await admin.GetAsync($"/api/connections/{id}/snapshots/999")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
      (await admin.GetAsync("/api/connections/999/snapshots")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
      await (await admin.PostAsync("/api/connections/999/refresh")).ProblemAsync(404, ProblemCodes.NotFound);
   }

   /// <summary>A structure that changed is a new snapshot, with what changed; one as it was isn't; the newest five are kept.</summary>
   [Fact]
   public async Task WhatChangedIsKept()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      string path = await TestSources.ShopAsync(factory);
      int id = await TestSources.AddSqliteAsync(admin, "shop", path);
      long first = (await SnapshotsAsync(admin, id))[0].GetProperty("id").GetInt64();

      await TestSources.SqliteAsync(path, "ALTER TABLE customers ADD COLUMN email TEXT; DROP VIEW open_orders; CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT);");
      await TestSources.RefreshAsync(admin, id);
      JsonElement snapshots = await SnapshotsAsync(admin, id);
      snapshots.GetArrayLength().ShouldBe(2);
      long second = snapshots[0].GetProperty("id").GetInt64();
      second.ShouldBeGreaterThan(first, "the newest comes first");
      snapshots[0].GetProperty("changes").GetRawText().ShouldBe("""{"added":2,"removed":1,"changed":0}""");
      (await ChangesAsync(admin, id, second)).ShouldBe(["added column customers.email", "added table notes", "removed table open_orders"]);

      // Read again as it was: no new snapshot, the newest found again.
      DateTime checkedAt = snapshots[0].GetProperty("checkedAt").GetDateTime();
      await TestSources.RefreshAsync(admin, id);
      snapshots = await SnapshotsAsync(admin, id);
      snapshots.GetArrayLength().ShouldBe(2);
      snapshots[0].GetProperty("id").GetInt64().ShouldBe(second);
      snapshots[0].GetProperty("checkedAt").GetDateTime().ShouldBeGreaterThanOrEqualTo(checkedAt);

      for (int i = 0; i < 4; i++)
      {
         await TestSources.SqliteAsync(path, $"CREATE TABLE t{i} (id INTEGER)");
         await TestSources.RefreshAsync(admin, id);
      }
      snapshots = await SnapshotsAsync(admin, id);
      snapshots.GetArrayLength().ShouldBe(5);
      snapshots.EnumerateArray().Select(s => s.GetProperty("id").GetInt64()).ShouldNotContain(first, "the newest five are kept");
      snapshots[0].GetProperty("tableCount").GetInt32().ShouldBe(11);
      (await ChangesAsync(admin, id, snapshots[0].GetProperty("id").GetInt64())).ShouldBe(["added table t3"]);
   }

   /// <summary>Row counts change all the time: a read that finds only them changed brings the newest snapshot up to date.</summary>
   [Fact]
   public async Task RowCountsAreBroughtUpToDateWithoutANewSnapshot()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      string path = Path.Combine(TestSources.Files(factory), "wh.duckdb");
      await TestSources.DuckDbAsync(path, "CREATE TABLE facts (id INTEGER PRIMARY KEY, amount DOUBLE)");
      int id = await TestSources.AddAsync(admin, "wh", "duckdb", new { settings = new { DataSource = path } });
      await TestSources.SettledAsync(admin, id, "ready");
      await TestSources.DuckDbAsync(path, "INSERT INTO facts SELECT range, range * 2 FROM range(5000)");
      await TestSources.RefreshAsync(admin, id);
      JsonElement snapshots = await SnapshotsAsync(admin, id);
      snapshots.GetArrayLength().ShouldBe(1);
      JsonElement detail = await (await admin.GetAsync($"/api/connections/{id}/snapshots/{snapshots[0].GetProperty("id").GetInt64()}")).JsonAsync(HttpStatusCode.OK);
      detail.GetProperty("schema").GetProperty("tables")[0].GetProperty("rowCountEstimate").GetInt64().ShouldBeGreaterThan(0);
      JsonElement node = (await (await admin.GetAsync("/api/catalog/tree/children?parent=wh")).JsonAsync(HttpStatusCode.OK)).GetProperty("nodes").EnumerateArray().Single();
      node.GetProperty("rows").GetInt64().ShouldBeGreaterThan(0, "the catalog has the newest counts");
   }

   /// <summary>A read that fails says why, and the schema read before is still the catalog's.</summary>
   [Fact]
   public async Task AReadThatFailsKeepsTheSchemaReadBefore()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      string path = await TestSources.ShopAsync(factory);
      int id = await TestSources.AddSqliteAsync(admin, "shop", path);
      File.Delete(path);
      JsonElement connection = await TestSources.RefreshAsync(admin, id, "failed");
      connection.GetProperty("schemaError").GetString().ShouldBe($"There is no file {path}");
      File.Exists(path).ShouldBeFalse("reading a schema never makes the file");
      JsonElement tree = await (await admin.GetAsync("/api/catalog/tree/children")).JsonAsync(HttpStatusCode.OK);
      JsonElement source = tree.GetProperty("nodes").EnumerateArray().Single();
      source.GetProperty("status").GetString().ShouldBe("failed");
      source.GetProperty("hasChildren").GetBoolean().ShouldBeTrue();
      (await SnapshotsAsync(admin, id)).GetArrayLength().ShouldBe(1);

      // A database that isn't one: the provider's words.
      await File.WriteAllTextAsync(path, "not a database", Token);
      connection = await TestSources.RefreshAsync(admin, id, "failed");
      connection.GetProperty("schemaError").GetString()!.ShouldContain("not a database");
   }

   /// <summary>A snapshot that can't be read is told of, and leaves its source out of the catalog until the schema is read again.</summary>
   [Fact]
   public async Task ADamagedSnapshotIsToldOf()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      int id = await TestSources.AddSqliteAsync(admin, "shop", await TestSources.ShopAsync(factory));
      await WithDbAsync(factory, db => db.SchemaSnapshots.ExecuteUpdateAsync(s => s.SetProperty(x => x.Data, new byte[] { 1, 2, 3 }), Token));
      factory.Services.GetRequiredService<GalaxyData.Web.Catalog.CatalogService>().Invalidate();
      JsonElement source = (await (await admin.GetAsync("/api/catalog")).JsonAsync(HttpStatusCode.OK)).GetProperty("sources")[0];
      source.GetProperty("hasSchema").GetBoolean().ShouldBeFalse();
      source.GetProperty("problem").GetString().ShouldBe("Its schema snapshot can't be read; read the schema again");
      (await (await admin.GetAsync("/api/catalog/tree/children?parent=shop")).JsonAsync(HttpStatusCode.OK)).GetProperty("nodes").GetArrayLength().ShouldBe(0);

      await TestSources.RefreshAsync(admin, id);
      source = (await (await admin.GetAsync("/api/catalog")).JsonAsync(HttpStatusCode.OK)).GetProperty("sources")[0];
      source.GetProperty("hasSchema").GetBoolean().ShouldBeTrue("the read rewrites the snapshot of the same structure");
      source.GetProperty("problem").ValueKind.ShouldBe(JsonValueKind.Null);
   }

   [Fact]
   public async Task AReadThatTakesTooLongFails()
   {
      await using WebAppFactory factory = new() { Settings = new Dictionary<string, string?> { ["GalaxyData:Connections:RefreshTimeout"] = "00:00:01" } };
      TestApi admin = await TestApi.SignedInAsync(factory);
      // An address nothing answers at.
      int id = await TestSources.AddAsync(admin, "far", "postgres", new { mode = "raw", connectionString = "Host=10.255.255.1;Database=x;Timeout=60" });
      JsonElement connection = await TestSources.SettledAsync(admin, id, "failed");
      connection.GetProperty("schemaError").GetString().ShouldBe("Reading the schema took longer than 00:00:01");
   }

   /// <summary>Secrets that can't be read fail the read, saying so; the secret is never in what is said.</summary>
   [Fact]
   public async Task SecretsThatCantBeReadFailTheRead()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      int id = await TestSources.AddAsync(admin, "pg", "postgres", new { mode = "raw", connectionString = "Host=127.0.0.1;Port=1;Database=shop;Username=reader;Password=a-secret-9" });
      JsonElement connection = await TestSources.SettledAsync(admin, id, "failed");
      connection.GetProperty("schemaError").GetString()!.ShouldNotContain("a-secret-9");
      await WithDbAsync(factory, db => db.Connections.ExecuteUpdateAsync(s => s.SetProperty(c => c.ProtectedSecrets, "damaged"), Token));
      connection = await TestSources.RefreshAsync(admin, id, "failed");
      connection.GetProperty("schemaError").GetString().ShouldStartWith("The stored secrets can't be read");
      admin.Responses.Where(r => r.Contains("a-secret-9", StringComparison.Ordinal)).ShouldBeEmpty();
   }

   /// <summary>What was being read when the application stopped, and what was never read, is read at the next start.</summary>
   [Fact]
   public async Task SchemasLeftUnreadAreReadAtTheNextStart()
   {
      await using SharedData data = new();
      string path = Path.Combine(data.Path, "files", "shop.db");
      await using (WebAppFactory first = new() { DataDirectory = data.Path })
      {
         TestApi admin = await TestApi.SignedInAsync(first);
         await TestSources.ShopAsync(first);
         await TestSources.AddSqliteAsync(admin, "shop", path);
         await TestSources.AddSqliteAsync(admin, "other", path);
         await WithDbAsync(first, async db =>
         {
            await db.Connections.Where(c => c.Alias == "shop").ExecuteUpdateAsync(s => s.SetProperty(c => c.SchemaStatus, SchemaStatus.Loading), Token);
            return await db.Connections.Where(c => c.Alias == "other").ExecuteUpdateAsync(s => s.SetProperty(c => c.SchemaStatus, SchemaStatus.NotLoaded), Token);
         });
      }
      await WebAppFactory.ReleasedAsync(data.Path);
      await using WebAppFactory second = new() { DataDirectory = data.Path };
      TestApi again = await TestApi.SignedInAsync(second);
      await TestSources.SettledAsync(again, 1, "ready");
      await TestSources.SettledAsync(again, 2, "ready");
   }

   /// <summary>Connections deleted take their snapshots with them; settings changed read the schema again.</summary>
   [Fact]
   public async Task ChangesToAConnectionReadItsSchemaAgain()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      string shop = await TestSources.ShopAsync(factory);
      string other = Path.Combine(TestSources.Files(factory), "other.db");
      await TestSources.SqliteAsync(other, "CREATE TABLE things (id INTEGER PRIMARY KEY)");
      int id = await TestSources.AddSqliteAsync(admin, "shop", shop);

      // A name changed alone isn't read again.
      JsonElement renamed = await (await admin.PutAsync($"/api/connections/{id}", new { version = 0, displayName = "The shop", connection = new { settings = new { DataSource = shop } } }))
         .JsonAsync(HttpStatusCode.OK);
      renamed.GetProperty("schemaStatus").GetString().ShouldBe("ready");
      JsonElement moved = await (await admin.PutAsync($"/api/connections/{id}", new { version = 1, displayName = "The shop", connection = new { settings = new { DataSource = other } } }))
         .JsonAsync(HttpStatusCode.OK);
      moved.GetProperty("schemaStatus").GetString().ShouldBe("loading");
      await TestSources.SettledAsync(admin, id, "ready");
      (await SnapshotsAsync(admin, id))[0].GetProperty("tableCount").GetInt32().ShouldBe(1);

      (await admin.DeleteAsync($"/api/connections/{id}?version=2")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
      (await WithDbAsync(factory, db => db.SchemaSnapshots.CountAsync(Token))).ShouldBe(0);
      JsonElement tree = await (await admin.GetAsync("/api/catalog/tree/children")).JsonAsync(HttpStatusCode.OK);
      tree.GetProperty("nodes").GetArrayLength().ShouldBe(0);
   }

   [Fact]
   public async Task OnlyAdministratorsReadSchemasAndTheirSnapshots()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      int id = await TestSources.AddSqliteAsync(admin, "shop", await TestSources.ShopAsync(factory));
      await admin.CreateUserAsync("dee", nameof(UserRole.DataManager), "first-password-of-a-user");
      TestApi dee = await TestApi.SignedInAsync(factory, "dee", "first-password-of-a-user", changeTo: "second-password-of-a-user");
      await (await dee.PostAsync($"/api/connections/{id}/refresh")).ProblemAsync(403, ProblemCodes.Forbidden);
      await (await dee.GetAsync($"/api/connections/{id}/snapshots")).ProblemAsync(403, ProblemCodes.Forbidden);
   }
}
