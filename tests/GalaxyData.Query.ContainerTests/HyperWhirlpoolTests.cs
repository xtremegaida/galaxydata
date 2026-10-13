using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Driver.ADO;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Dml;
using GalaxyData.Query.Execution;
using GalaxyData.Query.HyperWhirlpool;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.IntegrationTests.Execution;
using GalaxyData.Query.Providers;
using GalaxyData.Testing;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.ContainerTests;

/// <summary>
/// A HyperWhirlpool warehouse, read as its query service shows it (here, the ClickHouse server itself, which speaks the
/// same protocol): its schema is its latest schema document's, each domain a schema; queries follow the model's
/// references, across domains too, and a reference to nothing finds no row; nothing is written; and a query stopped
/// is stopped on the server, without being killed by its id.
/// </summary>
public sealed class HyperWhirlpoolTests(Servers servers) : IAsyncLifetime
{
   private static CancellationToken Token => TestContext.Current.CancellationToken;

   private HyperWhirlpoolWarehouse? warehouse;

   private HyperWhirlpoolWarehouse Warehouse => warehouse ?? throw new InvalidOperationException("The warehouse isn't made yet");

   /// <summary>A connection string to the warehouse, its web domain the default.</summary>
   private string ConnectionString => new ClickHouseConnectionStringBuilder(Servers.Admin(ServerKind.ClickHouse)) { Database = Warehouse.Web }.ToString();

   public async ValueTask InitializeAsync()
   {
      await servers.RequireAsync(ServerKind.ClickHouse);
      warehouse = await HyperWhirlpoolWarehouse.CreateAsync(() => Servers.OpenAdmin(ServerKind.ClickHouse), cancellationToken: Token);
   }

   public async ValueTask DisposeAsync()
   {
      if (warehouse != null) { await warehouse.DisposeAsync(); }
   }

   private DbConnection Open() => Servers.OpenClickHouse(ConnectionString);

   private async Task<SourceSchema> IntrospectAsync()
   {
      await using DbConnection connection = Open();
      await HyperWhirlpoolSourceProvider.Instance.PrepareConnectionAsync(connection, Token);
      return await new HyperWhirlpoolSchemaIntrospector(Warehouse.Catalog).IntrospectAsync(connection, IntrospectionOptions.Default, Token);
   }

   /// <summary>An engine over the warehouse as the application sees a HyperWhirlpool connection: read-only, without changes.</summary>
   private async Task<(QueryEngine Engine, SourceInfo Source)> EngineAsync()
   {
      SourceSchema schema = await IntrospectAsync();
      SourceInfo source = new HyperWhirlpoolKind().Configure(new SourceInfo("hw", HyperWhirlpoolSourceProvider.Kind, schema.DefaultSchema), new Dictionary<string, string>());
      QueryCatalog catalog = new CatalogBuilder().AddSource(source, schema).Build();
      return (new QueryEngine(catalog, new Connections(Open), [HyperWhirlpoolSourceProvider.Instance]), source);
   }

   private static async Task<string> RunAsync(QueryEngine engine, string query, PageRequest? paging = null)
   {
      await using QueryResult result = await engine.ExecuteAsync(new QueryRequest(query) { Paging = paging }, Token);
      return TestSources.Format(await result.ToListAsync(Token), result.Schema).ReplaceLineEndings("\n").TrimEnd();
   }

   [Fact]
   public async Task TheSchemaIsTheLatestDocuments()
   {
      SourceSchema schema = await IntrospectAsync();
      schema.ProviderKind.ShouldBe("hyperwhirlpool");
      schema.ServerVersion.ShouldNotBeNull().ShouldStartWith($"HyperWhirlpool model {HyperWhirlpoolWarehouse.Version}, ClickHouse ");
      schema.DefaultSchema.ShouldBe(Warehouse.Web);
      schema.Tables.Select(t => $"{t.Schema}.{t.Name}").ShouldBe(
         [$"{Warehouse.Crm}.contact", $"{Warehouse.Web}.device", $"{Warehouse.Web}.link_device_contact", $"{Warehouse.Web}.page_view_events", $"{Warehouse.Web}.session"]);
      TableSchema events = schema.Tables.Single(t => t.Name == "page_view_events");
      events.PrimaryKey.ShouldNotBeNull().Columns.ShouldBe(["event_id"]);
      events.ForeignKeys.Select(f => $"{f.Name}: {f.RefSchema}.{f.RefTable} {f.IsEnforced}").ShouldBe(
         [$"crm_contact: {Warehouse.Crm}.contact False", $"device: {Warehouse.Web}.device False", $"session: {Warehouse.Web}.session False"]);
   }

   /// <summary>
   /// A login that may read only the warehouse's views and catalog (as the query service's reader may) finds the
   /// catalog itself, whatever the warehouse names it.
   /// </summary>
   [Fact]
   public async Task AReaderFindsTheCatalogItMayRead()
   {
      string reader = "gdq_hw_reader_" + Guid.NewGuid().ToString("N")[..8];
      string password = Guid.NewGuid().ToString("N");
      await using DbConnection admin = Servers.OpenAdmin(ServerKind.ClickHouse);
      await admin.OpenAsync(Token);
      await admin.ExecuteAsync($"CREATE USER {reader} IDENTIFIED BY '{password}'", Token);
      try
      {
         await admin.ExecuteAsync($"GRANT SELECT ON {Warehouse.Web}.* TO {reader}", Token);
         await admin.ExecuteAsync($"GRANT SELECT ON {Warehouse.Crm}.* TO {reader}", Token);
         await admin.ExecuteAsync($"GRANT SELECT ON {Warehouse.Catalog}.* TO {reader}", Token);
         string connectionString = new ClickHouseConnectionStringBuilder(Servers.Admin(ServerKind.ClickHouse))
         {
            Database = Warehouse.Web,
            Username = reader,
            Password = password,
         }.ToString();
         await using DbConnection connection = Servers.OpenClickHouse(connectionString);
         await HyperWhirlpoolSourceProvider.Instance.PrepareConnectionAsync(connection, Token);
         SourceSchema schema = await HyperWhirlpoolSourceProvider.Instance.Introspector.IntrospectAsync(connection, IntrospectionOptions.Default, Token);
         schema.Tables.Count.ShouldBe(5);
         schema.ServerVersion.ShouldNotBeNull().ShouldStartWith($"HyperWhirlpool model {HyperWhirlpoolWarehouse.Version}");
      }
      finally
      {
         await admin.ExecuteAsync($"DROP USER IF EXISTS {reader}", Token);
      }
   }

   [Fact]
   public async Task QueriesFollowTheModelsReferencesAcrossDomains()
   {
      (QueryEngine engine, _) = await EngineAsync();
      // Both domains are databases of the one server: a query across them is sent whole.
      PreparedQuery across = engine.Prepare("hw.page_view_events.where(crm_contact.lifecycle == 'student').select(url)");
      (across.Merge, across.Fragments.Count).ShouldBe((null, 1));
      across.Fragments[0].Statement.Text.ShouldContain(Warehouse.Crm);
      (await RunAsync(engine, "hw.page_view_events.where(session.traffic_type == 'search').count()")).ShouldBe("3");
      (await RunAsync(engine, "hw.page_view_events.where(crm_contact.lifecycle == 'student').select(url).orderBy(url)"))
         .ShouldBe("'https://example.ac.za/'\n'https://example.ac.za/apply'\n'https://example.ac.za/fees'");
      // Events without a contact refer to '', which no contact has.
      (await RunAsync(engine, "hw.page_view_events.groupBy(lifecycle: crm_contact.lifecycle).select(lifecycle, n: count()).orderBy(n, lifecycle)"))
         .ShouldBe("'applicant' | 1\nnull | 2\n'student' | 3");
      (await RunAsync(engine, "hw.page_view_events.groupBy(month: startOfMonth(ts)).select(month, views: count(), visits: countDistinct(session_id)).orderBy(month)"))
         .ShouldBe("2026-01-01 | 3 | 1\n2026-02-01 | 3 | 2");
      (await RunAsync(engine, "hw.link_device_contact.select(browser: device.browser, country: crm_contact.country, evidence).orderBy(browser)"))
         .ShouldBe("'Chrome' | 'KE' | 1\n'Firefox' | 'ZA' | 3");
      (await RunAsync(engine, "hw.session.where(device.browser == 'Firefox').groupBy(traffic_type).select(traffic_type, pages: sum(page_views)).orderBy(traffic_type)"))
         .ShouldBe("'direct' | 1\n'search' | 3");
   }

   /// <summary>
   /// A grid's page of a view is found by its key first, then joined to the rows it refers to (their display values)
   /// with the page on the side ClickHouse builds the join from: the rows the whole query has there, in its order.
   /// </summary>
   [Fact]
   public async Task PagesAreFoundByTheirKeysAndJoinedToTheRowsTheyReferTo()
   {
      (QueryEngine engine, _) = await EngineAsync();
      const string Displays = ".extend(_display0: session.traffic_type, _display1: device.browser, _display2: crm_contact.country)";
      QueryReport report = new();
      foreach ((string query, PageRequest page) in new[]
      {
         // As a grid opens: in the key's order.
         ("hw.page_view_events" + Displays, new PageRequest(2, 3)),
         ("hw.page_view_events.where(url != '').orderBy(desc(ts))" + Displays, new PageRequest(1, 3)),
      })
      {
         string sql = engine.Prepare(new QueryRequest(query) { Paging = page }).Fragments.ShouldHaveSingleItem().Statement.Text;
         sql.ShouldContain(".event_id IN (");
         sql.ShouldContain("RIGHT JOIN");
         report.Case($"{query}, rows {page.Offset + 1} to {page.Offset + page.Limit}", await RunAsync(engine, query, page));
      }
      Golden.Match(report.ToString());
      // The sorted page is those rows of the whole query.
      string sorted = "hw.page_view_events.where(url != '').orderBy(desc(ts))" + Displays;
      (await RunAsync(engine, sorted, new PageRequest(1, 3))).ShouldBe(string.Join("\n", (await RunAsync(engine, sorted)).Split('\n').Skip(1).Take(3)));
   }

   [Fact]
   public async Task NothingIsWritten()
   {
      (QueryEngine engine, SourceInfo source) = await EngineAsync();
      (source.IsReadOnly, source.SupportsDml).ShouldBe((true, false));
      EntityDef session = engine.Catalog.FindEntity(EntityName.Parse("hw.session")).ShouldNotBeNull();
      DmlPlan plan = engine.PlanChanges(new ChangeSet().Add(new UpdateRow(session, new Dictionary<string, object?> { ["session_id"] = Guid.Empty },
         new Dictionary<string, object?> { ["traffic_type"] = "x" })));
      plan.Success.ShouldBeFalse();
      await using DbConnection connection = Open();
      await Should.ThrowAsync<NotSupportedException>(async () => await HyperWhirlpoolSourceProvider.Instance.PrepareWriteAsync(connection, source, Token));
   }

   /// <summary>
   /// A statement stopped is stopped on the server too, though it isn't killed by its id (the query service runs no
   /// <c>KILL</c>): its request is dropped, and ClickHouse stops a read whose client has gone.
   /// </summary>
   [Fact]
   public async Task AStoppedQueryStopsOnTheServer()
   {
      await using DbConnection connection = Open();
      await connection.OpenAsync(Token);
      await HyperWhirlpoolSourceProvider.Instance.PrepareConnectionAsync(connection, Token);
      await using DbCommand command = connection.CreateCommand();
      command.CommandText = "SELECT sum(number) FROM system.numbers_mt";
      HyperWhirlpoolSourceProvider.Instance.PrepareCommand(command);
      string id = ((ClickHouseCommand)command).QueryId!;
      Task running = command.ExecuteScalarAsync(Token);
      await using DbConnection admin = Servers.OpenAdmin(ServerKind.ClickHouse);
      await admin.OpenAsync(Token);
      try
      {
         (await RunningAsync(admin, id, expected: true)).ShouldBeTrue("the query started");
         HyperWhirlpoolSourceProvider.Instance.CancelCommand(command);
         await Should.ThrowAsync<Exception>(running);
         (await RunningAsync(admin, id, expected: false)).ShouldBeFalse("the query stopped on the server");
      }
      finally
      {
         await admin.ExecuteAsync("KILL QUERY WHERE query_id = {id:String} ASYNC", Token, ("id", id));
      }
   }

   /// <summary>Whether the query is running, once it is as <paramref name="expected"/> or ten seconds have passed.</summary>
   private static async Task<bool> RunningAsync(DbConnection admin, string id, bool expected)
   {
      for (int i = 0; i < 50; i++)
      {
         bool running = await admin.ScalarTextAsync("SELECT count() FROM system.processes WHERE query_id = {id:String}", Token, ("id", id)) != "0";
         if (running == expected) { return running; }
         await Task.Delay(200, Token);
      }
      return !expected;
   }

   /// <summary>Connections to the warehouse.</summary>
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
