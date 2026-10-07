using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;
using GalaxyData.Query.IntegrationTests.Execution;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.ContainerTests;

/// <summary>
/// The conformance set on PostgreSQL, SQL Server and ClickHouse: the same rows as SQLite gives, however the engine runs
/// it. ClickHouse's shop has its keys and relations declared in the overlay, since the server keeps none.
/// </summary>
public sealed class ConformanceTests(Servers servers)
{
   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   [InlineData(ServerKind.ClickHouse)]
   public async Task TheShopReturnsTheSameRowsAsSqlite(ServerKind server)
   {
      await using TestSources sources = await (await servers.ShopAsync(server)).SourcesAsync();
      await using TestSources sqlite = await TestSources.SqliteShopAsync();
      string expected = await Conformance.RunAllAsync(sqlite);
      (await Conformance.RunAllAsync(sources, Overlay(Conformance.Overlay, server, "shop", server, "shop"))).ShouldBe(expected);
   }

   [Theory]
   [InlineData(ServerKind.Postgres)]
   [InlineData(ServerKind.SqlServer)]
   [InlineData(ServerKind.ClickHouse)]
   public async Task EverythingInTheMergeEngineReturnsTheSameRows(ServerKind server)
   {
      await using TestSources sources = await (await servers.ShopAsync(server)).SourcesAsync();
      await using TestSources sqlite = await TestSources.SqliteShopAsync();
      string expected = await Conformance.RunAllAsync(sqlite);
      (await Conformance.RunAllAsync(sources, Overlay(Conformance.Overlay, server, "shop", server, "shop"), new QueryEngineOptions { PushDown = false })).ShouldBe(expected);
      sources.Merge.ActiveSessions.ShouldBe(0);
   }

   /// <summary>Orders on one server, customers on the other: fetched by keys, in batches of two, or in full.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres, ServerKind.SqlServer, BindJoinMode.Adaptive)]
   [InlineData(ServerKind.Postgres, ServerKind.SqlServer, BindJoinMode.Always)]
   [InlineData(ServerKind.Postgres, ServerKind.SqlServer, BindJoinMode.Never)]
   [InlineData(ServerKind.SqlServer, ServerKind.Postgres, BindJoinMode.Adaptive)]
   [InlineData(ServerKind.SqlServer, ServerKind.Postgres, BindJoinMode.Always)]
   [InlineData(ServerKind.SqlServer, ServerKind.Postgres, BindJoinMode.Never)]
   [InlineData(ServerKind.ClickHouse, ServerKind.Postgres, BindJoinMode.Adaptive)]
   [InlineData(ServerKind.ClickHouse, ServerKind.Postgres, BindJoinMode.Always)]
   [InlineData(ServerKind.ClickHouse, ServerKind.Postgres, BindJoinMode.Never)]
   [InlineData(ServerKind.SqlServer, ServerKind.ClickHouse, BindJoinMode.Adaptive)]
   [InlineData(ServerKind.SqlServer, ServerKind.ClickHouse, BindJoinMode.Always)]
   public async Task SplitAcrossServersReturnsTheSameRows(ServerKind sales, ServerKind crm, BindJoinMode mode)
   {
      await using TestSources split = new();
      await (await servers.SalesAsync(sales)).AddToAsync(split, "sales");
      await (await servers.CrmAsync(crm)).AddToAsync(split, "crm");
      await using TestSources sqlite = await TestSources.SqliteShopAsync();
      string expected = await Conformance.RunAllAsync(sqlite);
      QueryEngineOptions options = new() { BindJoins = mode, MaxBindBatch = mode == BindJoinMode.Always ? 2 : 2000 };
      (await Conformance.RunAllAsync(split, Overlay(Conformance.SplitOverlay, sales, "sales", crm, "crm"), options, Conformance.Split)).ShouldBe(expected);
      split.Merge.ActiveSessions.ShouldBe(0);
   }

   /// <summary>A server and a file database together: orders in SQLite or DuckDB, customers on a server, and the other way.</summary>
   [Theory]
   [InlineData(ServerKind.Postgres, true)]
   [InlineData(ServerKind.SqlServer, true)]
   [InlineData(ServerKind.ClickHouse, true)]
   [InlineData(ServerKind.Postgres, false)]
   [InlineData(ServerKind.SqlServer, false)]
   [InlineData(ServerKind.ClickHouse, false)]
   public async Task SplitWithAFileDatabaseReturnsTheSameRows(ServerKind server, bool serverHasTheCustomers)
   {
      await using TestSources split = new();
      if (serverHasTheCustomers)
      {
         await split.AddSqliteAsync("sales", IntegrationTests.Fixtures.Sql("shop.sqlite.sql") + "\nPRAGMA foreign_keys = OFF; DROP TABLE customers; DROP TABLE addresses; DROP TABLE employees;");
         await (await servers.CrmAsync(server)).AddToAsync(split, "crm");
      }
      else
      {
         await (await servers.SalesAsync(server)).AddToAsync(split, "sales");
         await split.AddDuckDbAsync("crm", IntegrationTests.Fixtures.Sql("shop.duckdb.sql") + "\nDROP VIEW open_orders; DROP TABLE order_lines; DROP TABLE orders; DROP TABLE audit_log;");
      }
      await using TestSources sqlite = await TestSources.SqliteShopAsync();
      string expected = await Conformance.RunAllAsync(sqlite);
      CatalogOverlay overlay = Overlay(Conformance.SplitOverlay, serverHasTheCustomers ? ServerKind.Postgres : server, "sales", serverHasTheCustomers ? server : ServerKind.Postgres, "crm");
      (await Conformance.RunAllAsync(split, overlay, rewrite: Conformance.Split)).ShouldBe(expected);
   }

   /// <summary>The overlay, with the keys and relations of the shop's tables that are in ClickHouse.</summary>
   private static CatalogOverlay Overlay(CatalogOverlay overlay, ServerKind sales, string salesAlias, ServerKind crm, string crmAlias) =>
      sales != ServerKind.ClickHouse && crm != ServerKind.ClickHouse
         ? overlay
         : ClickHouseShop.Keyed(overlay, sales == ServerKind.ClickHouse ? salesAlias : null, crm == ServerKind.ClickHouse ? crmAlias : null);
}
