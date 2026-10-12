using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Driver.ADO;
using GalaxyData.Connectors;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.ClickHouse;
using GalaxyData.Query.Execution;

namespace GalaxyData.Query.HyperWhirlpool;

/// <summary>HyperWhirlpool warehouses, read-only, through their query service, with a query key: a connection string, as ClickHouse's.</summary>
public sealed class HyperWhirlpoolConnector : Connector
{
   public override SourceProvider Provider => HyperWhirlpoolSourceProvider.Instance;

   public override ConnectionKind Kind { get; } = new HyperWhirlpoolKind();

   public override CommandLineHelp CommandLine { get; } =
      new(CommandLineTargets.ConnectionString, "a connection string", "Host=query.hw.example.com;Port=443;Protocol=https;Database=web;Password=rk_...");

   public override ValueTask<OpenedSource> OpenAsync(CommandLineSource source, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(source);
      if (source.Writable) { throw new FormatException($"'{source.Alias}' is a HyperWhirlpool source, which can't be written"); }
      ClickHouseDataSource dataSource = Create(source, () => HyperWhirlpoolSourceProvider.CreateDataSource(source.Target));
      return OpenDatabaseAsync(source, dataSource.CreateConnection(), dataSource.CreateConnection, dataSource, cancellationToken);
   }
}

/// <summary>
/// A HyperWhirlpool warehouse's query service, which speaks ClickHouse's HTTP protocol: always read-only. Its
/// keywords are ClickHouse.Driver's, as it names them, but only those the service needs: where it is, the query key
/// (sent as the password, so the driver's <c>Password</c>), and the domain whose views are named without it (the
/// driver's <c>Database</c>). The service runs every query as its own reader, so user names, roles, sessions and
/// server settings aren't the connection's to give.
/// </summary>
public sealed class HyperWhirlpoolKind : ConnectionKind
{
   private static readonly string[] Keywords =
   [
      "Host", "Port", "Protocol", "Path", "Database", "Password", "Compression", "Timeout", ClickHouseKeywords.SkipCertificateValidation, "ReadBufferSize",
   ];

   public override string Id => HyperWhirlpoolSourceProvider.Kind;

   public override string DisplayName => "HyperWhirlpool";

   public override string Icon => "cyclone";

   public override bool AlwaysReadOnly => true;

   public override string? RawExample => "Host=query.hw.example.com;Port=443;Protocol=https;Database=web;Password=rk_...";

   public override IReadOnlyList<FieldDto> Fields { get; } =
   [
      new("Host", "Host", FieldType.Text) { Required = true, Group = "connection", Placeholder = "query.hw.example.com", Help = "The warehouse's query service" },
      new("Port", "Port", FieldType.Number) { Default = "443", Group = "connection", Min = 1, Max = 65535, Help = "The port the query service listens on; 443 with HTTPS, or 80 with HTTP, if empty" },
      new("Password", "Query key", FieldType.Password) { Required = true, Group = "connection", Placeholder = "rk_...", Help = "A query key from the warehouse's manager (Keys)" },
      new("Database", "Default domain", FieldType.Text)
      {
         Group = "connection",
         Placeholder = "web",
         Help = "The domain whose views are named without it; the views of the other domains are named with theirs",
      },
      new("Protocol", "Protocol", FieldType.Select)
      {
         Default = "https",
         Group = "security",
         Choices = [new("http", "HTTP"), new("https", "HTTPS")],
      },
      new(ClickHouseKeywords.SkipCertificateValidation, "Skip checking the server's certificate", FieldType.Bool)
      {
         Default = "false",
         Group = "security",
         VisibleWhen = new VisibleWhenDto("Protocol", ["https"]),
         Help = "Accept a certificate no trusted authority signed (a server's own); the connection is still encrypted",
      },
      new("Compression", "Compress what is sent", FieldType.Bool) { Default = "true", Group = "advanced" },
      new("Timeout", "Request timeout (s)", FieldType.Number) { Default = "120", Group = "advanced", Min = 1 },
      new("Path", "Path", FieldType.Text) { Group = "advanced", Help = "The path of the query service, behind a proxy" },
   ];

   /// <summary>No foreign keys to trust: the model's references aren't enforced, and navigations along them may find no row.</summary>
   public override IReadOnlyList<FieldDto> Options { get; } = [];

   protected override DbConnectionStringBuilder NewBuilder() => new ClickHouseConnectionStringBuilder();

   /// <summary>The keywords the query service needs, named as ClickHouse.Driver names them; others are refused, as are values it would ignore.</summary>
   public override List<KeyValuePair<string, string>> Normalize(IEnumerable<KeyValuePair<string, string>> pairs)
   {
      ArgumentNullException.ThrowIfNull(pairs);
      return base.Normalize(pairs.Select(p => ClickHouseKeywords.Canonical(p, Keywords, settings: false, DisplayName)));
   }

   public override List<KeyValuePair<string, string>> Parse(string connectionString) => Normalize(base.Parse(connectionString));

   /// <summary>Read-only, and without changes: the warehouse is built from events.</summary>
   public override SourceInfo Configure(SourceInfo source, IReadOnlyDictionary<string, string> options) =>
      base.Configure(source, options) with { IsReadOnly = true, SupportsDml = false };

   /// <summary>From a data source of the provider's making, whose connections share an HTTP client.</summary>
   public override SourceConnector Connector(string connectionString) => new DataSourceConnector(HyperWhirlpoolSourceProvider.CreateDataSource(connectionString));

   /// <summary>
   /// Opening a connection sends nothing, so the service is asked its ClickHouse's version and the default domain
   /// (which tries the key), then the model version the warehouse's schema document describes.
   /// </summary>
   protected override async Task<string> FoundAsync(DbConnection connection, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(connection);
      await HyperWhirlpoolSourceProvider.Instance.PrepareConnectionAsync(connection, cancellationToken);
      string version;
      string database;
      await using (DbCommand command = connection.CreateCommand())
      {
         command.CommandText = "SELECT version(), currentDatabase()";
         await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
         await reader.ReadAsync(cancellationToken);
         (version, database) = (reader.GetString(0), reader.GetString(1));
      }
      string? model = await HyperWhirlpoolSchemaIntrospector.LatestVersionAsync(connection, cancellationToken);
      return model == null
         ? $"Connected to HyperWhirlpool (ClickHouse {version}), default domain {database}, but the warehouse has no model version live yet"
         : $"Connected to HyperWhirlpool model {model} (ClickHouse {version}), default domain {database}";
   }
}
