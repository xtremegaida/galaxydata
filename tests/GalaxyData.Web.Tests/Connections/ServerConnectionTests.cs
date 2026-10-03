using System;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Testing;
using Microsoft.Data.SqlClient;
using Npgsql;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests.Connections;

/// <summary>
/// Connections to the servers the container tests use (<c>servers.sh up</c>, or <c>GDQ_TEST_POSTGRES</c> and
/// <c>GDQ_TEST_SQLSERVER</c>): tried with the right password and a wrong one, which isn't repeated. Skipped when
/// the server can't be reached, unless it was named.
/// </summary>
public sealed class ServerConnectionTests
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   private static async Task RequireAsync(string kind, string connectionString)
   {
      try
      {
         await using DbConnection connection = kind == "postgres"
            ? new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false, Timeout = 3 }.ToString())
            : new SqlConnection(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false, ConnectTimeout = 3 }.ToString());
         await connection.OpenAsync(Token);
      }
      catch (Exception e) when (e is DbException or TimeoutException or InvalidOperationException)
      {
         if (TestServers.Named(kind == "postgres") != null) { Assert.Fail($"{kind} can't be reached: {e.Message}"); }
         Assert.Skip($"{kind} isn't running ({e.Message}); start the servers with tests/GalaxyData.Query.ContainerTests/servers.sh up");
      }
   }

   [Theory]
   [InlineData("postgres", "Database", "postgres", "Connected to PostgreSQL ")]
   [InlineData("sqlserver", "Initial Catalog", "master", "Connected to SQL Server ")]
   public async Task AServersConnectionIsTried(string kind, string databaseKeyword, string database, string connected)
   {
      string server = kind == "postgres" ? TestServers.Postgres : TestServers.SqlServer;
      await RequireAsync(kind, server);
      string password = new DbConnectionStringBuilder { ConnectionString = server }["password"].ToString()!;
      await using WebAppFactory factory = new();
      TestApi admin = await TestApi.SignedInAsync(factory);
      string connectionString = $"{server};{databaseKeyword}={database}";
      JsonElement created = await (await admin.PostAsync("/api/connections", new { alias = "srv", kind, connection = new { mode = "raw", connectionString } }))
         .JsonAsync(HttpStatusCode.Created);
      int id = created.GetProperty("id").GetInt32();
      JsonElement tried = await (await admin.PostAsync($"/api/connections/{id}/test")).JsonAsync(HttpStatusCode.OK);
      tried.GetProperty("ok").GetBoolean().ShouldBeTrue(tried.GetRawText());
      tried.GetProperty("message").GetString()!.ShouldStartWith(connected);
      tried.GetProperty("message").GetString()!.ShouldEndWith($", database {database}");

      // Read-write, too.
      tried = await (await admin.PostAsync("/api/connections/test", new { kind, connection = new { mode = "raw", connectionString, isReadOnly = false } }))
         .JsonAsync(HttpStatusCode.OK);
      tried.GetProperty("ok").GetBoolean().ShouldBeTrue(tried.GetRawText());

      string wrong = connectionString.Replace(password, "a-wrong-password-9", StringComparison.Ordinal);
      tried = await (await admin.PostAsync("/api/connections/test", new { kind, connection = new { mode = "raw", connectionString = wrong } })).JsonAsync(HttpStatusCode.OK);
      tried.GetProperty("ok").GetBoolean().ShouldBeFalse();
      tried.GetProperty("message").GetString()!.ShouldNotContain("a-wrong-password-9");
      admin.Responses.Where(r => r.Contains(password, StringComparison.Ordinal)).ShouldBeEmpty();
   }
}
