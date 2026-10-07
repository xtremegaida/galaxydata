using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Connectors;
using GalaxyData.Query.Execution;
using Npgsql;

namespace GalaxyData.Query.PostgreSql;

/// <summary>PostgreSQL databases, connected with a connection string through a data source of the provider's making.</summary>
public sealed class PostgreSqlConnector : Connector
{
   public override SourceProvider Provider => PostgreSqlSourceProvider.Instance;

   public override ConnectionKind Kind { get; } = new PostgreSqlKind();

   public override CommandLineHelp CommandLine { get; } = new(CommandLineTargets.ConnectionString, "a connection string", "Host=localhost;Database=shop;Username=me");

   /// <summary>Connections come from a data source of the provider's making, which reads enums; disposed with the source.</summary>
   public override ValueTask<OpenedSource> OpenAsync(CommandLineSource source, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(source);
      NpgsqlDataSource dataSource = Create(source, () => PostgreSqlSourceProvider.CreateDataSource(source.Target));
      return OpenDatabaseAsync(source, dataSource.CreateConnection(), dataSource.CreateConnection, dataSource, cancellationToken);
   }
}

public sealed class PostgreSqlKind : ConnectionKind
{
   /// <summary>Sessions of read-only connections start read-only, so nothing they run writes.</summary>
   public const string ReadOnlyOption = "-c default_transaction_read_only=on";

   private static readonly string[] SslModes = ["Disable", "Allow", "Prefer", "Require", "VerifyCA", "VerifyFull"];

   public override string Id => PostgreSqlSchemaIntrospector.ProviderKind;

   public override string DisplayName => "PostgreSQL";

   public override string? RawExample => "Host=db.example.com;Database=shop;Username=reader;Password=...";

   public override IReadOnlyList<FieldDto> Fields { get; } =
   [
      new("Host", "Host", FieldType.Text) { Required = true, Group = "connection", Placeholder = "db.example.com" },
      new("Port", "Port", FieldType.Number) { Default = "5432", Group = "connection", Min = 1, Max = 65535 },
      new("Database", "Database", FieldType.Text) { Required = true, Group = "connection" },
      new("Username", "User name", FieldType.Text) { Group = "connection" },
      new("Password", "Password", FieldType.Password) { Group = "connection" },
      new("SSL Mode", "SSL", FieldType.Select)
      {
         Default = "Prefer",
         Group = "security",
         Choices = SslModes.Select(m => new ChoiceDto(m, m)).ToList(),
      },
      new("Root Certificate", "Root certificate", FieldType.FilePath)
      {
         Group = "security",
         VisibleWhen = new VisibleWhenDto("SSL Mode", ["VerifyCA", "VerifyFull"]),
         Help = "The certificate (PEM) the server's must be signed by",
      },
      new("Timeout", "Connect timeout (s)", FieldType.Number) { Default = "15", Group = "advanced", Min = 1, Max = 1024 },
      new("Command Timeout", "Command timeout (s)", FieldType.Number) { Default = "30", Group = "advanced", Min = 0 },
      new("Application Name", "Application name", FieldType.Text) { Group = "advanced", Placeholder = "GalaxyData" },
      new("Search Path", "Search path", FieldType.Text) { Group = "advanced" },
      Other,
   ];

   public override IReadOnlyList<string> PathKeywords { get; } = ["Root Certificate", "SSL Certificate", "SSL Key", "Passfile"];

   protected override DbConnectionStringBuilder NewBuilder() => new NpgsqlConnectionStringBuilder();

   protected override void Restrict(DbConnectionStringBuilder builder, bool readOnly)
   {
      if (!readOnly) { return; }
      NpgsqlConnectionStringBuilder npgsql = (NpgsqlConnectionStringBuilder)builder;
      npgsql.Options = string.IsNullOrWhiteSpace(npgsql.Options) ? ReadOnlyOption : npgsql.Options + " " + ReadOnlyOption;
   }

   /// <summary>From a data source of the provider's making, which reads enums and other types queries read.</summary>
   public override SourceConnector Connector(string connectionString) => new DataSourceConnector(PostgreSqlSourceProvider.CreateDataSource(connectionString));

   protected override string Unpooled(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ToString();

   protected override async Task<string> FoundAsync(DbConnection connection, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(connection);
      await PostgreSqlSourceProvider.Instance.PrepareConnectionAsync(connection, cancellationToken);
      object? database = await ScalarAsync(connection, "SELECT current_database()", cancellationToken);
      return $"Connected to PostgreSQL {connection.ServerVersion}, database {database}";
   }
}
