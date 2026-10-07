using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Driver.ADO;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.ClickHouse;
using GalaxyData.Query.Cli;
using GalaxyData.Query.Dml;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.IntegrationTests.Execution;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.ContainerTests;

/// <summary>
/// What is ClickHouse's own: its sources are read-only; what its SQL can't express runs in the merge engine; a server
/// that can't be reached fails as a source does; its schemas are its databases; and the command line opens it.
/// </summary>
public sealed class ClickHouseTests(Servers servers)
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   private static readonly CatalogOverlay Keyed = ClickHouseShop.Keyed(CatalogOverlay.Empty, "shop", "shop");

   private static readonly IReadOnlyDictionary<string, string> NoOptions = new Dictionary<string, string>();

   private static async Task<SourceSchema> IntrospectAsync(ServerDatabase database, IntrospectionOptions? options = null)
   {
      await using DbConnection connection = database.Open();
      return await database.Provider.Introspector.IntrospectAsync(connection, options ?? IntrospectionOptions.Default, Token);
   }

   /// <summary>An engine over the shop as the application sees a ClickHouse connection: read-only, without changes.</summary>
   private async Task<(QueryEngine Engine, SourceInfo Source)> ConfiguredShopAsync()
   {
      ServerDatabase shop = await servers.ShopAsync(ServerKind.ClickHouse);
      SourceSchema schema = await IntrospectAsync(shop);
      SourceInfo source = new ClickHouseKind().Configure(new SourceInfo("shop", ClickHouseSourceProvider.Instance.ProviderKind, schema.DefaultSchema), NoOptions);
      QueryCatalog catalog = new CatalogBuilder().AddSource(source, schema).WithOverlay(Keyed).Build();
      return (new QueryEngine(catalog, new Connections(shop.Open), [ClickHouseSourceProvider.Instance]), source);
   }

   [Fact]
   public async Task ChangesAreRefusedBeforeAnyStatement()
   {
      (QueryEngine engine, SourceInfo source) = await ConfiguredShopAsync();
      (source.IsReadOnly, source.SupportsDml).ShouldBe((true, false));
      EntityDef orders = engine.Catalog.FindEntity(EntityName.Parse("shop.orders")).ShouldNotBeNull();
      DmlPlan plan = engine.PlanChanges(new ChangeSet().Add(new UpdateRow(orders, new Dictionary<string, object?> { ["id"] = 1001L }, new Dictionary<string, object?> { ["status"] = "x" })));
      plan.Success.ShouldBeFalse();
      Should.Throw<DmlScriptException>(() => engine.ParseScript(source, "INSERT INTO audit_log (message) VALUES ('x')")).Message.ShouldContain("read-only");
   }

   /// <summary>A source the application didn't configure still writes nothing: the provider refuses to open a write.</summary>
   [Fact]
   public async Task TheProviderRefusesToWrite()
   {
      ServerDatabase shop = await servers.FreshAsync(ServerKind.ClickHouse);
      await using TestSources sources = await shop.SourcesAsync();
      QueryEngine engine = sources.Engine();
      SourceInfo source = engine.Catalog.Sources.Single();
      DmlResult result = await engine.CommitAsync([engine.ParseScript(source, "INSERT INTO audit_log (message) VALUES ('x')")], Token);
      result.Success.ShouldBeFalse();
      result.Failure!.Message.ShouldContain("read-only");
      await using QueryResult count = await engine.ExecuteAsync(new QueryRequest("shop.audit_log.count()"), Token);
      (await count.ToListAsync(Token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
   }

   /// <summary>
   /// A subquery that gives a value and reads the query around it runs in the merge engine (ClickHouse's gives null for
   /// no rows), with the same rows; others run in ClickHouse whole.
   /// </summary>
   [Fact]
   public async Task WhatClickHouseCantWriteRunsInTheMergeEngine()
   {
      await using TestSources sources = await (await servers.ShopAsync(ServerKind.ClickHouse)).SourcesAsync();
      sources.Overlay = Keyed;
      await using TestSources sqlite = await TestSources.SqliteShopAsync();
      const string correlated = "shop.customers.select(name, n: orders.count(), last: orders.orderBy(desc(order_date)).firstOrDefault().status).orderBy(name)";
      const string whole = "shop.customers.where(orders.any(total > 50)).select(name).orderBy(name)";

      PreparedQuery split = sources.Engine().Prepare(correlated);
      split.Merge.ShouldNotBeNull();
      PreparedQuery single = sources.Engine().Prepare(whole);
      (single.Merge, single.Fragments.Count).ShouldBe((null, 1));
      single.Fragments[0].Statement.Text.ShouldContain("EXISTS");

      foreach (string query in new[] { correlated, whole })
      {
         await using QueryResult expected = await sqlite.Engine().ExecuteAsync(new QueryRequest(query), Token);
         await using QueryResult actual = await sources.Engine().ExecuteAsync(new QueryRequest(query), Token);
         TestSources.Format(await actual.ToListAsync(Token), actual.Schema).ShouldBe(TestSources.Format(await expected.ToListAsync(Token), expected.Schema), query);
      }
   }

   /// <summary>A server that can't be reached fails the query as a source does (a <see cref="DbException"/>), not as an HTTP error.</summary>
   [Fact]
   public async Task AServerThatCantBeReachedFailsAsASourceDoes()
   {
      ServerDatabase shop = await servers.ShopAsync(ServerKind.ClickHouse);
      SourceSchema schema = await IntrospectAsync(shop);
      await using ClickHouseDataSource nowhere = ClickHouseSourceProvider.CreateDataSource("Host=127.0.0.1;Port=1;Username=gdq;Password=x");
      QueryCatalog catalog = new CatalogBuilder().AddSource(new SourceInfo("shop", "clickhouse", schema.DefaultSchema), schema).Build();
      QueryEngine engine = new(catalog, new Connections(nowhere.CreateConnection), [ClickHouseSourceProvider.Instance]);
      QueryExecutionException failed = await Should.ThrowAsync<QueryExecutionException>(async () =>
      {
         await using QueryResult result = await engine.ExecuteAsync(new QueryRequest("shop.orders.count()"), Token);
         await result.ToListAsync(Token);
      });
      failed.InnerException.ShouldBeOfType<ClickHouseUnreachableException>().ShouldBeAssignableTo<DbException>();
      failed.Message.ShouldContain("can't be reached");
   }

   /// <summary>A source is the connection's database, its schema the default; other databases are read when they are named.</summary>
   [Fact]
   public async Task TheSourceIsTheConnectionsDatabaseUnlessOthersAreNamed()
   {
      ServerDatabase shop = await servers.ShopAsync(ServerKind.ClickHouse);
      ServerDatabase kinds = await servers.KindsAsync(ServerKind.ClickHouse);
      SourceSchema own = await IntrospectAsync(shop);
      own.DefaultSchema.ShouldBe(shop.Name);
      own.Tables.Select(t => t.Schema).Distinct().ShouldBe([shop.Name]);
      (await IntrospectAsync(shop, new IntrospectionOptions { IncludeSchemas = [shop.Name, kinds.Name] })).Tables.Select(t => t.Schema).Distinct().Order()
         .ShouldBe(new[] { shop.Name, kinds.Name }.Order());
      (await IntrospectAsync(shop, new IntrospectionOptions { ExcludeSchemas = [shop.Name] })).Tables.ShouldBeEmpty();
      (await IntrospectAsync(shop, new IntrospectionOptions { IncludeViews = false })).Tables.ShouldNotContain(t => t.Name == "open_orders");
   }

   /// <summary>The settings the language's meaning depends on are sent with every statement, so a session of its own changes nothing.</summary>
   [Fact]
   public async Task OuterJoinsGiveNullsAndAggregatesOfNoRowsAreNull()
   {
      await using TestSources sources = await (await servers.ShopAsync(ServerKind.ClickHouse)).SourcesAsync();
      sources.Overlay = Keyed;
      QueryEngine engine = sources.Engine();
      await using (QueryResult result = await engine.ExecuteAsync(new QueryRequest("shop.orders.where(id == 1004).select(id, city: ship_address.city, line: bill_address.line1)"), Token))
      {
         TestSources.Format(await result.ToListAsync(Token), result.Schema).TrimEnd().ShouldBe("1004 | null | null");
      }
      await using (QueryResult result = await engine.ExecuteAsync(new QueryRequest("shop.orders.where(total > 100000).groupBy().select(n: count(), s: sum(total), mx: max(total), a: avg(total))"), Token))
      {
         TestSources.Format(await result.ToListAsync(Token), result.Schema).TrimEnd().ShouldBe("0 | 0.00 | null | null");
      }
   }

   /// <summary>The command line opens a ClickHouse source with its connection string, read-only.</summary>
   [Fact]
   public async Task TheCommandLineOpensAClickHouseSource()
   {
      string source = "ch=clickhouse:" + (await servers.ShopAsync(ServerKind.ClickHouse)).ConnectionString;
      (int exit, string output, string error) = await GdqAsync("run", "-s", source, "--sql", "--format", "csv", "ch.orders.where(status == 'open').select(id, total).orderBy(id)");
      (exit, error).ShouldBe((0, string.Empty));
      output.ShouldContain("-- ch (ClickHouse)");
      output.ShouldContain("FROM orders AS o");
      output.ReplaceLineEndings("\n").ShouldEndWith("id,total\n1001,250.00\n1003,12.25\n");

      string script = Path.Combine(Path.GetTempPath(), $"gdq-script-{Guid.NewGuid():N}.sql");
      await File.WriteAllTextAsync(script, "INSERT INTO audit_log (message) VALUES ('x')", Token);
      try
      {
         (exit, _, error) = await GdqAsync("script", "-s", source, "-w", "ch", "--file", script, "ch");
         (exit, error.Trim()).ShouldBe((2, "gdq: 'ch' is a ClickHouse source, which can't be written"));
      }
      finally
      {
         File.Delete(script);
      }

      (exit, _, error) = await GdqAsync("run", "-s", "ch=clickhouse:ch.example.com", "ch.orders.count()");
      (exit, error.Trim()).ShouldBe((2, "gdq: The source 'ch' needs a connection string after 'clickhouse:', e.g. Host=localhost;Database=shop;Username=me"));
   }

   private static async Task<(int Exit, string Output, string Error)> GdqAsync(params string[] args)
   {
      using StringWriter output = new();
      using StringWriter error = new();
      int exit = await GdqApp.RunAsync(args, new StringReader(string.Empty), output, error, Token);
      return (exit, output.ToString(), error.ToString());
   }

   /// <summary>Connections to one database.</summary>
   private sealed class Connections(Func<DbConnection> open) : IConnectionFactory
   {
      public async ValueTask<DbConnection> OpenAsync(SourceInfo source, CancellationToken cancellationToken)
      {
         DbConnection connection = open();
         await connection.OpenAsync(cancellationToken);
         return connection;
      }
   }
}
