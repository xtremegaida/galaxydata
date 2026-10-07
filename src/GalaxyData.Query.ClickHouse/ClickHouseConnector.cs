using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Driver.ADO;
using GalaxyData.Connectors;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;

namespace GalaxyData.Query.ClickHouse;

/// <summary>ClickHouse servers, read-only, connected over HTTP with a connection string, through a data source of the provider's making.</summary>
public sealed class ClickHouseConnector : Connector
{
   public override SourceProvider Provider => ClickHouseSourceProvider.Instance;

   public override ConnectionKind Kind { get; } = new ClickHouseKind();

   public override CommandLineHelp CommandLine { get; } = new(CommandLineTargets.ConnectionString, "a connection string", "Host=localhost;Database=shop;Username=me");

   public override ValueTask<OpenedSource> OpenAsync(CommandLineSource source, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(source);
      if (source.Writable) { throw new FormatException($"'{source.Alias}' is a ClickHouse source, which can't be written"); }
      ClickHouseDataSource dataSource = Create(source, () => ClickHouseSourceProvider.CreateDataSource(source.Target));
      return OpenDatabaseAsync(source, dataSource.CreateConnection(), dataSource.CreateConnection, dataSource, cancellationToken);
   }
}

/// <summary>
/// A ClickHouse server, over HTTP: always read-only. Its keywords are ClickHouse.Driver's, as it names them, and a
/// server's settings, prefixed <c>set_</c>; those the language's meaning depends on (<see cref="ClickHouseSourceProvider.Settings"/>)
/// are the application's to set, as are sessions, which would make a source's statements wait for each other.
/// </summary>
public sealed class ClickHouseKind : ConnectionKind
{
   public const string SkipCertificateValidation = "SkipServerCertificateValidation";

   private const string SettingPrefix = "set_";

   /// <summary>The keywords ClickHouse.Driver takes, as it names them.</summary>
   private static readonly string[] Keywords =
   [
      "Host", "Port", "Database", "Username", "Password", "Protocol", "Path", "Compression", "Timeout", SkipCertificateValidation, "UseSession",
      "SessionId", "UseCustomDecimals", "UseFormDataParameters", "ReadStringsAsByteArrays", "ReadBufferSize", "Roles", "JsonReadMode",
      "JsonWriteMode", "MapReadMode", "AcceptEncoding", "AllowDuplicateJsonKeys",
   ];

   private static readonly string[] Flags = ["Compression", SkipCertificateValidation, "UseSession", "UseCustomDecimals", "UseFormDataParameters", "ReadStringsAsByteArrays", "AllowDuplicateJsonKeys"];

   public override string Id => ClickHouseSchemaIntrospector.ProviderKind;

   public override string DisplayName => "ClickHouse";

   public override bool AlwaysReadOnly => true;

   public override string? RawExample => "Host=ch.example.com;Port=8443;Protocol=https;Database=analytics;Username=reader;Password=...";

   public override IReadOnlyList<FieldDto> Fields { get; } =
   [
      new("Host", "Host", FieldType.Text) { Required = true, Group = "connection", Placeholder = "ch.example.com" },
      new("Port", "Port (HTTP)", FieldType.Number) { Default = "8123", Group = "connection", Min = 1, Max = 65535, Help = "ClickHouse's HTTP port: 8123, or 8443 with HTTPS" },
      new("Database", "Database", FieldType.Text) { Group = "connection", Placeholder = "default" },
      new("Username", "User name", FieldType.Text) { Group = "connection", Placeholder = "default" },
      new("Password", "Password", FieldType.Password) { Group = "connection" },
      new("Protocol", "Protocol", FieldType.Select)
      {
         Default = "http",
         Group = "security",
         Choices = [new("http", "HTTP"), new("https", "HTTPS")],
      },
      new(SkipCertificateValidation, "Skip checking the server's certificate", FieldType.Bool)
      {
         Default = "false",
         Group = "security",
         VisibleWhen = new VisibleWhenDto("Protocol", ["https"]),
         Help = "Accept a certificate no trusted authority signed (a server's own); the connection is still encrypted",
      },
      new("Compression", "Compress what is sent", FieldType.Bool) { Default = "true", Group = "advanced" },
      new("Timeout", "Request timeout (s)", FieldType.Number) { Default = "120", Group = "advanced", Min = 1 },
      new("Path", "Path", FieldType.Text) { Group = "advanced", Help = "The path of the server's HTTP interface, behind a proxy" },
      Other with { Help = "Any other keyword ClickHouse.Driver takes, or a server setting prefixed set_ (set_max_threads=4)" },
   ];

   /// <summary>No foreign keys to trust: relations come from the overlay.</summary>
   public override IReadOnlyList<FieldDto> Options { get; } = [];

   /// <summary>The settings the language's meaning depends on, and sessions, which make a source's statements wait for each other.</summary>
   protected override IReadOnlyList<string> Reserved { get; } =
      [.. ClickHouseSourceProvider.Settings.Keys.Select(s => SettingPrefix + s), "UseSession", "SessionId", "ReadStringsAsByteArrays"];

   protected override DbConnectionStringBuilder NewBuilder() => new ClickHouseConnectionStringBuilder();

   /// <summary>
   /// ClickHouse.Driver's builder keeps keywords as written and takes any: they are named as it names them, settings
   /// keep their names, others are refused, and values it would ignore are refused too.
   /// </summary>
   public override List<KeyValuePair<string, string>> Normalize(IEnumerable<KeyValuePair<string, string>> pairs)
   {
      ArgumentNullException.ThrowIfNull(pairs);
      return base.Normalize(pairs.Select(Canonical));
   }

   public override List<KeyValuePair<string, string>> Parse(string connectionString) => Normalize(base.Parse(connectionString));

   private static KeyValuePair<string, string> Canonical(KeyValuePair<string, string> pair)
   {
      string key = pair.Key.Trim();
      if (key.StartsWith(SettingPrefix, StringComparison.OrdinalIgnoreCase) && key.Length > SettingPrefix.Length)
      {
         return new(SettingPrefix + key[SettingPrefix.Length..], pair.Value);
      }
      string keyword = Keywords.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase))
         ?? throw new ArgumentException($"ClickHouse connections take no keyword '{key}' (a server setting is written set_{key})");
      string value = pair.Value.Trim();
      bool valid = keyword switch
      {
         "Port" => ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ushort port) && port > 0,
         "Timeout" or "ReadBufferSize" => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0,
         "Protocol" => value.Equals("http", StringComparison.OrdinalIgnoreCase) || value.Equals("https", StringComparison.OrdinalIgnoreCase),
         _ when Flags.Contains(keyword) => bool.TryParse(value, out _),
         _ => true,
      };
      if (!valid) { throw new ArgumentException($"'{pair.Value}' isn't a value {keyword} takes"); }
      return new(keyword, keyword == "Protocol" ? value.ToLowerInvariant() : pair.Value);
   }

   /// <summary>Always read-only, whatever the connection says: nothing a session runs writes.</summary>
   protected override void Restrict(DbConnectionStringBuilder builder, bool readOnly)
   {
      ArgumentNullException.ThrowIfNull(builder);
      builder[SettingPrefix + "readonly"] = "2";
   }

   /// <summary>Read-only, and without changes: ClickHouse has no transactions to write them in.</summary>
   public override SourceInfo Configure(SourceInfo source, IReadOnlyDictionary<string, string> options) =>
      base.Configure(source, options) with { IsReadOnly = true, SupportsDml = false };

   /// <summary>From a data source of the provider's making, whose connections share an HTTP client.</summary>
   public override SourceConnector Connector(string connectionString) => new DataSourceConnector(ClickHouseSourceProvider.CreateDataSource(connectionString));

   /// <summary>
   /// Opening a connection sends nothing, so the server is asked its version and database, with the settings queries
   /// send (a login whose profile is read-only may not change them).
   /// </summary>
   protected override async Task<string> FoundAsync(DbConnection connection, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(connection);
      await ClickHouseSourceProvider.Instance.PrepareConnectionAsync(connection, cancellationToken);
      await using DbCommand command = connection.CreateCommand();
      command.CommandText = "SELECT version(), currentDatabase()";
      await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
      await reader.ReadAsync(cancellationToken);
      return $"Connected to ClickHouse {reader.GetString(0)}, database {reader.GetString(1)}";
   }
}
