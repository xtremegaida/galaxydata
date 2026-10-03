using System;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Testing;
using GalaxyData.Web.Tests.Catalog;
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

   private static async Task ExecuteAsync(string kind, string connectionString, string sql)
   {
      await using DbConnection connection = kind == "postgres"
         ? new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ToString())
         : new SqlConnection(new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ToString());
      await connection.OpenAsync(Token);
      await using DbCommand command = connection.CreateCommand();
      command.CommandText = sql;
      await command.ExecuteNonQueryAsync(Token);
   }

   /// <summary>A server's schema read into the catalog, from a database of the test's own (dropped after): its schemas, keys and foreign keys.</summary>
   [Theory]
   [InlineData("postgres")]
   [InlineData("sqlserver")]
   public async Task AServersSchemaIsRead(string kind)
   {
      bool postgres = kind == "postgres";
      string server = postgres ? TestServers.Postgres : TestServers.SqlServer;
      await RequireAsync(kind, server);
      string password = new DbConnectionStringBuilder { ConnectionString = server }["password"].ToString()!;
      string database = "gd_web_" + Guid.NewGuid().ToString("N")[..10];
      string system = postgres ? new NpgsqlConnectionStringBuilder(server) { Database = "postgres" }.ToString() : new SqlConnectionStringBuilder(server) { InitialCatalog = "master" }.ToString();
      string connectionString = postgres ? new NpgsqlConnectionStringBuilder(server) { Database = database }.ToString() : new SqlConnectionStringBuilder(server) { InitialCatalog = database }.ToString();
      await ExecuteAsync(kind, system, $"CREATE DATABASE {database}");
      try
      {
         await ExecuteAsync(kind, connectionString, postgres
            ? "CREATE SCHEMA sales; CREATE TABLE customers (id int PRIMARY KEY, name text NOT NULL); " +
              "CREATE TABLE sales.orders (id int GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, customer_id int NOT NULL REFERENCES customers(id), total numeric(10,2))"
            : "CREATE TABLE customers (id int PRIMARY KEY, name nvarchar(100) NOT NULL); EXEC('CREATE SCHEMA sales'); " +
              "CREATE TABLE sales.orders (id int IDENTITY PRIMARY KEY, customer_id int NOT NULL REFERENCES customers(id), total decimal(10,2))");
         await using WebAppFactory factory = new();
         TestApi admin = await TestApi.SignedInAsync(factory);
         int id = await TestSources.AddAsync(admin, "srv", kind, new { mode = "raw", connectionString, isReadOnly = false });
         await TestSources.SettledAsync(admin, id, "ready");
         JsonElement nodes = (await (await admin.GetAsync("/api/catalog/tree/children?parent=srv")).JsonAsync(HttpStatusCode.OK)).GetProperty("nodes");
         nodes.EnumerateArray().Select(n => $"{n.GetProperty("kind").GetString()} {n.GetProperty("id").GetString()}").ShouldBe(["schema srv.sales", "table srv.customers"]);
         JsonElement orders = await (await admin.GetAsync("/api/catalog/entity?name=srv.sales.orders")).JsonAsync(HttpStatusCode.OK);
         orders.GetProperty("navigations").EnumerateArray().Select(n => $"{n.GetProperty("name").GetString()} {n.GetProperty("multiplicity").GetString()}")
            .ShouldBe(["customer one"], "a foreign key the server enforces, of a column that can't be null");
         JsonElement key = orders.GetProperty("columns").EnumerateArray().First();
         key.GetProperty("isIdentity").GetBoolean().ShouldBeTrue();
         // SQL Server gives identity values itself; PostgreSQL takes one given.
         key.GetProperty("insert").GetString().ShouldBe(postgres ? "optional" : "never");
         orders.GetProperty("capabilities").GetProperty("canUpdate").GetBoolean().ShouldBeTrue();
         admin.Responses.Where(r => r.Contains(password, StringComparison.Ordinal)).ShouldBeEmpty();
      }
      finally
      {
         NpgsqlConnection.ClearAllPools();
         SqlConnection.ClearAllPools();
         await ExecuteAsync(kind, system, postgres
            ? $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"
            : $"ALTER DATABASE {database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {database}");
      }
   }
}
