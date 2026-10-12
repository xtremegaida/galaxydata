using ClickHouse.Driver.ADO;
using GalaxyData.Query.HyperWhirlpool;
using Shouldly;
using Xunit;

namespace GalaxyData.Query.Tests.Connectors;

/// <summary>A query service is reached as the form says when the connection doesn't say: HTTPS, on 443 (80 over HTTP), not as a ClickHouse server is.</summary>
public sealed class HyperWhirlpoolProviderTests
{
   [Theory]
   [InlineData("Host=h", "https", 443)]
   [InlineData("Host=h;Protocol=http", "http", 80)]
   [InlineData("Host=h;Port=8100", "https", 8100)]
   [InlineData("Host=h;Protocol=http;Port=18101", "http", 18101)]
   public void AQueryServiceIsReachedOverHttpsUnlessTheConnectionSays(string connectionString, string protocol, int port)
   {
      ClickHouseConnectionStringBuilder builder = new(HyperWhirlpoolSourceProvider.WithDefaults(connectionString));
      (builder.Protocol, (int)builder.Port, builder.Host).ShouldBe((protocol, port, "h"));
   }
}
