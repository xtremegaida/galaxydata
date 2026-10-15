using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Driver.ADO;
using GalaxyData.Query.ClickHouse;
using GalaxyData.Query.HyperWhirlpool;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.Tests.Connectors;

/// <summary>
/// The settings every statement runs with. A data source's connections share one dictionary of them (ClickHouse.Driver's),
/// which connections running statements read: preparing many connections at once, as a dashboard's widgets do, writes it
/// once and reads it after. Written as each connection was prepared, it broke, and every statement of the source after it.
/// </summary>
public sealed class ClickHouseProviderTests
{
   private static string[] SettingsOf(ClickHouseConnection connection) =>
      [.. connection.CustomSettings.Select(s => $"{s.Key}={s.Value}").Order()];

   private static readonly string[] Expected =
      [.. ClickHouseSourceProvider.Settings.Select(s => $"{s.Key}={s.Value}").Order()];

   [Fact]
   public async Task ADataSourcesConnectionsArePreparedAtOnce()
   {
      const int threads = 8;
      for (int round = 0; round < 400; round++)
      {
         await using ClickHouseDataSource dataSource = round % 2 == 0
            ? ClickHouseSourceProvider.CreateDataSource("Host=h")
            : HyperWhirlpoolSourceProvider.CreateDataSource("Host=h;Password=rk_x");
         List<ClickHouseConnection> connections = [.. Enumerable.Range(0, threads).Select(_ => (ClickHouseConnection)dataSource.CreateConnection())];
         connections.ShouldAllBe(c => ReferenceEquals(c.CustomSettings, connections[0].CustomSettings), "ClickHouse.Driver shares them");
         // A fresh data source's connections, all prepared at the same moment (as a dashboard's widgets open theirs),
         // each then reading the settings as the driver does for each statement it sends.
         using Barrier start = new(threads);
         await Task.WhenAll(connections.Select(connection => Task.Run(async () =>
         {
            start.SignalAndWait(TestContext.Current.CancellationToken);
            await ClickHouseSourceProvider.Instance.PrepareConnectionAsync(connection, TestContext.Current.CancellationToken);
            for (int read = 0; read < 50; read++)
            {
               foreach (KeyValuePair<string, object> setting in connection.CustomSettings) { setting.Value.ShouldNotBeNull(); }
            }
         }, TestContext.Current.CancellationToken)));
         SettingsOf(connections[^1]).ShouldBe(Expected, $"round {round}");
      }
   }

   [Fact]
   public async Task AConnectionIsGivenTheSettings()
   {
      await using ClickHouseConnection connection = new("Host=h;set_session_timezone=Africa/Johannesburg");
      await ClickHouseSourceProvider.Instance.PrepareConnectionAsync(connection, TestContext.Current.CancellationToken);
      SettingsOf(connection).ShouldBe(Expected, "the language's, whatever the connection said");
   }
}
