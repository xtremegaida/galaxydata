using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GalaxyData.Web.Tests.Catalog;

/// <summary>Sources for tests: databases made in the data directory's <c>files</c>, connections to them, and waiting for their schemas.</summary>
internal static class TestSources
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   public static string Files(WebAppFactory factory) => Path.Combine(factory.DataDirectory, "files");

   /// <summary>The shop fixture as a SQLite database in the data directory's files.</summary>
   public static async Task<string> ShopAsync(WebAppFactory factory, string name = "shop.db")
   {
      string path = Path.Combine(Files(factory), name);
      await SqliteAsync(path, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "shop.sqlite.sql"), Token));
      return path;
   }

   /// <summary>Runs SQL in a SQLite database, made if it isn't there.</summary>
   public static async Task SqliteAsync(string path, string sql)
   {
      await using SqliteConnection connection = new($"Data Source={path};Pooling=False");
      await connection.OpenAsync(Token);
      await using SqliteCommand command = new(sql, connection);
      await command.ExecuteNonQueryAsync(Token);
   }

   /// <summary>Runs SQL in a DuckDB database, made if it isn't there.</summary>
   public static async Task DuckDbAsync(string path, string sql)
   {
      await using DuckDBConnection connection = new($"Data Source={path}");
      await connection.OpenAsync(Token);
      await using System.Data.Common.DbCommand command = connection.CreateCommand();
      command.CommandText = sql;
      await command.ExecuteNonQueryAsync(Token);
   }

   /// <summary>Makes a connection; its id.</summary>
   public static async Task<int> AddAsync(TestApi admin, string alias, string kind, object connection) =>
      (await (await admin.PostAsync("/api/connections", new { alias, kind, connection })).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetInt32();

   /// <summary>A SQLite connection to a file; its id, once its schema is read.</summary>
   public static async Task<int> AddSqliteAsync(TestApi admin, string alias, string path, bool readOnly = true, object? options = null)
   {
      int id = await AddAsync(admin, alias, "sqlite", new { settings = new { DataSource = path }, isReadOnly = readOnly, options });
      await SettledAsync(admin, id, "ready");
      return id;
   }

   /// <summary>The connection once its schema isn't being read, which must have gone as <paramref name="expected"/> says.</summary>
   public static async Task<JsonElement> SettledAsync(TestApi admin, int id, string expected)
   {
      Stopwatch waited = Stopwatch.StartNew();
      while (true)
      {
         JsonElement connection = await (await admin.GetAsync($"/api/connections/{id}")).JsonAsync(HttpStatusCode.OK);
         string status = connection.GetProperty("schemaStatus").GetString()!;
         if (status != "loading")
         {
            Assert.True(status == expected, $"The schema of connection {id} is {status}, not {expected}: {connection.GetProperty("schemaError")}");
            return connection;
         }
         if (waited.Elapsed > TimeSpan.FromSeconds(60)) { Assert.Fail($"The schema of connection {id} is still being read"); }
         await Task.Delay(20, Token);
      }
   }

   /// <summary>Reads a connection's schema again, and waits for it.</summary>
   public static async Task<JsonElement> RefreshAsync(TestApi admin, int id, string expected = "ready")
   {
      JsonElement accepted = await (await admin.PostAsync($"/api/connections/{id}/refresh")).JsonAsync(HttpStatusCode.Accepted);
      Assert.Equal("loading", accepted.GetProperty("schemaStatus").GetString());
      return await SettledAsync(admin, id, expected);
   }
}
