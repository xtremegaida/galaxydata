using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Driver.ADO;
using GalaxyData.Query.ClickHouse;
using GalaxyData.Query.ContainerTests;
using GalaxyData.Query.Execution;
using GalaxyData.Query.IntegrationTests;
using GalaxyData.Query.IntegrationTests.Execution;
using GalaxyData.Query.PostgreSql;
using GalaxyData.Query.Providers;
using GalaxyData.Query.SqlServer;
using GalaxyData.Testing;
using Microsoft.Data.SqlClient;
using Npgsql;
using Xunit;

[assembly: AssemblyFixture(typeof(Servers))]

namespace GalaxyData.Query.ContainerTests;

public enum ServerKind
{
   Postgres,
   SqlServer,
   ClickHouse,
}

/// <summary>
/// The database servers the tests run against: the PostgreSQL, SQL Server and ClickHouse that <c>servers.sh up</c>
/// starts, or those <c>GDQ_TEST_POSTGRES</c>, <c>GDQ_TEST_SQLSERVER</c> and <c>GDQ_TEST_CLICKHOUSE</c> name (connection
/// strings of logins that may create databases). Each database is made from a fixture script, once, under a name of
/// its own, and dropped when the tests are done. The tests of a server that can't be reached are skipped, unless it
/// was named, when they fail.
/// </summary>
public sealed partial class Servers : IAsyncDisposable
{
   public const string DefaultPostgres = TestServers.DefaultPostgres;
   public const string DefaultSqlServer = TestServers.DefaultSqlServer;
   public const string DefaultClickHouse = TestServers.DefaultClickHouse;

   private readonly ConcurrentDictionary<string, Lazy<Task<ServerDatabase>>> databases = new(StringComparer.Ordinal);
   private readonly ConcurrentDictionary<ServerKind, Lazy<Task<string?>>> unavailable = new();
   private static readonly ConcurrentDictionary<string, NpgsqlDataSource> DataSources = new(StringComparer.Ordinal);
   private static readonly ConcurrentDictionary<string, ClickHouseDataSource> ClickHouseSources = new(StringComparer.Ordinal);

   /// <summary>A PostgreSQL connection, from the data source the provider recommends (one for each database).</summary>
   internal static NpgsqlConnection OpenPostgres(string connectionString) =>
      DataSources.GetOrAdd(connectionString, PostgreSqlSourceProvider.CreateDataSource).CreateConnection();

   /// <summary>A ClickHouse connection, from the data source the provider makes (one for each database), whose connections share an HTTP client.</summary>
   internal static ClickHouseConnection OpenClickHouse(string connectionString) =>
      ClickHouseSources.GetOrAdd(connectionString, ClickHouseSourceProvider.CreateDataSource).CreateConnection();

   private static string? Named(ServerKind server) => TestServers.Named(server switch
   {
      ServerKind.Postgres => "GDQ_TEST_POSTGRES",
      ServerKind.SqlServer => "GDQ_TEST_SQLSERVER",
      _ => "GDQ_TEST_CLICKHOUSE",
   });

   /// <summary>The connection string of the server's login.</summary>
   public static string Admin(ServerKind server) => Named(server) ?? server switch
   {
      ServerKind.Postgres => DefaultPostgres,
      ServerKind.SqlServer => DefaultSqlServer,
      _ => DefaultClickHouse,
   };

   /// <summary>Skips the test when the server can't be reached (fails it, when the server was named).</summary>
   public async Task RequireAsync(ServerKind server)
   {
      string? reason = await unavailable.GetOrAdd(server, s => new Lazy<Task<string?>>(() => ProbeAsync(s))).Value;
      if (reason == null) { return; }
      if (Named(server) != null) { Assert.Fail($"{server} ({Admin(server)}) can't be reached: {reason}"); }
      Assert.Skip($"{server} isn't running ({reason}); start the servers with tests/GalaxyData.Query.ContainerTests/servers.sh up");
   }

   private static async Task<string?> ProbeAsync(ServerKind server)
   {
      try
      {
         await using DbConnection connection = OpenAdmin(server);
         await connection.OpenAsync();
         // Opening a ClickHouse connection sends nothing.
         if (server == ServerKind.ClickHouse) { await connection.ExecuteAsync("SELECT 1"); }
         return null;
      }
      catch (Exception e) when (e is DbException or TimeoutException or InvalidOperationException)
      {
         return e.Message;
      }
   }

   internal static DbConnection OpenAdmin(ServerKind server) => server switch
   {
      ServerKind.Postgres => new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Admin(server)) { Database = "postgres", Pooling = false }.ToString()),
      ServerKind.SqlServer => new SqlConnection(new SqlConnectionStringBuilder(Admin(server)) { InitialCatalog = "master", Pooling = false }.ToString()),
      _ => OpenClickHouse(Admin(server)),
   };

   /// <summary>The shop fixture in one database.</summary>
   public Task<ServerDatabase> ShopAsync(ServerKind server) => DatabaseAsync(server, "shop");

   /// <summary>The shop's orders and their lines, without the customers, addresses and employees (in <see cref="CrmAsync"/>).</summary>
   public Task<ServerDatabase> SalesAsync(ServerKind server) => DatabaseAsync(server, "shop", server switch
   {
      ServerKind.Postgres => "DROP TABLE addresses, customers, employees CASCADE;",
      ServerKind.SqlServer => "ALTER TABLE orders DROP CONSTRAINT fk_orders_customer, fk_orders_ship, fk_orders_bill;\nDROP TABLE addresses;\nDROP TABLE customers;\nDROP TABLE employees;",
      _ => "DROP TABLE addresses;\nDROP TABLE customers;\nDROP TABLE employees;",
   });

   /// <summary>The shop's customers, addresses and employees.</summary>
   public Task<ServerDatabase> CrmAsync(ServerKind server) =>
      DatabaseAsync(server, "shop", "DROP VIEW open_orders;\nDROP TABLE order_lines;\nDROP TABLE orders;\nDROP TABLE audit_log;");

   /// <summary>A table with a column of each type the server has, and a row of values and one of nulls.</summary>
   public Task<ServerDatabase> KindsAsync(ServerKind server) => DatabaseAsync(server, "kinds");

   /// <summary>
   /// A database made from <c>{fixture}.{postgres|sqlserver|clickhouse}.sql</c> and <paramref name="then"/>. Its text
   /// compares case-sensitively (collation C, Latin1_General_100_CS_AS; ClickHouse's always does), as SQLite's does,
   /// unless <paramref name="serverCollation"/>.
   /// </summary>
   public async Task<ServerDatabase> DatabaseAsync(ServerKind server, string fixture, string? then = null, bool serverCollation = false)
   {
      await RequireAsync(server);
      string key = $"{server}|{fixture}|{then}|{serverCollation}";
      Lazy<Task<ServerDatabase>> made = databases.GetOrAdd(key, _ => new Lazy<Task<ServerDatabase>>(() => CreateAsync(server, fixture, then, serverCollation)));
      return await made.Value;
   }

   /// <summary>A database of the test's own, which it may change: made from the fixture and <paramref name="then"/>, and dropped with the others.</summary>
   public async Task<ServerDatabase> FreshAsync(ServerKind server, string fixture = "shop", string? then = null)
   {
      await RequireAsync(server);
      Lazy<Task<ServerDatabase>> made = databases.GetOrAdd($"{server}|{fixture}|{then}|fresh {Guid.NewGuid():N}",
         _ => new Lazy<Task<ServerDatabase>>(() => CreateAsync(server, fixture, then, serverCollation: false)));
      return await made.Value;
   }

   private static async Task<ServerDatabase> CreateAsync(ServerKind server, string fixture, string? then, bool serverCollation)
   {
      string name = "gdq_test_" + Guid.NewGuid().ToString("N")[..12];
      await using (DbConnection admin = OpenAdmin(server))
      {
         await admin.OpenAsync();
         await admin.ExecuteAsync(server switch
         {
            ServerKind.Postgres => $"CREATE DATABASE {name} TEMPLATE template0 ENCODING 'UTF8'{(serverCollation ? string.Empty : " LC_COLLATE 'C' LC_CTYPE 'C'")}",
            ServerKind.SqlServer => $"CREATE DATABASE {name}{(serverCollation ? string.Empty : " COLLATE Latin1_General_100_CS_AS")}",
            _ => $"CREATE DATABASE {name}",
         });
      }
      ServerDatabase database = new(server, name);
      string script = Fixtures.Sql($"{fixture}.{ServerDatabase.Suffix(server)}.sql") + "\n" + (then ?? string.Empty);
      try
      {
         // Not a connection of the data source: it would know the types of the database as it was before the script.
         await using DbConnection connection = server == ServerKind.Postgres
            ? new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ToString())
            : database.Open();
         await connection.OpenAsync();
         // SQL Server's scripts are batches, which GO lines end; ClickHouse runs one statement a request.
         string[] batches = server switch
         {
            ServerKind.SqlServer => Batches().Split(script),
            ServerKind.ClickHouse => script.Split(';'),
            _ => [script],
         };
         foreach (string batch in batches)
         {
            if (Statement(batch).Length > 0) { await connection.ExecuteAsync(batch); }
         }
         return database;
      }
      catch
      {
         await database.DropAsync();
         throw;
      }
   }

   [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
   private static partial Regex Batches();

   [GeneratedRegex(@"^\s*--.*$", RegexOptions.Multiline)]
   private static partial Regex Comments();

   /// <summary>A batch without its comments and spaces: empty when it is nothing else.</summary>
   private static string Statement(string batch) => Comments().Replace(batch, string.Empty).Trim();

   public async ValueTask DisposeAsync()
   {
      foreach (Lazy<Task<ServerDatabase>> made in databases.Values.Where(m => m.IsValueCreated))
      {
         try
         {
            await (await made.Value).DropAsync();
         }
         catch (Exception e) when (e is DbException or InvalidOperationException)
         {
            // A database that wasn't made, or can't be dropped now, is left for the server's next start.
         }
      }
      foreach (NpgsqlDataSource source in DataSources.Values) { await source.DisposeAsync(); }
      DataSources.Clear();
      foreach (ClickHouseDataSource source in ClickHouseSources.Values) { await source.DisposeAsync(); }
      ClickHouseSources.Clear();
   }
}

/// <summary>A database of one of the test servers.</summary>
public sealed class ServerDatabase(ServerKind server, string name)
{
   public ServerKind Server { get; } = server;

   public string Name { get; } = name;

   public string ConnectionString { get; } = server switch
   {
      ServerKind.Postgres => new NpgsqlConnectionStringBuilder(Servers.Admin(server)) { Database = name }.ToString(),
      ServerKind.SqlServer => new SqlConnectionStringBuilder(Servers.Admin(server)) { InitialCatalog = name }.ToString(),
      _ => new ClickHouseConnectionStringBuilder(Servers.Admin(server)) { Database = name }.ToString(),
   };

   public SourceProvider Provider => Server switch
   {
      ServerKind.Postgres => PostgreSqlSourceProvider.Instance,
      ServerKind.SqlServer => SqlServerSourceProvider.Instance,
      _ => ClickHouseSourceProvider.Instance,
   };

   /// <summary>The server's part of its fixtures' names: <c>shop.clickhouse.sql</c>.</summary>
   public static string Suffix(ServerKind server) => server switch
   {
      ServerKind.Postgres => "postgres",
      ServerKind.SqlServer => "sqlserver",
      _ => "clickhouse",
   };

   public DbConnection Open() => Server switch
   {
      ServerKind.Postgres => Servers.OpenPostgres(ConnectionString),
      ServerKind.SqlServer => new SqlConnection(ConnectionString),
      _ => Servers.OpenClickHouse(ConnectionString),
   };

   /// <summary>Adds the database to <paramref name="sources"/> as <paramref name="alias"/>.</summary>
   internal Task<TestSources> AddToAsync(TestSources sources, string alias)
   {
      ArgumentNullException.ThrowIfNull(sources);
      return sources.AddAsync(alias, Open, Provider);
   }

   /// <summary>A new set of sources with this database as <paramref name="alias"/>.</summary>
   internal Task<TestSources> SourcesAsync(string alias = "shop") => AddToAsync(new TestSources(), alias);

   internal async Task DropAsync()
   {
      await using DbConnection admin = Servers.OpenAdmin(Server);
      await admin.OpenAsync();
      switch (Server)
      {
         case ServerKind.Postgres:
            NpgsqlConnection.ClearAllPools();
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {Name} WITH (FORCE)");
            break;
         case ServerKind.SqlServer:
            SqlConnection.ClearAllPools();
            await admin.ExecuteAsync($"ALTER DATABASE {Name} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Name}");
            break;
         default:
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {Name} SYNC");
            break;
      }
   }

   public override string ToString() => $"{Server} {Name}";
}
