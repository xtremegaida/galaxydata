using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using GalaxyData.Testing;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Problems;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Connections;

/// <summary>Connections through the API: made, shown without secrets, changed, tried, converted and deleted.</summary>
public sealed class ConnectionApiTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   private const string PgSecret = "pg-secret-value-1";

   /// <summary>A PostgreSQL connection to nowhere (port 1 refuses at once), with a password.</summary>
   private const string Nowhere = "Host=127.0.0.1;Port=1;Database=shop;Username=reader;Password=" + PgSecret;

   private static string Files(WebAppFactory factory) => Path.Combine(factory.DataDirectory, "files");

   private static async Task<string> SqliteFileAsync(WebAppFactory factory, string name)
   {
      string path = Path.Combine(Files(factory), name);
      await using SqliteConnection connection = new($"Data Source={path};Pooling=False");
      await connection.OpenAsync(Token);
      await using SqliteCommand command = new("CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT)", connection);
      await command.ExecuteNonQueryAsync(Token);
      return path;
   }

   private static async Task<JsonElement> CreateAsync(TestApi admin, string alias, string kind, object connection, HttpStatusCode status = HttpStatusCode.Created) =>
      await (await admin.PostAsync("/api/connections", new { alias, kind, connection })).JsonAsync(status);

   [Fact]
   public async Task TheKindsDescribeTheirForms()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      JsonElement kinds = await (await admin.GetAsync("/api/connection-kinds")).JsonAsync(HttpStatusCode.OK);
      Golden.Match(JsonSerializer.Serialize(kinds, new JsonSerializerOptions { WriteIndented = true }), "json");
   }

   [Fact]
   public async Task ASqliteConnectionIsMadeShownAndTried()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      string path = await SqliteFileAsync(factory, "shop.db");
      JsonElement created = await CreateAsync(admin, "shop", "sqlite", new { settings = new Dictionary<string, string> { ["data source"] = path }, options = new { trustForeignKeys = "true" } });
      int id = created.GetProperty("id").GetInt32();
      created.GetProperty("alias").GetString().ShouldBe("shop");
      created.GetProperty("settings").GetRawText().ShouldBe(JsonSerializer.Serialize(new Dictionary<string, string> { ["Data Source"] = path }));
      created.GetProperty("connectionString").GetString().ShouldBe($"Data Source={path}");
      created.GetProperty("isReadOnly").GetBoolean().ShouldBeTrue("connections are read-only unless said otherwise");
      created.GetProperty("options").GetRawText().ShouldBe("""{"trustForeignKeys":"true"}""");
      created.GetProperty("schemaStatus").GetString().ShouldBe("loading", "its schema is read once it is made");
      await Catalog.TestSources.SettledAsync(admin, id, "ready");

      JsonElement tried = await (await admin.PostAsync($"/api/connections/{id}/test")).JsonAsync(HttpStatusCode.OK);
      tried.GetProperty("ok").GetBoolean().ShouldBeTrue(tried.GetRawText());
      tried.GetProperty("message").GetString()!.ShouldMatch(@"^Opened the SQLite 3\.[\d.]+ database: 1 table or view$");

      File.Delete(path);
      tried = await (await admin.PostAsync($"/api/connections/{id}/test")).JsonAsync(HttpStatusCode.OK);
      tried.GetProperty("ok").GetBoolean().ShouldBeFalse();
      tried.GetProperty("message").GetString().ShouldBe($"There is no file {path}");
      File.Exists(path).ShouldBeFalse("trying a connection never makes its file");
   }

   [Fact]
   public async Task ADuckDbConnectionIsTriedReadOnly()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      string path = Path.Combine(Files(factory), "warehouse.duckdb");
      await using (DuckDBConnection connection = new($"Data Source={path}"))
      {
         await connection.OpenAsync(Token);
         await using System.Data.Common.DbCommand command = connection.CreateCommand();
         command.CommandText = "CREATE TABLE orders (id INTEGER); CREATE VIEW big AS SELECT * FROM orders";
         await command.ExecuteNonQueryAsync(Token);
      }
      JsonElement created = await CreateAsync(admin, "wh", "duckdb", new { mode = "raw", connectionString = $"DataSource={path};Threads=2" });
      created.GetProperty("settings").GetRawText().ShouldBe(JsonSerializer.Serialize(new Dictionary<string, string> { ["Data Source"] = path, ["threads"] = "2" }));
      JsonElement tried = await (await admin.PostAsync($"/api/connections/{created.GetProperty("id").GetInt32()}/test")).JsonAsync(HttpStatusCode.OK);
      tried.GetProperty("message").GetString()!.ShouldMatch(@"^Opened the DuckDB v?[\d.]+.* database: 2 tables and views$");
   }

   [Fact]
   public async Task AFolderOfWorkbooksIsAlwaysReadOnly()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      string folder = Path.Combine(Files(factory), "books");
      Directory.CreateDirectory(folder);
      foreach (string name in (string[])["a.xlsx", "b.xlsx", "~$a.xlsx", "notes.txt"]) { await File.WriteAllTextAsync(Path.Combine(folder, name), "", Token); }
      JsonElement created = await CreateAsync(admin, "xl", "excel", new { settings = new { Folder = folder }, options = new { headerRow = "false" }, isReadOnly = false });
      created.GetProperty("isReadOnly").GetBoolean().ShouldBeTrue();
      created.GetProperty("connectionString").ValueKind.ShouldBe(JsonValueKind.Null);
      JsonElement tried = await (await admin.PostAsync($"/api/connections/{created.GetProperty("id").GetInt32()}/test")).JsonAsync(HttpStatusCode.OK);
      tried.GetProperty("message").GetString().ShouldBe("Found 2 workbooks in the folder");
      JsonElement problem = await (await admin.PostAsync("/api/connection-kinds/excel/convert", new { connection = new { settings = new { Folder = folder } }, to = "raw" }))
         .ProblemAsync(400, ProblemCodes.InvalidRequest);
      problem.GetProperty("errors").GetProperty("mode")[0].GetString().ShouldBe("Excel folder connections have no connection string");
   }

   /// <summary>A secret is kept protected: never in an answer, the settings or the audit, and in the database only protected.</summary>
   [Fact]
   public async Task SecretsAreKeptAndNeverShown()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      JsonElement created = await CreateAsync(admin, "pg", "postgres", new { mode = "raw", connectionString = Nowhere });
      int id = created.GetProperty("id").GetInt32();
      created.GetProperty("mode").GetString().ShouldBe("raw");
      created.GetProperty("settings").GetRawText().ShouldBe("""{"Host":"127.0.0.1","Port":"1","Database":"shop","Username":"reader"}""");
      created.GetProperty("secrets").GetRawText().ShouldBe("""{"Password":{"hasValue":true}}""");
      created.GetProperty("connectionString").GetString().ShouldBe("Host=127.0.0.1;Port=1;Database=shop;Username=reader;Password=********");

      JsonElement tried = await (await admin.PostAsync($"/api/connections/{id}/test")).JsonAsync(HttpStatusCode.OK);
      tried.GetProperty("ok").GetBoolean().ShouldBeFalse();

      // Kept through the form, and through the connection string masked; then cleared.
      JsonElement kept = await (await admin.PutAsync($"/api/connections/{id}", new
      {
         version = 0,
         connection = new { settings = new { Host = "127.0.0.1", Port = "2", Database = "shop", Username = "reader" }, secrets = new { Password = new { action = "keep" } } },
      })).JsonAsync(HttpStatusCode.OK);
      kept.GetProperty("secrets").GetProperty("Password").GetProperty("hasValue").GetBoolean().ShouldBeTrue();
      kept.GetProperty("mode").GetString().ShouldBe("form");
      kept = await (await admin.PutAsync($"/api/connections/{id}", new
      {
         version = 1,
         connection = new { mode = "raw", connectionString = "Host=127.0.0.1;Port=1;Database=shop;Username=reader;Password=********" },
      })).JsonAsync(HttpStatusCode.OK);
      kept.GetProperty("secrets").GetProperty("Password").GetProperty("hasValue").GetBoolean().ShouldBeTrue();
      JsonElement cleared = await (await admin.PutAsync($"/api/connections/{id}", new { version = 2, connection = new { mode = "raw", connectionString = "Host=127.0.0.1;Database=shop" } }))
         .JsonAsync(HttpStatusCode.OK);
      cleared.GetProperty("secrets").GetRawText().ShouldBe("""{"Password":{"hasValue":false}}""");

      using (IServiceScope scope = factory.Services.CreateScope())
      {
         MetadataDb db = scope.ServiceProvider.GetRequiredService<MetadataDb>();
         SourceConnection row = await db.Connections.SingleAsync(Token);
         row.SettingsJson.ShouldNotContain(PgSecret);
         (row.ProtectedSecrets ?? string.Empty).ShouldNotContain(PgSecret);
      }
      JsonElement[] events = (await (await admin.GetAsync("/api/audit/admin-events")).JsonAsync(HttpStatusCode.OK)).EnumerateArray().ToArray();
      events.Select(e => e.GetProperty("action").GetString()).Take(4).ShouldBe(["connection.updated", "connection.updated", "connection.updated", "connection.created"]);
      events[0].GetProperty("details").GetString().ShouldBe("""{"settings":{"Port":{"from":"1","to":null},"Username":{"from":"reader","to":null}},"secrets":{"Password":"cleared"}}""");
      events[3].GetProperty("details").GetString()!.ShouldContain("""
         "secrets":["Password"]
         """.Trim());
      admin.Responses.Where(r => r.Contains(PgSecret, StringComparison.Ordinal)).ShouldBeEmpty();
   }

   [Fact]
   public async Task SettingsAreTriedBeforeTheyAreSavedWithTheSecretsTheyKeep()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      int id = (await CreateAsync(admin, "pg", "postgres", new { mode = "raw", connectionString = Nowhere })).GetProperty("id").GetInt32();
      string masked = Nowhere.Replace(PgSecret, "********", StringComparison.Ordinal);
      JsonElement tried = await (await admin.PostAsync("/api/connections/test", new { kind = "postgres", connectionId = id, connection = new { mode = "raw", connectionString = masked } }))
         .JsonAsync(HttpStatusCode.OK);
      tried.GetProperty("ok").GetBoolean().ShouldBeFalse();
      tried.GetProperty("message").GetString()!.ShouldNotContain(PgSecret);
      JsonElement problem = await (await admin.PostAsync("/api/connections/test", new { kind = "postgres", connection = new { mode = "raw", connectionString = masked } }))
         .ProblemAsync(400, ProblemCodes.InvalidRequest);
      problem.GetProperty("errors").GetProperty("connectionString")[0].GetString().ShouldBe("There is no Password to keep: write it in place of ********");
      admin.Responses.Where(r => r.Contains(PgSecret, StringComparison.Ordinal)).ShouldBeEmpty();
   }

   [Fact]
   public async Task ATryThatTakesTooLongIsStopped()
   {
      await using WebAppFactory factory = new() { Settings = new Dictionary<string, string?> { ["GalaxyData:Connections:TestTimeout"] = "00:00:01" } };
      TestApi admin = await TestApi.SignedInAsync(factory);
      // An address nothing answers at.
      JsonElement tried = await (await admin.PostAsync("/api/connections/test",
         new { kind = "postgres", connection = new { mode = "raw", connectionString = "Host=10.255.255.1;Database=x;Timeout=60" } })).JsonAsync(HttpStatusCode.OK);
      tried.GetProperty("ok").GetBoolean().ShouldBeFalse();
      tried.GetProperty("elapsedMs").GetDouble().ShouldBeLessThan(5000);
   }

   [Theory]
   [InlineData("1x")]
   [InlineData("and")]
   [InlineData("null")]
   [InlineData("$x")]
   [InlineData("a-b")]
   [InlineData("with space")]
   [InlineData("café")]
   public async Task AnAliasIsAPlainName(string alias)
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      JsonElement problem = await (await admin.PostAsync("/api/connections", new { alias, kind = "postgres", connection = new { mode = "raw", connectionString = Nowhere } }))
         .ProblemAsync(400, ProblemCodes.InvalidRequest);
      problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).ShouldBe(["alias"]);
   }

   [Fact]
   public async Task AnAliasIsOneIgnoringCaseAndAKindIsOneOfTheKinds()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      await CreateAsync(admin, "Shop", "postgres", new { mode = "raw", connectionString = Nowhere });
      await (await admin.PostAsync("/api/connections", new { alias = "shop", kind = "postgres", connection = new { mode = "raw", connectionString = Nowhere } }))
         .ProblemAsync(409, ProblemCodes.AliasTaken);
      JsonElement problem = await (await admin.PostAsync("/api/connections", new { alias = "other", kind = "oracle", connection = new { } }))
         .ProblemAsync(400, ProblemCodes.InvalidRequest);
      problem.GetProperty("errors").GetProperty("kind")[0].GetString().ShouldBe("There is no kind 'oracle': use postgres, sqlserver, sqlite, duckdb, excel, clickhouse");
   }

   [Fact]
   public async Task ProblemsAreReportedByField()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      JsonElement problem = await (await admin.PostAsync("/api/connections", new
      {
         alias = "files",
         kind = "sqlite",
         connection = new { settings = new Dictionary<string, string> { ["Data Source"] = @"C:\Windows\System32\config\SAM" }, options = new { trustForeignKeys = "maybe" } },
      })).ProblemAsync(400, ProblemCodes.InvalidRequest);
      problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).ShouldBe(["settings.Data Source", "options.trustForeignKeys"], ignoreOrder: true);
   }

   [Fact]
   public async Task ChangesToAnotherVersionAreConflictsAndDeletingIsInTheAudit()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      int id = (await CreateAsync(admin, "pg", "postgres", new { mode = "raw", connectionString = Nowhere })).GetProperty("id").GetInt32();
      await (await admin.PutAsync($"/api/connections/{id}", new { version = 3, connection = new { mode = "raw", connectionString = Nowhere } }))
         .ProblemAsync(409, ProblemCodes.ConcurrencyConflict);
      // The same settings change nothing, and keep the version.
      (await (await admin.PutAsync($"/api/connections/{id}", new { version = 0, connection = new { mode = "raw", connectionString = Nowhere } })).JsonAsync(HttpStatusCode.OK))
         .GetProperty("version").GetInt32().ShouldBe(0);
      await (await admin.DeleteAsync($"/api/connections/{id}?version=1")).ProblemAsync(409, ProblemCodes.ConcurrencyConflict);
      (await admin.DeleteAsync($"/api/connections/{id}?version=0")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
      await (await admin.GetAsync($"/api/connections/{id}")).ProblemAsync(404, ProblemCodes.NotFound);
      JsonElement last = (await (await admin.GetAsync("/api/audit/admin-events")).JsonAsync(HttpStatusCode.OK))[0];
      (last.GetProperty("action").GetString(), last.GetProperty("target").GetString()).ShouldBe(("connection.deleted", "connection:pg"));
   }

   [Fact]
   public async Task TheFormAndTheConnectionStringConvert()
   {
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      JsonElement raw = await (await admin.PostAsync("/api/connection-kinds/sqlserver/convert", new
      {
         to = "raw",
         connection = new { settings = new { server = "db", Database = "shop", uid = "reader" }, secrets = new { Password = new { action = "keep" } } },
      })).JsonAsync(HttpStatusCode.OK);
      raw.GetProperty("mode").GetString().ShouldBe("raw");
      raw.GetProperty("connectionString").GetString().ShouldBe("Data Source=db;Initial Catalog=shop;User ID=reader;Password=********");
      JsonElement form = await (await admin.PostAsync("/api/connection-kinds/sqlserver/convert", new { to = "form", connection = raw })).JsonAsync(HttpStatusCode.OK);
      form.GetProperty("settings").GetRawText().ShouldBe("""{"Data Source":"db","Initial Catalog":"shop","User ID":"reader"}""");
      form.GetProperty("secrets").GetRawText().ShouldBe("""{"Password":{"action":"keep","value":null}}""");
      await (await admin.PostAsync("/api/connection-kinds/sqlserver/convert", new { to = "form", connection = new { mode = "raw", connectionString = "Bogus=1" } }))
         .ProblemAsync(400, ProblemCodes.InvalidRequest);
      await (await admin.PostAsync("/api/connection-kinds/oracle/convert", new { to = "form", connection = new { } })).ProblemAsync(404, ProblemCodes.NotFound);
   }

   /// <summary>Without the keys that protected them, a connection's secrets can't be read: it says so, and they must be entered again.</summary>
   [Fact]
   public async Task SecretsWhoseKeysAreGoneAreEnteredAgain()
   {
      await using SharedData data = new();
      await using (WebAppFactory first = new() { DataDirectory = data.Path })
      {
         TestApi admin = await TestApi.SignedInAsync(first);
         await CreateAsync(admin, "pg", "postgres", new { mode = "raw", connectionString = Nowhere });
      }
      await WebAppFactory.ReleasedAsync(data.Path);
      Directory.Delete(Path.Combine(data.Path, AuthSetup.KeysDirectoryName), recursive: true);
      await using WebAppFactory second = new() { DataDirectory = data.Path };
      TestApi again = await TestApi.SignedInAsync(second);
      JsonElement connection = await (await again.GetAsync("/api/connections/1")).JsonAsync(HttpStatusCode.OK);
      connection.GetProperty("secretsUnreadable").GetBoolean().ShouldBeTrue();
      connection.GetProperty("secrets").GetRawText().ShouldBe("""{"Password":{"hasValue":false}}""");
      JsonElement tried = await (await again.PostAsync("/api/connections/1/test")).JsonAsync(HttpStatusCode.OK);
      tried.GetProperty("message").GetString()!.ShouldStartWith("The stored secrets can't be read");

      var keep = new { settings = new { Host = "127.0.0.1", Database = "shop" }, secrets = new { Password = new { action = "keep" } } };
      await (await again.PutAsync("/api/connections/1", new { version = 0, connection = keep })).ProblemAsync(400, ProblemCodes.InvalidRequest);
      var masked = new { mode = "raw", connectionString = "Host=127.0.0.1;Database=shop;Password=********" };
      await (await again.PostAsync("/api/connections/test", new { kind = "postgres", connectionId = 1, connection = masked })).ProblemAsync(400, ProblemCodes.InvalidRequest);
      // A secret entered in the form, then converted into the string (masked there), is set, not kept.
      var entered = new
      {
         mode = "raw",
         connectionString = "Host=127.0.0.1;Database=shop;Password=********",
         secrets = new { Password = new { action = "set", value = "entered-again" } },
      };
      (await again.PostAsync("/api/connections/test", new { kind = "postgres", connectionId = 1, connection = entered })).StatusCode.ShouldBe(HttpStatusCode.OK);
      var set = new { settings = new { Host = "127.0.0.1", Database = "shop" }, secrets = new { Password = new { action = "set", value = "entered-again" } } };
      connection = await (await again.PutAsync("/api/connections/1", new { version = 0, connection = set })).JsonAsync(HttpStatusCode.OK);
      connection.GetProperty("secretsUnreadable").GetBoolean().ShouldBeFalse();
      connection.GetProperty("secrets").GetRawText().ShouldBe("""{"Password":{"hasValue":true}}""");
   }
}
